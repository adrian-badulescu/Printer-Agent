using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Worker;

/// <summary>
/// Înainte de <see cref="AgentWorker"/>: încarcă sau obține sesiune (enroll cu cod dacă e nevoie).
/// </summary>
public sealed class AgentEnrollmentHostedService : IHostedService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAgentSessionRenewalService _sessionRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<AgentEnrollmentHostedService> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public AgentEnrollmentHostedService(
        IHttpClientFactory httpClientFactory,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAppConfiguration appConfiguration,
        ILogger<AgentEnrollmentHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _appConfiguration = appConfiguration;
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
                    return;

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
            _logger.LogWarning("Enrollment rejected (429); will retry later.");
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
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
        return true;
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
