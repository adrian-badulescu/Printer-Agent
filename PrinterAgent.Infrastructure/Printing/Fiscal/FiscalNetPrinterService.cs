using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Observability;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FiscalNetPrinterService : IPrinterService
{
    private readonly FiscalNetHttpClient _httpClient;
    private readonly ILogger<FiscalNetPrinterService> _logger;

    public FiscalNetPrinterService(FiscalNetHttpClient httpClient, ILogger<FiscalNetPrinterService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PrintJobResult> PrintAsync(Printer printer, PrintJob job, CancellationToken cancellationToken = default)
    {
        var payloadType = (job.Payload.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (payloadType is not ("fiscal-receipt" or "fiscal-invoice"))
        {
            _logger.LogWarning(
                "FiscalNet printer {PrinterName} received unsupported payload type {PayloadType}.",
                printer.Name,
                payloadType);
            return PrintJobResult.Failed("UNSUPPORTED_PAYLOAD");
        }

        var lines = FiscalNetReceiptLineBuilder.Build(job, printer);
        var response = await _httpClient.SendReceiptAsync(printer, lines, cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            _logger.LogInformation(
                "Fiscal job {JobId} printed on {PrinterName}. Receipt={ReceiptNumber}.",
                job.RedisMessageId,
                printer.Name,
                response.FiscalReceiptNumber);
            return response.ToPrintJobResult();
        }

        AgentMetrics.PrintFailures.Add(1);
        _logger.LogWarning(
            "Fiscal job {JobId} failed on {PrinterName}. Code={Code}.",
            job.RedisMessageId,
            printer.Name,
            response.ErrorCode);
        return response.ToPrintJobResult();
    }
}
