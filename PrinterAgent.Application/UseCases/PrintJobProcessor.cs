using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Observability;
using PrinterAgent.Domain;

namespace PrinterAgent.Application.UseCases;

public interface IPrintJobProcessor
{
    Task ProcessJobAsync(PrintJob job, CancellationToken cancellationToken = default);
}

public class PrintJobProcessor : IPrintJobProcessor
{
    private readonly IPrinterService _printerService;
    private readonly IBackendClient _backendClient;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PrintJobProcessor> _logger;

    public PrintJobProcessor(
        IPrinterService printerService,
        IBackendClient backendClient,
        IAppConfiguration appConfiguration,
        IAgentSessionStore sessionStore,
        IPrinterDiscoveryService printerDiscovery,
        IConfiguration configuration,
        ILogger<PrintJobProcessor> logger)
    {
        _printerService = printerService;
        _backendClient = backendClient;
        _appConfiguration = appConfiguration;
        _sessionStore = sessionStore;
        _printerDiscovery = printerDiscovery;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ProcessJobAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        var effectiveRestaurant = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
        if (!string.Equals(job.RestaurantId, effectiveRestaurant, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Job {JobId} restaurant mismatch: job={JobRestaurant} agent={AgentRestaurant}.",
                job.RedisMessageId, job.RestaurantId, effectiveRestaurant);
            AgentMetrics.PrintFailures.Add(1);
            await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Failed, cancellationToken);
            AgentMetrics.JobsProcessed.Add(1);
            return;
        }

        await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Printing, cancellationToken);

        _logger.LogInformation(
            "Print job {JobId}: payloadType={PayloadType} requested printerId={RequestedPrinterId}.",
            job.RedisMessageId,
            job.Payload?.Type,
            job.PrinterId);

        var printer = _appConfiguration.Printers.FirstOrDefault(p =>
            string.Equals(p.Id, job.PrinterId, StringComparison.OrdinalIgnoreCase));
        if (printer == null)
        {
            var configured = _appConfiguration.Printers.Select(p => p.Id).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            _logger.LogWarning(
                "Print job {JobId} failed: no printer with Id matching {RequestedPrinterId}. Configured printer ids: [{Configured}]. " +
                "Add or fix Printers[] in %ProgramData%\\URSPrinterAgent\\agent.json (Configurator) so Id matches the backend job's printerId.",
                job.RedisMessageId,
                job.PrinterId,
                string.Join(", ", configured));
            AgentMetrics.PrintFailures.Add(1);
            await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Failed, cancellationToken);
            AgentMetrics.JobsProcessed.Add(1);
            return;
        }

        var success = await _printerService.PrintAsync(printer, job, cancellationToken);
        if (!success)
        {
            var recovery = await _printerDiscovery.TryRecoverAfterPrintFailureAsync(printer, cancellationToken)
                .ConfigureAwait(false);
            if (recovery.Recovered && recovery.Printer != null)
            {
                if (_configuration is IConfigurationRoot root)
                    root.Reload();

                var retryPrinter = _appConfiguration.Printers.FirstOrDefault(p =>
                                      string.Equals(p.Id, job.PrinterId, StringComparison.OrdinalIgnoreCase))
                                  ?? recovery.Printer;
                success = await _printerService.PrintAsync(retryPrinter, job, cancellationToken).ConfigureAwait(false);
            }
        }

        var finalStatus = success ? PrintJobStatus.Success : PrintJobStatus.Failed;

        if (!success)
            AgentMetrics.PrintFailures.Add(1);

        await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, finalStatus, cancellationToken);
        AgentMetrics.JobsProcessed.Add(1);
    }
}
