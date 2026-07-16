using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IFiscalCommandRouter
{
    Task<PrintJobResult> ExecuteAsync(
        Printer printer,
        FiscalCommandRequest request,
        CancellationToken cancellationToken = default);
}
