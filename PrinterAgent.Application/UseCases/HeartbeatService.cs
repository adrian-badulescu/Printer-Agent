using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Networking;
using PrinterAgent.Domain;

public interface IHeartbeatService
{
    Task SendHeartbeatAsync(CancellationToken cancellationToken = default);
}

public class HeartbeatService : IHeartbeatService
{
    private readonly IBackendClient _backendClient;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAgentSessionRenewalService _sessionRenewal;
    private readonly IAgentDeviceRenewalService _deviceRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly ILocalPrintAuthTokenProvider _localPrintAuthTokenProvider;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IBackendClient backendClient,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAgentDeviceRenewalService deviceRenewal,
        IAppConfiguration appConfiguration,
        IPrinterDiscoveryService printerDiscovery,
        ILocalPrintAuthTokenProvider localPrintAuthTokenProvider,
        ILogger<HeartbeatService> logger)
    {
        _backendClient = backendClient;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _deviceRenewal = deviceRenewal;
        _appConfiguration = appConfiguration;
        _printerDiscovery = printerDiscovery;
        _localPrintAuthTokenProvider = localPrintAuthTokenProvider;
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
                _logger.LogWarning(
                    "Heartbeat skipped: no session (agentId/restaurantId missing). Enrollment loop will recover via device credential or enrollment code.");
                return;
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

            if (_appConfiguration.LocalPrintEnabled)
            {
                agentInfo.LocalApiBaseUrl = LocalPrintEndpointBuilder.TryBuildBaseUrl(_appConfiguration.LocalPrintPort);
                agentInfo.LocalPrintApiToken = await _localPrintAuthTokenProvider.GetTokenAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                _logger.LogWarning(
                    "Unauthorized heartbeat for agentId={AgentId}; attempting refresh, device renew, then retry.",
                    agentId);

                _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken, force: true)
                    .ConfigureAwait(false);
                ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);

                if (!ok)
                {
                    _ = await _deviceRenewal.TryRenewWithDeviceCredentialAsync(cancellationToken).ConfigureAwait(false);
                    await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);

                    agentId = _sessionStore.AgentId;
                    restaurantId = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
                    if (!string.IsNullOrWhiteSpace(agentId) && !string.IsNullOrWhiteSpace(restaurantId))
                    {
                        agentInfo.AgentId = agentId;
                        agentInfo.RestaurantId = restaurantId;
                        ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!ok)
            {
                _logger.LogWarning(
                    "URS_Metric HeartbeatUnauthorized agentId={AgentId}. Session cleared; enrollment loop will try device renew or re-enroll.",
                    agentId);
                await _sessionStore.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
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
