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
    public void BuildFiscalReceiptXml_includes_restaurant_and_order_header_messages()
    {
        var printer = new Domain.Printer
        {
            Fiscal = new Domain.FiscalPrinterSettings { OperatorId = 1, DefaultDepartment = 1 },
        };
        var payload = new Domain.PrintJobPayload
        {
            Type = "fiscal-receipt",
            RestaurantName = "Trattoria Roma",
            OrderId = "order-42",
            FinalTotal = 5m,
            PaymentMethod = "cash",
            Items = [new Domain.PrintJobItem { Name = "Caffe", Quantity = 1, UnitPrice = 5m, VatGroup = 1 }],
        };

        var xml = EpsonFiscalXmlBuilder.BuildFiscalReceiptXml(payload, printer);

        Assert.StartsWith(
            "<printerFiscalReceipt><printRecMessage operator=\"1\" messageType=\"1\" index=\"1\" message=\"Trattoria Roma\" />"
            + "<printRecMessage operator=\"1\" messageType=\"1\" index=\"2\" message=\"Order: order-42\" />"
            + "<beginFiscalReceipt operator=\"1\" />",
            xml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFiscalReceiptXml_skips_local_order_id_header()
    {
        var printer = new Domain.Printer { Fiscal = new Domain.FiscalPrinterSettings { OperatorId = 1 } };
        var payload = new Domain.PrintJobPayload
        {
            Type = "fiscal-receipt",
            RestaurantName = "Trattoria",
            OrderId = "local-offline-1",
            FinalTotal = 1m,
            Items = [new Domain.PrintJobItem { Name = "Tea", Quantity = 1, UnitPrice = 1m }],
        };

        var xml = EpsonFiscalXmlBuilder.BuildFiscalReceiptXml(payload, printer);

        Assert.Contains("message=\"Trattoria\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Order:", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("index=\"2\"", xml, StringComparison.Ordinal);
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
