using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Networking;
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
    private readonly IAgentDeviceRenewalService _deviceRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly IEpsonFiscalClient _epsonFiscalClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalPrintAuthTokenProvider _localPrintAuthTokenProvider;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IBackendClient backendClient,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAgentDeviceRenewalService deviceRenewal,
        IAppConfiguration appConfiguration,
        IPrinterDiscoveryService printerDiscovery,
        IEpsonFiscalClient epsonFiscalClient,
        IHttpClientFactory httpClientFactory,
        ILocalPrintAuthTokenProvider localPrintAuthTokenProvider,
        ILogger<HeartbeatService> logger)
    {
        _backendClient = backendClient;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _deviceRenewal = deviceRenewal;
        _appConfiguration = appConfiguration;
        _printerDiscovery = printerDiscovery;
        _epsonFiscalClient = epsonFiscalClient;
        _httpClientFactory = httpClientFactory;
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

    private async Task<bool> IsPrinterReachableAsync(Printer printer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(printer.IpAddress))
            return false;

        if (PrinterTypes.IsEpsonFiscal(printer))
            return await _epsonFiscalClient.IsReachableAsync(printer, cancellationToken).ConfigureAwait(false);

        if (PrinterTypes.IsFiscalNet(printer))
            return await IsFiscalNetReachableAsync(printer, cancellationToken).ConfigureAwait(false);

        if (printer.Port <= 0)
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

    private async Task<bool> IsFiscalNetReachableAsync(Printer printer, CancellationToken cancellationToken)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var scheme = fiscal.UseHttps ? "https" : "http";
        var port = printer.Port > 0 ? printer.Port : 65400;
        var host = printer.IpAddress.Trim();
        var url = $"{scheme}://{host}:{port}/api/Receipt";

        try
        {
            var client = _httpClientFactory.CreateClient("FiscalNet");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Options, url);
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 405)
                return true;

            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var getResponse = await client.SendAsync(getRequest, cts.Token).ConfigureAwait(false);
            return getResponse.IsSuccessStatusCode || (int)getResponse.StatusCode == 405;
        }
        catch
        {
            return false;
        }
    }
}
