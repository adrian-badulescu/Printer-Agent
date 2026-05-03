using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Networking;
using PrinterAgent.Infrastructure.Networking;

namespace PrinterAgent.Infrastructure.Persistence;

public sealed class PrinterMacCaptureService : IPrinterMacCapture
{
    private readonly IAppConfiguration _config;
    private readonly IAgentPrinterConfigurationUpdater _updater;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PrinterMacCaptureService> _logger;

    public PrinterMacCaptureService(
        IAppConfiguration config,
        IAgentPrinterConfigurationUpdater updater,
        IConfiguration configuration,
        ILogger<PrinterMacCaptureService> logger)
    {
        _config = config;
        _updater = updater;
        _configuration = configuration;
        _logger = logger;
    }

    public Task TryPersistMacAfterSuccessfulPrintAsync(string printerId, string remoteIpv4, CancellationToken cancellationToken = default)
    {
        try
        {
            var configured = _config.Printers.FirstOrDefault(p => p.Id == printerId);
            if (configured == null)
                return Task.CompletedTask;

            if (!string.IsNullOrEmpty(PrinterMacNormalizer.Normalize(configured.MacAddress)))
                return Task.CompletedTask;

            if (!IPAddress.TryParse(remoteIpv4, out var ip))
                return Task.CompletedTask;

            if (!IpHlpNative.TryGetMacForIPv4(ip, out var macRaw) || string.IsNullOrEmpty(macRaw))
            {
                _logger.LogWarning(
                    "Could not resolve MAC for printer {PrinterId} at {Ip} after successful print; macAddress will remain unset.",
                    printerId,
                    remoteIpv4);
                return Task.CompletedTask;
            }

            var norm = PrinterMacNormalizer.Normalize(macRaw);
            if (string.IsNullOrEmpty(norm))
                return Task.CompletedTask;

            if (_updater.TryPatchPrinter(printerId, null, norm, false, "arp_after_first_print"))
            {
                if (_configuration is IConfigurationRoot root)
                    root.Reload();
                _logger.LogInformation("Persisted MAC from ARP for printer {PrinterId}.", printerId);
            }
            else
            {
                _logger.LogWarning(
                    "MAC resolved for printer {PrinterId} but could not write %ProgramData%\\URSPrinterAgent\\agent.json (missing file or printer id).",
                    printerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAC capture skipped for {PrinterId}.", printerId);
        }

        return Task.CompletedTask;
    }
}
