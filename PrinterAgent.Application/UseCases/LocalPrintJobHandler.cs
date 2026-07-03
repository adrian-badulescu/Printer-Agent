using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Application.UseCases;

public interface ILocalPrintJobHandler
{
    Task<bool> PrintAsync(PrintJob job, CancellationToken cancellationToken = default);
}

public sealed class LocalPrintJobHandler : ILocalPrintJobHandler
{
    private readonly IPrinterService _printerService;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalPrintJobHandler> _logger;

    public LocalPrintJobHandler(
        IPrinterService printerService,
        IAppConfiguration appConfiguration,
        IAgentSessionStore sessionStore,
        IPrinterDiscoveryService printerDiscovery,
        IConfiguration configuration,
        ILogger<LocalPrintJobHandler> logger)
    {
        _printerService = printerService;
        _appConfiguration = appConfiguration;
        _sessionStore = sessionStore;
        _printerDiscovery = printerDiscovery;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> PrintAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        var effectiveRestaurant = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
        if (!string.Equals(job.RestaurantId, effectiveRestaurant, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Local print job rejected: restaurant mismatch job={JobRestaurant} agent={AgentRestaurant}.",
                job.RestaurantId,
                effectiveRestaurant);
            return false;
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
            return false;
        }

        var success = await _printerService.PrintAsync(printer, job, cancellationToken).ConfigureAwait(false);
        if (success)
            return true;

        var recovery = await _printerDiscovery.TryRecoverAfterPrintFailureAsync(printer, cancellationToken)
            .ConfigureAwait(false);
        if (!recovery.Recovered || recovery.Printer == null)
            return false;

        if (_configuration is IConfigurationRoot root)
            root.Reload();

        var retryPrinter = _appConfiguration.Printers.FirstOrDefault(p =>
                               string.Equals(p.Id, job.PrinterId, StringComparison.OrdinalIgnoreCase))
                           ?? recovery.Printer;
        return await _printerService.PrintAsync(retryPrinter, job, cancellationToken).ConfigureAwait(false);
    }
}
