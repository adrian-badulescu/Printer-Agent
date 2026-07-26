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
        string[] lines;
        switch (payloadType)
        {
            case "bill":
                lines = FiscalNetNonFiscalLineBuilder.Build(job, printer);
                break;
            case "fiscal-receipt":
            case "fiscal-invoice":
                lines = FiscalNetReceiptLineBuilder.Build(job, printer);
                break;
            case "fiscal-storno-reso":
                lines = FiscalNetReceiptLineBuilder.BuildStorno(job, printer);
                break;
            default:
                _logger.LogWarning(
                    "FiscalNet printer {PrinterName} received unsupported payload type {PayloadType}.",
                    printer.Name,
                    payloadType);
                return PrintJobResult.Failed("UNSUPPORTED_PAYLOAD");
        }

        var response = await _httpClient.SendReceiptAsync(printer, lines, cancellationToken).ConfigureAwait(false);

        if (response.Success)
        {
            _logger.LogInformation(
                "FiscalNet job {JobId} ({PayloadType}) printed on {PrinterName}. Receipt={ReceiptNumber}. Z={ZReport}. Date={FiscalDate}.",
                job.RedisMessageId,
                payloadType,
                printer.Name,
                response.FiscalReceiptNumber,
                response.ZReportNumber ?? "(missing)",
                response.FiscalDate ?? "(missing)");
            if (IsFiscalDocumentPayload(payloadType)
                && string.IsNullOrWhiteSpace(response.ZReportNumber))
            {
                _logger.LogWarning(
                    "FiscalNet job {JobId}: Z report number missing in driver response; storno will fail until NRZ is available.",
                    job.RedisMessageId);
            }
            return response.ToPrintJobResult();
        }

        AgentMetrics.PrintFailures.Add(1);
        _logger.LogWarning(
            "FiscalNet job {JobId} ({PayloadType}) failed on {PrinterName}. Code={Code}.",
            job.RedisMessageId,
            payloadType,
            printer.Name,
            response.ErrorCode);
        return response.ToPrintJobResult();
    }

    private static bool IsFiscalDocumentPayload(string payloadType) =>
        payloadType is "fiscal-receipt" or "fiscal-invoice" or "fiscal-storno-reso";
}
