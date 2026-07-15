using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Application.UseCases;

public interface ILocalPrintJobHandler
{
    Task<PrintJobResult> PrintAsync(PrintJob job, CancellationToken cancellationToken = default);
}

public sealed class LocalPrintJobHandler : ILocalPrintJobHandler
{
    private readonly IPrinterServiceFactory _printerServiceFactory;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalPrintJobHandler> _logger;

    public LocalPrintJobHandler(
        IPrinterServiceFactory printerServiceFactory,
        IAppConfiguration appConfiguration,
        IAgentSessionStore sessionStore,
        IPrinterDiscoveryService printerDiscovery,
        IConfiguration configuration,
        ILogger<LocalPrintJobHandler> logger)
    {
        _printerServiceFactory = printerServiceFactory;
        _appConfiguration = appConfiguration;
        _sessionStore = sessionStore;
        _printerDiscovery = printerDiscovery;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PrintJobResult> PrintAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        var effectiveRestaurant = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
        if (!string.Equals(job.RestaurantId, effectiveRestaurant, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Local print job rejected: restaurant mismatch job={JobRestaurant} agent={AgentRestaurant}.",
                job.RestaurantId,
                effectiveRestaurant);
            return PrintJobResult.Failed("RESTAURANT_MISMATCH");
        }

        job.RedisMessageId = $"local-{Guid.NewGuid():N}";

        var merged = await _printerDiscovery
            .MergeArpEndpointsAsync(_appConfiguration.Printers, cancellationToken)
            .ConfigureAwait(false);

        var printer = merged.FirstOrDefault(p =>
            string.Equals(p.Id, job.PrinterId, StringComparison.OrdinalIgnoreCase));
        if (printer == null)
        {
            _logger.LogWarning(
                "Local print job failed: no printer with Id {PrinterId}. Configured: [{Configured}].",
                job.PrinterId,
                string.Join(", ", _appConfiguration.Printers.Select(p => p.Id)));
            return PrintJobResult.Failed("PRINTER_NOT_FOUND");
        }

        var printerService = _printerServiceFactory.Resolve(printer);
        var result = await printerService.PrintAsync(printer, job, cancellationToken).ConfigureAwait(false);
        if (result.Success)
            return result;

        var recovery = await _printerDiscovery.TryRecoverAfterPrintFailureAsync(printer, cancellationToken)
            .ConfigureAwait(false);
        if (!recovery.Recovered || recovery.Printer == null)
            return result;

        if (_configuration is IConfigurationRoot root)
            root.Reload();

        var retryPrinter = _appConfiguration.Printers.FirstOrDefault(p =>
                               string.Equals(p.Id, job.PrinterId, StringComparison.OrdinalIgnoreCase))
                           ?? recovery.Printer;
        var retryService = _printerServiceFactory.Resolve(retryPrinter);
        return await retryService.PrintAsync(retryPrinter, job, cancellationToken).ConfigureAwait(false);
    }
}
