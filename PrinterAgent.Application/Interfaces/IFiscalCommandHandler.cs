using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IFiscalCommandHandler
{
    bool CanHandle(Printer printer);

    Task<PrintJobResult> ExecuteAsync(
        Printer printer,
        FiscalCommandRequest request,
        CancellationToken cancellationToken = default);
}
