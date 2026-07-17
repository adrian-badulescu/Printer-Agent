using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class EpsonFiscalXmlBuilderTests
{
    [Fact]
    public void BuildFiscalReceiptXml_includes_items_and_payment()
    {
        var printer = new Domain.Printer
        {
            Fiscal = new Domain.FiscalPrinterSettings { OperatorId = 10, DefaultDepartment = 2 },
        };
        var payload = new Domain.PrintJobPayload
        {
            Type = "fiscal-receipt",
            FinalTotal = 12.5m,
            PaymentMethod = "card",
            Items =
            [
                new Domain.PrintJobItem { Name = "Pizza", Quantity = 1, UnitPrice = 12.5m, VatGroup = 2 },
            ],
        };

        var xml = EpsonFiscalXmlBuilder.BuildFiscalReceiptXml(payload, printer);

        Assert.Contains("operator=\"10\"", xml, StringComparison.Ordinal);
        Assert.Contains("description=\"Pizza\"", xml, StringComparison.Ordinal);
        Assert.Contains("paymentType=\"2\"", xml, StringComparison.Ordinal);
        Assert.Contains("payment=\"12.50\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNonFiscalBillXml_uses_printNormal_lines()
    {
        var printer = new Domain.Printer { Fiscal = new Domain.FiscalPrinterSettings { OperatorId = 1 } };
        var payload = new Domain.PrintJobPayload
        {
            Type = "bill",
            RestaurantName = "Trattoria",
            Items = [new Domain.PrintJobItem { Name = "Caffe", Quantity = 2, UnitPrice = 1.5m }],
            FinalTotal = 3m,
        };

        var xml = EpsonFiscalXmlBuilder.BuildNonFiscalBillXml(payload, printer);

        Assert.Contains("<printerNonFiscal>", xml, StringComparison.Ordinal);
        Assert.Contains("data=\"Trattoria\"", xml, StringComparison.Ordinal);
        Assert.Contains("2x Caffe 3.00", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOpenDrawerXml_uses_printerCommand()
    {
        var xml = EpsonFiscalXmlBuilder.BuildOpenDrawerXml(new Domain.Printer
        {
            Fiscal = new Domain.FiscalPrinterSettings { OperatorId = 3 },
        });

        Assert.Contains("<openDrawer operator=\"3\" />", xml, StringComparison.Ordinal);
    }
}
