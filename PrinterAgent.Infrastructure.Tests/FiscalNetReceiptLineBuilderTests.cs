using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalNetReceiptLineBuilderTests
{
    [Fact]
    public void Build_maps_items_payments_and_customer_code()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "fiscal-receipt",
                PaymentMethod = "cash",
                FinalTotal = 12.50m,
                CustomerFiscalCode = "RO12345678",
                Items =
                [
                    new PrintJobItem { Name = "Pizza", Quantity = 2, UnitPrice = 6.25m, VatGroup = 2, Department = 3 }
                ]
            }
        };

        var printer = new Printer
        {
            Fiscal = new FiscalPrinterSettings { DefaultVatGroup = 1, DefaultDepartment = 1 }
        };

        var lines = FiscalNetReceiptLineBuilder.Build(job, printer);

        Assert.Contains("CF^RO12345678", lines);
        Assert.Contains("S^Pizza^625^2000^buc^2^3", lines);
        Assert.Contains("P^1^1250", lines);
    }

    [Fact]
    public void Build_uses_default_vat_group_when_item_missing()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "fiscal-receipt",
                PaymentMethod = "card",
                FinalTotal = 10m,
                Items = [new PrintJobItem { Name = "Cafea", Quantity = 1, UnitPrice = 10m }]
            }
        };

        var printer = new Printer { Fiscal = new FiscalPrinterSettings { DefaultVatGroup = 4 } };
        var lines = FiscalNetReceiptLineBuilder.Build(job, printer);

        Assert.Contains("S^Cafea^1000^1000^buc^4^1", lines);
        Assert.Contains("P^2^1000", lines);
    }
}
