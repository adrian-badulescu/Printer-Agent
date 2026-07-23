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
    private readonly IPrinterServiceFactory _printerServiceFactory;
    private readonly IFiscalCommandRouter _fiscalCommandRouter;
    private readonly IBackendClient _backendClient;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IPrinterDiscoveryService _printerDiscovery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PrintJobProcessor> _logger;

    public PrintJobProcessor(
        IPrinterServiceFactory printerServiceFactory,
        IFiscalCommandRouter fiscalCommandRouter,
        IBackendClient backendClient,
        IAppConfiguration appConfiguration,
        IAgentSessionStore sessionStore,
        IPrinterDiscoveryService printerDiscovery,
        IConfiguration configuration,
        ILogger<PrintJobProcessor> logger)
    {
        _printerServiceFactory = printerServiceFactory;
        _fiscalCommandRouter = fiscalCommandRouter;
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
            await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Failed, cancellationToken: cancellationToken);
            AgentMetrics.JobsProcessed.Add(1);
            return;
        }

        await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Printing, cancellationToken: cancellationToken);

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
            await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Failed, cancellationToken: cancellationToken);
            AgentMetrics.JobsProcessed.Add(1);
            return;
        }

        var result = await ExecutePrintAsync(printer, job, cancellationToken);
        var success = result.Success;
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
                result = await ExecutePrintAsync(retryPrinter, job, cancellationToken).ConfigureAwait(false);
                success = result.Success;
            }
        }

        var finalStatus = success ? PrintJobStatus.Success : PrintJobStatus.Failed;

        if (!success)
            AgentMetrics.PrintFailures.Add(1);

        await _backendClient.UpdateJobStatusAsync(
            job.RedisMessageId,
            finalStatus,
            result.ErrorCode,
            result.DeviceErrorCode,
            success ? result.FiscalNumber : null,
            success ? result.ZReportNumber : null,
            success ? result.FiscalDate : null,
            cancellationToken);
        AgentMetrics.JobsProcessed.Add(1);
    }

    private async Task<PrintJobResult> ExecutePrintAsync(
        Printer printer,
        PrintJob job,
        CancellationToken cancellationToken)
    {
        if (string.Equals(job.Payload?.Type, PrintJobPayloadTypes.FiscalCommand, StringComparison.OrdinalIgnoreCase))
        {
            var payload = job.Payload!;
            var command = (payload.Command ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(command))
                return PrintJobResult.Failed("MISSING_FISCAL_COMMAND");

            return await _fiscalCommandRouter.ExecuteAsync(
                printer,
                new FiscalCommandRequest { Command = command },
                cancellationToken).ConfigureAwait(false);
        }

        var printerService = _printerServiceFactory.Resolve(printer);
        return await printerService.PrintAsync(printer, job, cancellationToken).ConfigureAwait(false);
    }
}
