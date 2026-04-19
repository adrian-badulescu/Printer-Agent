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
    private readonly ILogger<PrintJobProcessor> _logger;

    public PrintJobProcessor(
        IPrinterService printerService,
        IBackendClient backendClient,
        IAppConfiguration appConfiguration,
        ILogger<PrintJobProcessor> logger)
    {
        _printerService = printerService;
        _backendClient = backendClient;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public async Task ProcessJobAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(job.RestaurantId, _appConfiguration.RestaurantId, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Job {JobId} restaurant mismatch: job={JobRestaurant} agent={AgentRestaurant}.",
                job.RedisMessageId, job.RestaurantId, _appConfiguration.RestaurantId);
            AgentMetrics.PrintFailures.Add(1);
            await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Failed, cancellationToken);
            AgentMetrics.JobsProcessed.Add(1);
            return;
        }

        await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Received, cancellationToken);
        await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Printing, cancellationToken);

        var printer = _appConfiguration.Printers.FirstOrDefault(p => p.Id == job.PrinterId);
        if (printer == null)
        {
            AgentMetrics.PrintFailures.Add(1);
            await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, PrintJobStatus.Failed, cancellationToken);
            AgentMetrics.JobsProcessed.Add(1);
            return;
        }

        var success = await _printerService.PrintAsync(printer, job, cancellationToken);
        var finalStatus = success ? PrintJobStatus.Success : PrintJobStatus.Failed;

        if (!success)
            AgentMetrics.PrintFailures.Add(1);

        await _backendClient.UpdateJobStatusAsync(job.RedisMessageId, finalStatus, cancellationToken);
        AgentMetrics.JobsProcessed.Add(1);
    }
}
