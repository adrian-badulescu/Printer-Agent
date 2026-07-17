using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;

namespace PrinterAgent.Infrastructure.Printing;

public sealed class PrinterServiceFactory : IPrinterServiceFactory
{
    private readonly EscPosPrinterService _escPos;
    private readonly FiscalNetPrinterService _fiscalNet;
    private readonly EpsonFiscalPrinterService _epsonFiscal;

    public PrinterServiceFactory(
        EscPosPrinterService escPos,
        FiscalNetPrinterService fiscalNet,
        EpsonFiscalPrinterService epsonFiscal)
    {
        _escPos = escPos;
        _fiscalNet = fiscalNet;
        _epsonFiscal = epsonFiscal;
    }

    public IPrinterService Resolve(Printer printer)
    {
        if (PrinterTypes.IsEpsonFiscal(printer))
            return _epsonFiscal;
        if (PrinterTypes.IsFiscalNet(printer))
            return _fiscalNet;
        return _escPos;
    }
}
