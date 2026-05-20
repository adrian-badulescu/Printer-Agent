using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Application.UseCases;

public interface IHeartbeatService
{
    Task SendHeartbeatAsync(CancellationToken cancellationToken = default);
}

public class HeartbeatService : IHeartbeatService
{
    private readonly IBackendClient _backendClient;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAgentSessionRenewalService _sessionRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IBackendClient backendClient,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAppConfiguration appConfiguration,
        IPrinterDiscoveryService printerDiscovery,
        ILogger<HeartbeatService> logger)
    {
        _backendClient = backendClient;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _appConfiguration = appConfiguration;
        _printerDiscovery = printerDiscovery;
        _logger = logger;
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);

            _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            var agentId = _sessionStore.AgentId;
            var restaurantId = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(restaurantId))
            {
                var code = _appConfiguration.EnrollmentCode;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    _logger.LogWarning("Heartbeat recovery: session is missing; attempting enrollment using EnrollmentCode.");

                    var instanceId = _sessionStore.GetOrCreateClientInstanceId(cancellationToken);
                    var payload = await _backendClient.EnrollAsync(code, instanceId, cancellationToken).ConfigureAwait(false);

                    if (payload != null
                        && !string.IsNullOrWhiteSpace(payload.AgentId)
                        && !string.IsNullOrWhiteSpace(payload.AccessToken)
                        && !string.IsNullOrWhiteSpace(payload.RefreshToken)
                        && !string.IsNullOrWhiteSpace(payload.RestaurantId))
                    {
                        await _sessionStore.SaveSessionAsync(
                                payload.AgentId,
                                payload.AccessToken,
                                payload.RefreshToken,
                                payload.RestaurantId,
                                payload.ExpiresAtUtc,
                                cancellationToken)
                            .ConfigureAwait(false);

                        agentId = _sessionStore.AgentId;
                        restaurantId = _sessionStore.SessionRestaurantId ?? payload.RestaurantId;
                    }
                }

                if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(restaurantId))
                {
                    _logger.LogWarning("Heartbeat skipped: AgentId or RestaurantId is missing in session.");
                    return;
                }
            }

            var mergedPrinters = await _printerDiscovery
                .MergeArpEndpointsAsync(_appConfiguration.Printers, cancellationToken)
                .ConfigureAwait(false);

            var printersForHeartbeat = mergedPrinters.ToList();
            foreach (var printer in printersForHeartbeat)
            {
                printer.Status = await IsPrinterReachableAsync(printer, cancellationToken).ConfigureAwait(false)
                    ? PrinterStatus.Online
                    : PrinterStatus.Offline;
            }

            var agentInfo = new AgentInfo
            {
                AgentId = agentId,
                RestaurantId = restaurantId,
                Version = _appConfiguration.Version,
                Printers = printersForHeartbeat
            };

            var ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);
            if (!ok && !string.IsNullOrWhiteSpace(_sessionStore.RefreshToken))
            {
                _logger.LogWarning(
                    "Unauthorized heartbeat for agentId={AgentId}; attempting forced token refresh and a second heartbeat.",
                    agentId);
                _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken, force: true)
                    .ConfigureAwait(false);
                ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);
            }

            if (!ok)
            {
                _logger.LogWarning(
                    "URS_Metric HeartbeatUnauthorized agentId={AgentId}. Session cleared; if refresh failed permanently, set EnrollmentCode in agent.json and restart the service.",
                    agentId);
                await _sessionStore.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

                var code = _appConfiguration.EnrollmentCode;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    _logger.LogWarning(
                        "Heartbeat recovery: attempting re-enrollment using EnrollmentCode (agent.json) after session was cleared.");

                    var instanceId = _sessionStore.GetOrCreateClientInstanceId(cancellationToken);
                    var payload = await _backendClient.EnrollAsync(code, instanceId, cancellationToken).ConfigureAwait(false);
                    if (payload != null
                        && !string.IsNullOrWhiteSpace(payload.AgentId)
                        && !string.IsNullOrWhiteSpace(payload.AccessToken)
                        && !string.IsNullOrWhiteSpace(payload.RefreshToken))
                    {
                        await _sessionStore.SaveSessionAsync(
                                payload.AgentId,
                                payload.AccessToken,
                                payload.RefreshToken,
                                payload.RestaurantId,
                                payload.ExpiresAtUtc,
                                cancellationToken)
                            .ConfigureAwait(false);
                        _logger.LogInformation("Heartbeat recovery: re-enrollment succeeded (agentId={AgentId}).", payload.AgentId);
                    }
                    else
                    {
                        _logger.LogWarning("Heartbeat recovery: re-enrollment failed; will retry on next heartbeat cycle.");
                    }
                }

                return;
            }

            _logger.LogInformation("Heartbeat sent successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat.");
        }
    }

    private static async Task<bool> IsPrinterReachableAsync(Printer printer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(printer.IpAddress) || printer.Port <= 0)
            return false;

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(printer.IpAddress, printer.Port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
