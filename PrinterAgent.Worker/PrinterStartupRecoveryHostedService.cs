using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;
using PrinterAgent.Worker.Config;

namespace PrinterAgent.Worker;

/// <summary>
/// Subnet scanning normally runs only after a print job fails. Heartbeat uses ARP only (no scan), which often
/// does nothing right after reboot. Probes the configured IP once at startup and runs the same recovery as a failed print.
/// </summary>
public sealed class PrinterStartupRecoveryHostedService : IHostedService
{
    private const int StartupTcpProbeMs = 2000;

    private readonly IAgentSessionStore _sessionStore;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IPrinterDiscoveryService _discovery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PrinterStartupRecoveryHostedService> _logger;

    public PrinterStartupRecoveryHostedService(
        IAgentSessionStore sessionStore,
        IAppConfiguration appConfiguration,
        IPrinterDiscoveryService discovery,
        IConfiguration configuration,
        ILogger<PrinterStartupRecoveryHostedService> logger)
    {
        _sessionStore = sessionStore;
        _appConfiguration = appConfiguration;
        _discovery = discovery;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("PrinterDiscovery:RecoverWhenConfiguredIpUnreachableAtStartup", true))
        {
            _logger.LogInformation(
                "Printer startup IP recovery is disabled (PrinterDiscovery:RecoverWhenConfiguredIpUnreachableAtStartup=false).");
            return Task.CompletedTask;
        }

        _ = RunWhenSessionReadyAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunWhenSessionReadyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForSessionAsync(stoppingToken).ConfigureAwait(false);
            var delaySec = _configuration.GetValue("PrinterDiscovery:StartupRecoveryDelaySeconds", 5);
            if (delaySec > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delaySec, 0, 120)), stoppingToken).ConfigureAwait(false);

            var printers = _appConfiguration.Printers;
            if (printers.Count == 0)
                return;

            foreach (var printer in printers)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(printer.IpAddress))
                {
                    _logger.LogWarning("Printer startup check: {PrinterId} has no ipAddress — skip.", printer.Id);
                    continue;
                }

                if (!IPAddress.TryParse(printer.IpAddress, out _))
                {
                    _logger.LogWarning(
                        "Printer startup check: {PrinterId} has invalid ipAddress {Ip} — skip.",
                        printer.Id,
                        printer.IpAddress);
                    continue;
                }

                if (await IsConfiguredEndpointReachableAsync(printer, stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Printer startup check: {PrinterId} responds at {Ip}:{Port} — no recovery needed.",
                        printer.Id,
                        printer.IpAddress,
                        printer.Port);
                    continue;
                }

                _logger.LogWarning(
                    "Printer startup check: no TCP response at configured {Ip}:{Port} for {PrinterId} — running discovery recovery.",
                    printer.IpAddress,
                    printer.Port,
                    printer.Id);

                var result = await _discovery.TryRecoverAfterPrintFailureAsync(printer, stoppingToken).ConfigureAwait(false);
                if (result.Recovered)
                    _logger.LogInformation(
                        "Printer startup recovery succeeded for {PrinterId}: {Note}.",
                        printer.Id,
                        result.TelemetryNote);
                else
                    _logger.LogWarning(
                        "Printer startup recovery could not fix {PrinterId}: {Note}.",
                        printer.Id,
                        result.TelemetryNote ?? "unknown");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printer startup recovery loop failed.");
        }
    }

    private async Task WaitForSessionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _sessionStore.LoadAsync(ct).ConfigureAwait(false);
            var agentId = _sessionStore.AgentId;
            var restaurantId = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
            if (!string.IsNullOrWhiteSpace(agentId) && !string.IsNullOrWhiteSpace(restaurantId))
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsConfiguredEndpointReachableAsync(Printer printer, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StartupTcpProbeMs);
            await client.ConnectAsync(printer.IpAddress!, printer.Port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
