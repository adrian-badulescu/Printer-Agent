using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using PrinterAgent.Infrastructure.Networking;
using PrinterAgent.Infrastructure.Redis;
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
    private readonly IAgentDeviceRenewalService _deviceRenewal;
    private readonly IDeviceCredentialStore _deviceCredentialStore;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IBackendClient _backendClient;
    private readonly IRedisRuntimeCredentials _redisRuntimeCredentials;
    private readonly IRedisConnectionMultiplexerHolder _redisHolder;
    private readonly IOptions<WireGuardOptions> _wireGuardOptions;
    private readonly IWireGuardTunnelManager _wireGuardTunnelManager;
    private readonly ILogger<AgentEnrollmentHostedService> _logger;
    private bool _loggedOperationalRedisReady;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _wireGuardBgCts;
    private Task? _wireGuardBackgroundTask;

    public AgentEnrollmentHostedService(
        IHttpClientFactory httpClientFactory,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAgentDeviceRenewalService deviceRenewal,
        IDeviceCredentialStore deviceCredentialStore,
        IAppConfiguration appConfiguration,
        IBackendClient backendClient,
        IRedisRuntimeCredentials redisRuntimeCredentials,
        IRedisConnectionMultiplexerHolder redisHolder,
        IOptions<WireGuardOptions> wireGuardOptions,
        IWireGuardTunnelManager wireGuardTunnelManager,
        ILogger<AgentEnrollmentHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _deviceRenewal = deviceRenewal;
        _deviceCredentialStore = deviceCredentialStore;
        _appConfiguration = appConfiguration;
        _backendClient = backendClient;
        _redisRuntimeCredentials = redisRuntimeCredentials;
        _redisHolder = redisHolder;
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

        try
        {
            _wireGuardBgCts?.Cancel();
        }
        catch { }

        if (_wireGuardBackgroundTask != null)
        {
            try
            {
                await _wireGuardBackgroundTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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
                    if (await TryFinishAndStayAliveAsync(cancellationToken).ConfigureAwait(false))
                        continue;
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                if (_sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
                {
                    if (await TryFinishAndStayAliveAsync(cancellationToken).ConfigureAwait(false))
                        continue;
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _ = await _deviceRenewal.TryRenewWithDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
                if (_sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
                {
                    if (await TryFinishAndStayAliveAsync(cancellationToken).ConfigureAwait(false))
                        continue;
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var code = _appConfiguration.EnrollmentCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    if (await CanContinueWithoutEnrollmentCodeAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!warnedMissingCode)
                        {
                            warnedMissingCode = true;
                            _logger.LogWarning(
                                "EnrollmentCode is missing in agent.json, but session refresh or device credential can recover — continuing startup.");
                        }

                        if (await TryFinishAndStayAliveAsync(cancellationToken).ConfigureAwait(false))
                            continue;
                    }
                    else if (!warnedMissingCode)
                    {
                        warnedMissingCode = true;
                        _logger.LogWarning(
                            "EnrollmentCode is missing in agent.json — waiting for it to be saved (Configurator) to enroll. No service restart is required.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                warnedMissingCode = false;
                var ok = await TryEnrollOnceAsync(code, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    if (await TryFinishAndStayAliveAsync(cancellationToken).ConfigureAwait(false))
                        continue;
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

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

    /// <summary>When operational, idle instead of exiting so re-enroll works after session loss (e.g. agent removed in Manager UI).</summary>
    private async Task<bool> TryFinishAndStayAliveAsync(CancellationToken cancellationToken)
    {
        if (!await TryFinishWireGuardSetupAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return true;
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
                        "Enrollment rejected (401): {Body}. Refresh token exists; continuing without re-enrollment.",
                        err);
                    return true;
                }

                if (await CanContinueWithoutEnrollmentCodeAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Enrollment rejected (401): {Body}. Device credential exists; continuing without re-enrollment.",
                        err);
                    return true;
                }

                _logger.LogWarning(
                    "Enrollment rejected (401): {Body}. Generate a **new** enrollment code in Manager UI (codes are single-use) and save it in Configurator.",
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

        if (!string.IsNullOrWhiteSpace(payload.DeviceCredential))
        {
            await _deviceCredentialStore.SaveAsync(payload.AgentId, payload.DeviceCredential, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Enrollment succeeded for agentId {AgentId}.", payload.AgentId);

        await TryProvisionWireGuardConfAsync(payload.AgentId, cancellationToken).ConfigureAwait(false);
        // Always re-fetch after enroll: removing the last agent revokes DB + Redis ACL; a stale
        // redis.credentials.json would otherwise skip GET /redis-credentials and leave DB empty.
        await TryProvisionRedisCredentialsIfNeededAsync(cancellationToken, forceRefresh: true).ConfigureAwait(false);
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

    /// <summary>
    /// Returns true when enrollment loop can exit (WireGuard disabled or conf + tunnel service are ready).
    /// </summary>
    private async Task<bool> TryFinishWireGuardSetupAsync(CancellationToken cancellationToken)
    {
        _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);
        await TryProvisionWireGuardIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await TryProvisionRedisCredentialsIfNeededAsync(cancellationToken).ConfigureAwait(false);

        var redisReady = IsRedisAuthReady();
        var wgReady = IsWireGuardInfrastructureReady();
        var sessionReady = _sessionStore.HasUsableSession(TimeSpan.FromMinutes(5));

        if (redisReady && sessionReady)
        {
            if (!wgReady)
            {
                if (!_loggedOperationalRedisReady)
                {
                    _loggedOperationalRedisReady = true;
                    _logger.LogInformation(
                        "Agent operationally ready for print (session + Redis). WireGuard tunnel still provisioning — required at restaurant sites without another VPN route to {RedisHost}.",
                        _appConfiguration.RedisConnectionSummary);
                }

                StartWireGuardBackgroundProvisioningIfNeeded(cancellationToken);
            }

            return true;
        }

        {
            var opt = _wireGuardOptions.Value;
            var path = opt.ConfigFilePath?.Trim() ?? string.Empty;
            _logger.LogWarning(
                "Agent startup not ready. WireGuardReady={WireGuardReady} RedisReady={RedisReady} SessionReady={SessionReady} WireGuardPath={Path} LegacyBundledRedisPassword={Legacy}. Will retry in 30s.",
                wgReady,
                redisReady,
                sessionReady,
                path,
                _appConfiguration.HasLegacyRedisPassword);
        }

        return false;
    }

    private void StartWireGuardBackgroundProvisioningIfNeeded(CancellationToken parentCancellationToken)
    {
        var opt = _wireGuardOptions.Value;
        if (!opt.Enabled)
            return;

        if (IsWireGuardInfrastructureReady())
            return;

        if (_wireGuardBackgroundTask is { IsCompleted: false })
            return;

        _wireGuardBgCts?.Cancel();
        _wireGuardBgCts = CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken);
        _wireGuardBackgroundTask = RunWireGuardBackgroundAsync(_wireGuardBgCts.Token);
    }

    private async Task RunWireGuardBackgroundAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WireGuard: background provisioning started (retries until tunnel service is ready).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (!_sessionStore.HasUsableSession(TimeSpan.FromMinutes(5)))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await TryProvisionWireGuardIfNeededAsync(cancellationToken).ConfigureAwait(false);

                if (IsWireGuardInfrastructureReady())
                {
                    _logger.LogInformation("WireGuard: background provisioning complete — tunnel infrastructure is ready.");
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WireGuard: background provisioning iteration failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool IsRedisAuthReady()
    {
        if (_redisRuntimeCredentials.HasCredentials)
            return true;

        return _appConfiguration.HasLegacyRedisPassword;
    }

    private async Task TryProvisionRedisCredentialsIfNeededAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        try
        {
            await _redisRuntimeCredentials.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!forceRefresh && _redisRuntimeCredentials.HasCredentials)
            {
                return;
            }

            if (_appConfiguration.HasLegacyRedisPassword)
            {
                _logger.LogInformation(
                    "Redis: using legacy MSI password from install-dir agent.json (per-restaurant credentials not fetched). To use ACL, set Redis.Password to empty in %ProgramData%\\URSPrinterAgent\\agent.json or clear it in Program Files\\URSPrinterAgent\\agent.json.");
                return;
            }

            var agentId = _sessionStore.AgentId;
            if (string.IsNullOrWhiteSpace(agentId))
                return;

            var creds = await _backendClient.GetRedisCredentialsAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (creds == null
                || string.IsNullOrWhiteSpace(creds.Host)
                || string.IsNullOrWhiteSpace(creds.User)
                || string.IsNullOrWhiteSpace(creds.Password))
            {
                _logger.LogWarning(
                    "Redis credentials: backend did not return credentials for agentId {AgentId}. Check API logs for 'Could not provision Redis ACL user' (App VPS → Redis VPS ACL).",
                    agentId);
                return;
            }

            await _redisRuntimeCredentials.SaveAsync(
                    new RedisRuntimeCredentialsPayload
                    {
                        Host = creds.Host.Trim(),
                        Port = creds.Port > 0 ? creds.Port : 6379,
                        User = creds.User.Trim(),
                        Password = creds.Password,
                        StreamKeyPrefix = string.IsNullOrWhiteSpace(creds.StreamKeyPrefix)
                            ? "print.jobs"
                            : creds.StreamKeyPrefix.Trim(),
                        ConsumerGroup = string.IsNullOrWhiteSpace(creds.ConsumerGroup)
                            ? "printer-agents"
                            : creds.ConsumerGroup.Trim()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _redisHolder.Reset();

            _logger.LogInformation(
                "Redis credentials provisioned for ACL user {RedisUser} at {Host}:{Port}.",
                creds.User,
                creds.Host,
                creds.Port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis credentials provisioning failed; will retry.");
        }
    }

    private bool IsWireGuardInfrastructureReady()
    {
        var opt = _wireGuardOptions.Value;
        if (!opt.Enabled)
            return true;

        var path = opt.ConfigFilePath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return true;

        if (!File.Exists(path))
            return false;

        if (IsStaleWireGuardConf(path))
            return false;

        var serviceName = ResolveTunnelServiceName(opt, path);
        if (string.IsNullOrWhiteSpace(serviceName))
            return true;

        if (!opt.InstallTunnelServiceIfMissing)
            return true;

        if (!_wireGuardTunnelManager.ServiceExists(serviceName))
            return false;

        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private async Task TryProvisionWireGuardIfNeededAsync(CancellationToken cancellationToken)
    {
        var opt = _wireGuardOptions.Value;
        if (!opt.Enabled)
            return;

        var path = opt.ConfigFilePath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var agentId = _sessionStore.AgentId;
        if (string.IsNullOrWhiteSpace(agentId))
            return;

        var serviceName = ResolveTunnelServiceName(opt, path);
        var confMissing = !File.Exists(path);
        var serviceMissing = !string.IsNullOrWhiteSpace(serviceName)
            && !_wireGuardTunnelManager.ServiceExists(serviceName);

        if (!confMissing && IsStaleWireGuardConf(path))
        {
            _logger.LogWarning(
                "WireGuard: stale .conf at {Path} (dev LAN hub or AllowedIPs missing Redis host {RedisHost}); removing tunnel and re-provisioning from backend.",
                path,
                GetRedisHostFromSummary() ?? "(unknown)");
            await TryRemoveWireGuardTunnelAsync(opt, path, cancellationToken).ConfigureAwait(false);
            confMissing = true;
            serviceMissing = !string.IsNullOrWhiteSpace(serviceName)
                && !_wireGuardTunnelManager.ServiceExists(serviceName);
        }

        if (!confMissing && !serviceMissing)
            return;

        await TryProvisionWireGuardConfAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    private bool IsStaleWireGuardConf(string confPath)
    {
        try
        {
            var text = File.ReadAllText(confPath);
            if (text.Contains("192.168.", StringComparison.Ordinal))
                return true;

            var redisHost = GetRedisHostFromSummary();
            if (string.IsNullOrWhiteSpace(redisHost))
                return false;

            return !text.Contains(redisHost, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private string? GetRedisHostFromSummary()
    {
        var summary = _appConfiguration.RedisConnectionSummary;
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        var endpoint = summary.Split(',')[0];
        var host = endpoint.Split(':')[0];
        return string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }

    private async Task TryRemoveWireGuardTunnelAsync(WireGuardOptions opt, string confPath, CancellationToken cancellationToken)
    {
        var serviceName = ResolveTunnelServiceName(opt, confPath);
        if (!string.IsNullOrWhiteSpace(serviceName) && _wireGuardTunnelManager.ServiceExists(serviceName))
        {
            var tunnelName = serviceName.StartsWith("WireGuardTunnel$", StringComparison.Ordinal)
                ? serviceName["WireGuardTunnel$".Length..]
                : Path.GetFileNameWithoutExtension(confPath);
            await _wireGuardTunnelManager.UninstallTunnelServiceAsync(tunnelName, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(confPath))
            File.Delete(confPath);
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
            {
                var serviceName = ResolveTunnelServiceName(opt, path);
                var serviceExists = !string.IsNullOrWhiteSpace(serviceName)
                    && _wireGuardTunnelManager.ServiceExists(serviceName);
                if (opt.InstallTunnelServiceIfMissing && !serviceExists)
                    await TryInstallTunnelServiceAsync(opt, path, cancellationToken).ConfigureAwait(false);
                else if (serviceExists)
                    TryStartTunnelService(opt, serviceName);
                return;
            }

            await File.WriteAllTextAsync(path, conf, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("WireGuard provisioning: wrote config to {Path}.", path);

            if (opt.InstallTunnelServiceIfMissing)
                await TryInstallTunnelServiceAsync(opt, path, cancellationToken, reinstallIfExists: true).ConfigureAwait(false);
            else
                TryStartTunnelService(opt, ResolveTunnelServiceName(opt, path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WireGuard provisioning: failed to download/write .conf; continuing without blocking enrollment.");
        }
    }

    private async Task TryInstallTunnelServiceAsync(
        WireGuardOptions opt,
        string confPath,
        CancellationToken cancellationToken,
        bool reinstallIfExists = false)
    {
        try
        {
            var serviceName = ResolveTunnelServiceName(opt, confPath);
            if (string.IsNullOrWhiteSpace(serviceName))
                return;

            var tunnelName = serviceName.StartsWith("WireGuardTunnel$", StringComparison.Ordinal)
                ? serviceName["WireGuardTunnel$".Length..]
                : Path.GetFileNameWithoutExtension(confPath);

            if (_wireGuardTunnelManager.ServiceExists(serviceName))
            {
                if (!reinstallIfExists)
                {

                    _logger.LogInformation("WireGuard provisioning: tunnel service {Service} already exists.", serviceName);
                    TryStartTunnelService(opt, serviceName);
                    return;
                }

                _logger.LogInformation(
                    "WireGuard provisioning: reinstalling tunnel service {Service} after .conf update.",
                    serviceName);

                await _wireGuardTunnelManager.UninstallTunnelServiceAsync(tunnelName, cancellationToken).ConfigureAwait(false);
                await WaitForTunnelServiceRemovedAsync(serviceName, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "WireGuard provisioning: installing tunnel service {Service} from {Path}.",
                serviceName,
                confPath);

            try
            {
                await _wireGuardTunnelManager.InstallTunnelServiceAsync(confPath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already installed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "WireGuard provisioning: install reported tunnel still present; uninstalling and retrying once.");
                await _wireGuardTunnelManager.UninstallTunnelServiceAsync(tunnelName, cancellationToken).ConfigureAwait(false);
                await WaitForTunnelServiceRemovedAsync(serviceName, cancellationToken).ConfigureAwait(false);
                await _wireGuardTunnelManager.InstallTunnelServiceAsync(confPath, cancellationToken).ConfigureAwait(false);
            }

            TryStartTunnelService(opt, serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WireGuard provisioning: failed to install tunnel service from {Path}.", confPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task WaitForTunnelServiceRemovedAsync(string serviceName, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            if (!_wireGuardTunnelManager.ServiceExists(serviceName))
                return;

            TryStopTunnelService(serviceName);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogWarning(
            "WireGuard provisioning: tunnel service {Service} still present after uninstall; install may fail.",
            serviceName);
    }

    [SupportedOSPlatform("windows")]
    private void TryStopTunnelService(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return;

        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                _logger.LogInformation("WireGuard provisioning: stopping service {Service}...", serviceName);
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WireGuard provisioning: could not stop service {Service}.", serviceName);
        }
    }

    [SupportedOSPlatform("windows")]
    private void TryStartTunnelService(WireGuardOptions opt, string serviceName)
    {
        if (!opt.StartServiceIfStopped || string.IsNullOrWhiteSpace(serviceName))
            return;

        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status == ServiceControllerStatus.Running)
                return;

            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("WireGuard provisioning: starting service {Service}...", serviceName);
                sc.Start();
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.WaitForTunnelServiceSeconds, 5, 600));
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            _logger.LogInformation(
                "WireGuard provisioning: service {Service} status is {Status}.",
                serviceName,
                sc.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WireGuard provisioning: could not start service {Service}.", serviceName);
        }
    }

    private static string ResolveTunnelServiceName(WireGuardOptions opt, string confPath)
    {
        if (!string.IsNullOrWhiteSpace(opt.WindowsTunnelServiceName))
            return opt.WindowsTunnelServiceName.Trim();

        var tunnelName = opt.TunnelName?.Trim();
        if (string.IsNullOrWhiteSpace(tunnelName))
            tunnelName = Path.GetFileNameWithoutExtension(confPath);

        return string.IsNullOrWhiteSpace(tunnelName) ? string.Empty : $"WireGuardTunnel${tunnelName}";
    }

    /// <summary>
    /// Dacă enroll nu mai poate reuși (ex. cod consumat), dar avem încă refresh, lăsăm hostul pornit:
    /// Heartbeat reaplează refresh la fiecare ciclu.
    /// </summary>
    private bool CanContinueWithRefreshOnly() =>
        !string.IsNullOrWhiteSpace(_sessionStore.AgentId)
        && !string.IsNullOrWhiteSpace(_sessionStore.RefreshToken);

    private async Task<bool> CanContinueWithoutEnrollmentCodeAsync(CancellationToken cancellationToken)
    {
        if (CanContinueWithRefreshOnly())
            return true;

        await _deviceCredentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return _deviceCredentialStore.HasCredential;
    }

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

        [JsonPropertyName("deviceCredential")]
        public string DeviceCredential { get; set; } = string.Empty;
    }
}
