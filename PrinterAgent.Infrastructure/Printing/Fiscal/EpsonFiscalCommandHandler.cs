using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class EpsonFiscalCommandHandler : IFiscalCommandHandler
{
    private readonly IEpsonFiscalClient _client;
    private readonly ILogger<EpsonFiscalCommandHandler> _logger;

    public EpsonFiscalCommandHandler(IEpsonFiscalClient client, ILogger<EpsonFiscalCommandHandler> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool CanHandle(Printer printer) => PrinterTypes.IsEpsonFiscal(printer);

    public async Task<PrintJobResult> ExecuteAsync(
        Printer printer,
        FiscalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(command, FiscalCommandTypes.OpenDrawer, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Epson fiscal printer {PrinterName} received unsupported command {Command}.",
                printer.Name,
                command);
            return PrintJobResult.Failed("UNSUPPORTED_FISCAL_COMMAND");
        }

        var innerXml = EpsonFiscalXmlBuilder.BuildOpenDrawerXml(printer);
        var result = await _client.SendXmlAsync(printer, innerXml, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation(
                "Epson fiscal command {Command} succeeded on {PrinterName}.",
                command,
                printer.Name);
            return result;
        }

        _logger.LogWarning(
            "Epson fiscal command {Command} failed on {PrinterName}. Code={Code}.",
            command,
            printer.Name,
            result.ErrorCode);
        return result;
    }
}
