using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Observability;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FiscalNetCommandHandler : IFiscalCommandHandler
{
    private readonly FiscalNetHttpClient _httpClient;
    private readonly ILogger<FiscalNetCommandHandler> _logger;

    public FiscalNetCommandHandler(FiscalNetHttpClient httpClient, ILogger<FiscalNetCommandHandler> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public bool CanHandle(Printer printer) => PrinterTypes.IsFiscalNet(printer);

    public async Task<PrintJobResult> ExecuteAsync(
        Printer printer,
        FiscalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
        var lines = MapCommandToLines(command);
        if (lines == null)
        {
            _logger.LogWarning(
                "FiscalNet printer {PrinterName} received unsupported command {Command}.",
                printer.Name,
                command);
            return PrintJobResult.Failed("UNSUPPORTED_FISCAL_COMMAND");
        }

        var response = await _httpClient.SendReceiptAsync(printer, lines, cancellationToken).ConfigureAwait(false);

        // #region agent log
        DebugSessionLog.Write(
            "H3",
            "FiscalNetCommandHandler.ExecuteAsync:done",
            "fiscal command http completed",
            new
            {
                command,
                printerId = printer.Id,
                port = printer.Port,
                success = response.Success,
                errorCode = response.ErrorCode,
            });
        // #endregion

        if (response.Success)
        {
            _logger.LogInformation(
                "Fiscal command {Command} succeeded on {PrinterName}.",
                command,
                printer.Name);
            return response.ToPrintJobResult();
        }

        _logger.LogWarning(
            "Fiscal command {Command} failed on {PrinterName}. Code={Code}.",
            command,
            printer.Name,
            response.ErrorCode);
        return response.ToPrintJobResult();
    }

    internal static string[]? MapCommandToLines(string command) =>
        command switch
        {
            FiscalCommandTypes.OpenDrawer => ["DS^"],
            _ => null,
        };
}
