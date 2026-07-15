using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;

namespace PrinterAgent.Infrastructure.Printing;

public sealed class PrinterServiceFactory : IPrinterServiceFactory
{
    private readonly EscPosPrinterService _escPos;
    private readonly FiscalNetPrinterService _fiscalNet;

    public PrinterServiceFactory(EscPosPrinterService escPos, FiscalNetPrinterService fiscalNet)
    {
        _escPos = escPos;
        _fiscalNet = fiscalNet;
    }

    public IPrinterService Resolve(Printer printer) =>
        PrinterTypes.IsFiscalNet(printer) ? _fiscalNet : _escPos;
}
