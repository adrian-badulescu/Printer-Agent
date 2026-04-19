using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IPrinterService
{
    Task<bool> PrintAsync(Printer printer, PrintJob job, CancellationToken cancellationToken = default);
}
