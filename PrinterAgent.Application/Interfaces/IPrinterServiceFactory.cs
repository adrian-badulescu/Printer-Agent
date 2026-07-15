using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IPrinterServiceFactory
{
    IPrinterService Resolve(Printer printer);
}
