using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IPrinterService
{
    Task<PrintJobResult> PrintAsync(Printer printer, PrintJob job, CancellationToken cancellationToken = default);
}
