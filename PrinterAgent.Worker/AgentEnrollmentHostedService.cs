using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Infrastructure.Networking;
using PrinterAgent.Worker.Config;

namespace PrinterAgent.Worker;

/// <summary>
/// Înainte de <see cref="AgentWorker"/>: încarcă sau obține sesiune (enroll cu cod dacă e nevoie).
/// </summary>
public sealed class AgentEnrollmentHostedService : IHostedService
{
    private static readonly Random Jitter = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAgentSessionRenewalService _sessionRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IBackendClient _backendClient;
    private readonly IOptions<WireGuardOptions> _wireGuardOptions;
    private readonly IWireGuardTunnelManager _wireGuardTunnelManager;
    private readonly ILogger<AgentEnrollmentHostedService> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public AgentEnrollmentHostedService(
        IHttpClientFactory httpClientFactory,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAppConfiguration appConfiguration,
        IBackendClient backendClient,
        IOptions<WireGuardOptions> wireGuardOptions,
        IWireGuardTunnelManager wireGuardTunnelManager,
        ILogger<AgentEnrollmentHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _appConfiguration = appConfiguration;
        _backendClient = backendClient;
        _wireGuardOptions = wireGuardOptions;
        _wireGuardTunnelManager = wireGuardTunnelManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Enrollment must retry when config changes; many installs start the service before the operator saves EnrollmentCode.
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunEnrollmentLoopAsync(_loopCts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _loopCts?.Cancel();
        }
        catch { }

        if (_loopTask != null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task RunEnrollmentLoopAsync(CancellationToken cancellationToken)
    {
        var warnedMissingCode = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);

                if (_sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
                {
                    // Already enrolled and access token is good.
                    return;
                }

                _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                if (_sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
                {
                    return;
                }

                var code = _appConfiguration.EnrollmentCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    if (!warnedMissingCode)
                    {
                        warnedMissingCode = true;
                        if (CanContinueWithRefreshOnly())
                        {
                            _logger.LogWarning(
                                "EnrollmentCode is missing in agent.json, but AgentId and refresh token exist in session — continuing startup; heartbeat will retry refresh.");
                            return;
                        }

                        _logger.LogWarning(
                            "EnrollmentCode is missing in agent.json — waiting for it to be saved (Configurator) to enroll. No service restart is required.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                warnedMissingCode = false;
                var ok = await TryEnrollOnceAsync(code, cancellationToken).ConfigureAwait(false);
                if (ok)
                    return;

                // Enrollment failed but did not throw; wait and retry (covers transient errors and 401 when code was wrong).
                // 429 backoff is handled inside TryEnrollOnceAsync via a local delay.
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enrollment loop error; will retry.");
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryEnrollOnceAsync(string code, CancellationToken cancellationToken)
    {
        var instanceId = _sessionStore.GetOrCreateClientInstanceId(cancellationToken);
        var client = _httpClientFactory.CreateClient("PrinterAgentEnroll");

        using var response = await client.PostAsJsonAsync(
                "api/agents/enroll",
                new EnrollRequestBody { EnrollmentCode = code, ClientInstanceId = instanceId },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = Compute429Backoff(response);
            _logger.LogWarning(
                "Enrollment rejected (429); backing off for {DelaySeconds:F0}s then retrying.",
                delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (CanContinueWithRefreshOnly())
                {
                    _logger.LogWarning(
                        "Enrollment rejected (401): {Body}. Refresh token exists; continuing without re-enrollment (heartbeat will keep refreshing).",
                        err);
                    return true;
                }

                _logger.LogWarning(
                    "Enrollment rejected (401): {Body}. The code may be invalid/expired or issued for a different backend/DB/pepper. Waiting for a new code.",
                    err);
                return false;
            }

            _logger.LogWarning("Enrollment failed ({Status}): {Body}", (int)response.StatusCode, err);
            return false;
        }

        var payload = await response.Content.ReadFromJsonAsync<EnrollResponseBody>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.AccessToken)
            || string.IsNullOrWhiteSpace(payload.AgentId)
            || string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            _logger.LogWarning("Invalid enrollment response; will retry.");
            return false;
        }

        await _sessionStore.SaveSessionAsync(
                payload.AgentId,
                payload.AccessToken,
                payload.RefreshToken,
                payload.RestaurantId,
                payload.ExpiresAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Enrollment succeeded for agentId {AgentId}.", payload.AgentId);

        await TryProvisionWireGuardConfAsync(payload.AgentId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static TimeSpan Compute429Backoff(HttpResponseMessage response)
    {
        // Prefer server-provided Retry-After when present.
        try
        {
            var ra = response.Headers.RetryAfter;
            if (ra?.Delta != null)
                return Clamp(ra.Delta.Value);
            if (ra?.Date != null)
            {
                var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                    return Clamp(delta);
            }
        }
        catch
        {
            // ignore header parse issues
        }

        // Fallback: modest randomized delay to avoid stampeding.
        var seconds = 30 + Jitter.Next(0, 30); // 30–59s
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(5))
            return TimeSpan.FromSeconds(5);
        if (value > TimeSpan.FromMinutes(5))
            return TimeSpan.FromMinutes(5);
        return value;
    }

    private async Task TryProvisionWireGuardConfAsync(string agentId, CancellationToken cancellationToken)
    {
        try
        {
            var opt = _wireGuardOptions.Value;
            if (!opt.Enabled)
                return;

            var path = opt.ConfigFilePath?.Trim();
            if (string.IsNullOrWhiteSpace(path))
                return;

            var conf = await _backendClient.GetWireGuardConfAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(conf))
            {
                _logger.LogWarning("WireGuard provisioning: backend did not return a .conf for agentId {AgentId}.", agentId);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
            if (string.Equals(existing, conf, StringComparison.Ordinal))
                return;

            await File.WriteAllTextAsync(path, conf, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("WireGuard provisioning: wrote config to {Path}.", path);

            if (opt.InstallTunnelServiceIfMissing)
            {
                // Installing the tunnel service requires admin. The agent runs as a Windows service (LocalSystem),
                // so this should work as long as WireGuard for Windows is installed.
                await TryInstallTunnelServiceAsync(opt, path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WireGuard provisioning: failed to download/write .conf; continuing without blocking enrollment.");
        }
    }

    private async Task TryInstallTunnelServiceAsync(WireGuardOptions opt, string confPath, CancellationToken cancellationToken)
    {
        try
        {
            var tunnelName = opt.TunnelName?.Trim();
            if (string.IsNullOrWhiteSpace(tunnelName))
                tunnelName = Path.GetFileNameWithoutExtension(confPath);

            var serviceName = string.IsNullOrWhiteSpace(opt.WindowsTunnelServiceName)
                ? $"WireGuardTunnel${tunnelName}"
                : opt.WindowsTunnelServiceName.Trim();

            if (_wireGuardTunnelManager.ServiceExists(serviceName))
            {
                _logger.LogInformation("WireGuard provisioning: tunnel service {Service} already exists.", serviceName);
                return;
            }

            _logger.LogInformation(
                "WireGuard provisioning: installing tunnel service {Service} from {Path}.",
                serviceName,
                confPath);

            await _wireGuardTunnelManager.InstallTunnelServiceAsync(confPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WireGuard provisioning: failed to install tunnel service from {Path}.", confPath);
        }
    }

    /// <summary>
    /// Dacă enroll nu mai poate reuși (ex. cod consumat), dar avem încă refresh, lăsăm hostul pornit:
    /// Heartbeat reaplează refresh la fiecare ciclu.
    /// </summary>
    private bool CanContinueWithRefreshOnly() =>
        !string.IsNullOrWhiteSpace(_sessionStore.AgentId)
        && !string.IsNullOrWhiteSpace(_sessionStore.RefreshToken);

    private sealed class EnrollRequestBody
    {
        [JsonPropertyName("enrollmentCode")]
        public string EnrollmentCode { get; set; } = string.Empty;

        [JsonPropertyName("clientInstanceId")]
        public Guid ClientInstanceId { get; set; }
    }

    private sealed class EnrollResponseBody
    {
        [JsonPropertyName("agentId")]
        public string AgentId { get; set; } = string.Empty;

        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("restaurantId")]
        public string RestaurantId { get; set; } = string.Empty;

        [JsonPropertyName("expiresAtUtc")]
        public DateTime ExpiresAtUtc { get; set; }
    }
}
