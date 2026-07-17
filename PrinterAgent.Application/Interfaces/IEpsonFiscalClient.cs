using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IEpsonFiscalClient
{
    Task<PrintJobResult> SendXmlAsync(Printer printer, string innerXml, CancellationToken cancellationToken = default);

    Task<bool> IsReachableAsync(Printer printer, CancellationToken cancellationToken = default);
}
