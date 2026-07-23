using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

/// <summary>
/// Simulates EpsonBridgeStub flow: fiscal receipt → direct invoice → storno reso (REFUND).
/// </summary>
public sealed class EpsonFiscalEndToEndTests
{
    private static readonly Printer EpsonPrinter = new()
    {
        Fiscal = new FiscalPrinterSettings { OperatorId = 1, DefaultDepartment = 1 },
    };

    private static readonly PrintJobItem SampleItem = new()
    {
        Name = "Pizza",
        Quantity = 1,
        UnitPrice = 12.5m,
        VatGroup = 2,
    };

    [Fact]
    public void Receipt_invoice_reso_chain_matches_stub_addInfo_and_refund_reference()
    {
        var receiptPayload = new PrintJobPayload
        {
            Type = "fiscal-receipt",
            FinalTotal = 12.5m,
            PaymentMethod = "card",
            Items = [SampleItem],
        };

        var receiptXml = EpsonFiscalXmlBuilder.BuildPrintXml(receiptPayload, EpsonPrinter);
        Assert.Contains("<printerFiscalReceipt>", receiptXml, StringComparison.Ordinal);
        Assert.Contains("<printRecItem", receiptXml, StringComparison.Ordinal);

        const string stubReceiptResponse = """
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <response success="true" code="" status="0">
                  <addInfo>
                    <fiscalReceiptNumber>0001</fiscalReceiptNumber>
                    <zRepNumber>0001</zRepNumber>
                    <fiscalDate>22072026</fiscalDate>
                  </addInfo>
                </response>
              </s:Body>
            </s:Envelope>
            """;

        var receiptResult = FpMateFiscalResponse.Parse(stubReceiptResponse).ToPrintJobResult();
        Assert.True(receiptResult.Success);
        Assert.Equal("0001", receiptResult.FiscalNumber);
        Assert.Equal("0001", receiptResult.ZReportNumber);
        Assert.Equal("22072026", receiptResult.FiscalDate);

        var invoicePayload = new PrintJobPayload
        {
            Type = "fiscal-invoice",
            CustomerName = "Acme SRL",
            CustomerFiscalCode = "IT12345678901",
            CustomerAddressLine1 = "Via Roma 1",
            FinalTotal = 12.5m,
            PaymentMethod = "card",
            Items = [SampleItem],
        };

        var invoiceXml = EpsonFiscalXmlBuilder.BuildPrintXml(invoicePayload, EpsonPrinter);
        Assert.Contains("documentType=\"directInvoice\"", invoiceXml, StringComparison.Ordinal);

        const string stubInvoiceResponse = """
            <response success="true" code="" status="0">
              <addInfo>
                <fiscalDocumentNumber>1001</fiscalDocumentNumber>
                <zRepNumber>0001</zRepNumber>
                <fiscalDate>22072026</fiscalDate>
              </addInfo>
            </response>
            """;

        var invoiceResult = FpMateFiscalResponse.Parse(stubInvoiceResponse).ToPrintJobResult();
        Assert.Equal("1001", invoiceResult.FiscalNumber);

        var resoPayload = new PrintJobPayload
        {
            Type = "fiscal-storno-reso",
            FiscalReferenceZReport = receiptResult.ZReportNumber,
            FiscalReferenceReceiptNumber = receiptResult.FiscalNumber,
            FiscalReferenceDate = receiptResult.FiscalDate,
            FinalTotal = 12.5m,
            PaymentMethod = "card",
            Items = [SampleItem],
        };

        var resoXml = EpsonFiscalXmlBuilder.BuildPrintXml(resoPayload, EpsonPrinter);
        Assert.Contains("message=\"REFUND 0001 0001 22072026\"", resoXml, StringComparison.Ordinal);
        Assert.Contains("<printRecRefund", resoXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<printRecItem", resoXml, StringComparison.Ordinal);

        const string stubResoResponse = """
            <response success="true" code="" status="0">
              <addInfo>
                <fiscalReceiptNumber>0002</fiscalReceiptNumber>
                <zRepNumber>0001</zRepNumber>
                <fiscalDate>22072026</fiscalDate>
              </addInfo>
            </response>
            """;

        var resoResult = FpMateFiscalResponse.Parse(stubResoResponse).ToPrintJobResult();
        Assert.True(resoResult.Success);
        Assert.Equal("0002", resoResult.FiscalNumber);
    }
}
