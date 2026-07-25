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
    public void Build_includes_restaurant_header_before_items()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "fiscal-receipt",
                PaymentMethod = "cash",
                FinalTotal = 10m,
                RestaurantName = "Cafe Roma",
                RegistrationNumber = "RO555",
                Items = [new PrintJobItem { Name = "Cafea", Quantity = 1, UnitPrice = 10m }]
            }
        };

        var lines = FiscalNetReceiptLineBuilder.Build(job, new Printer { Fiscal = new FiscalPrinterSettings() });

        Assert.Equal("TL^CAFE ROMA", lines[0]);
        Assert.Equal("TL^REG. NO: RO555", lines[1]);
        Assert.Contains("S^Cafea^1000^1000^buc^1^1", lines);
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

    [Fact]
    public void BuildStorno_emits_VS_lines_reference_and_payment()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "fiscal-storno-reso",
                PaymentMethod = "cash",
                FiscalReferenceReceiptNumber = "0042",
                FiscalReferenceZReport = "0001",
                FiscalReferenceDate = "22072026",
                Items =
                [
                    new PrintJobItem { Name = "ARTICOL STORNO", Quantity = 1, UnitPrice = 6m, VatGroup = 1, Department = 1 }
                ]
            }
        };

        var lines = FiscalNetReceiptLineBuilder.BuildStorno(job, new Printer { Fiscal = new FiscalPrinterSettings() });

        Assert.Contains("TL^STORNO BON 0042 Z0001 22072026", lines);
        Assert.Contains("VS^ARTICOL STORNO^600^1000^buc^1^1", lines);
        Assert.Contains("P^1^600", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("S^", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStornoReferenceLine_formats_receipt_z_and_date()
    {
        var line = FiscalNetReceiptLineBuilder.BuildStornoReferenceLine(new PrintJobPayload
        {
            FiscalReferenceReceiptNumber = "99",
            FiscalReferenceZReport = "3",
            FiscalReferenceDate = "01012026",
        });

        Assert.Equal("STORNO BON 99 Z3 01012026", line);
    }
}
