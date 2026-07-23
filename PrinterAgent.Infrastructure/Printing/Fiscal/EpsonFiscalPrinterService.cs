using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Observability;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class EpsonFiscalPrinterService : IPrinterService
{
    private readonly IEpsonFiscalClient _client;
    private readonly ILogger<EpsonFiscalPrinterService> _logger;

    public EpsonFiscalPrinterService(IEpsonFiscalClient client, ILogger<EpsonFiscalPrinterService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<PrintJobResult> PrintAsync(Printer printer, PrintJob job, CancellationToken cancellationToken = default)
    {
        var payloadType = (job.Payload.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (payloadType is not ("bill" or "fiscal-receipt" or "fiscal-invoice" or "fiscal-storno-reso"))
        {
            _logger.LogWarning(
                "Epson fiscal printer {PrinterName} received unsupported payload type {PayloadType}.",
                printer.Name,
                payloadType);
            return PrintJobResult.Failed("UNSUPPORTED_PAYLOAD");
        }

        string innerXml;
        try
        {
            innerXml = EpsonFiscalXmlBuilder.BuildPrintXml(job.Payload, printer);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Epson fiscal XML build failed for job {JobId}.", job.RedisMessageId);
            return PrintJobResult.Failed("UNSUPPORTED_PAYLOAD", null, ex.Message);
        }

        var result = await _client.SendXmlAsync(printer, innerXml, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation(
                "Epson fiscal job {JobId} ({PayloadType}) printed on {PrinterName}. FiscalNumber={FiscalNumber}.",
                job.RedisMessageId,
                payloadType,
                printer.Name,
                result.FiscalNumber ?? result.FiscalReceiptNumber);
            return result;
        }

        AgentMetrics.PrintFailures.Add(1);
        _logger.LogWarning(
            "Epson fiscal job {JobId} ({PayloadType}) failed on {PrinterName}. Code={Code}.",
            job.RedisMessageId,
            payloadType,
            printer.Name,
            result.ErrorCode);
        return result;
    }
}
