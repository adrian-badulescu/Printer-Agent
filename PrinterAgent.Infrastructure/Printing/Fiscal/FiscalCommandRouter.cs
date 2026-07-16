using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FiscalCommandRouter : IFiscalCommandRouter
{
    private readonly IReadOnlyList<IFiscalCommandHandler> _handlers;

    public FiscalCommandRouter(IEnumerable<IFiscalCommandHandler> handlers)
    {
        _handlers = handlers.ToList();
    }

    public Task<PrintJobResult> ExecuteAsync(
        Printer printer,
        FiscalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(printer));
        if (handler == null)
            return Task.FromResult(PrintJobResult.Failed("UNSUPPORTED_FISCAL_DEVICE"));

        return handler.ExecuteAsync(printer, request, cancellationToken);
    }
}
