using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalNetNonFiscalLineBuilderTests
{
    [Fact]
    public void Build_emits_only_TL_lines_for_bill_payload()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "bill",
                OrderId = "ord-1",
                RestaurantName = "Test Restaurant",
                TableName = "T5",
                Currency = "RON",
                FinalTotal = 25m,
                ClosedAtUtc = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc),
                Items =
                [
                    new PrintJobItem { Name = "Pizza", Quantity = 2, UnitPrice = 12.50m }
                ]
            }
        };

        var lines = FiscalNetNonFiscalLineBuilder.Build(job, new Printer());

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.StartsWith("TL^", line));
        Assert.DoesNotContain(lines, line => line.StartsWith("S^", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("P^", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("CF^", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Non Fiscal", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("TEST RESTAURANT", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Order:ord-1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("TABLE: T5", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("2x Pizza", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("TOTAL: 25.00 RON", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_sanitizes_caret_in_item_names()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "bill",
                OrderId = "ord-2",
                Items = [new PrintJobItem { Name = "Bad^Name", Quantity = 1, UnitPrice = 10m }]
            }
        };

        var lines = FiscalNetNonFiscalLineBuilder.Build(job, new Printer());

        Assert.Contains(lines, line => line == "TL^1x BadName");
    }

    [Fact]
    public void Build_omits_order_line_when_order_id_missing()
    {
        var job = new PrintJob
        {
            Payload = new PrintJobPayload
            {
                Type = "bill",
                OrderId = "",
                RestaurantName = "Test Restaurant",
                Items = [new PrintJobItem { Name = "Pizza", Quantity = 1, UnitPrice = 10m }]
            }
        };

        var lines = FiscalNetNonFiscalLineBuilder.Build(job, new Printer());

        Assert.DoesNotContain(lines, line => line.Contains("Order:", StringComparison.Ordinal));
    }
}
