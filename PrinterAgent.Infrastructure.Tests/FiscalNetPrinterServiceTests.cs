using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalNetPrinterServiceTests
{
    [Fact]
    public async Task PrintAsync_bill_posts_TL_lines_to_fiscalnet()
    {
        var handler = new StubFiscalNetHandler();
        var factory = new StubHttpClientFactory(handler);
        var httpClient = new FiscalNetHttpClient(factory, NullLogger<FiscalNetHttpClient>.Instance);
        var service = new FiscalNetPrinterService(httpClient, NullLogger<FiscalNetPrinterService>.Instance);

        var job = new PrintJob
        {
            RedisMessageId = "job-1",
            Payload = new PrintJobPayload
            {
                Type = "bill",
                OrderId = "ord-1",
                RestaurantName = "Cafe",
                Items = [new PrintJobItem { Name = "Espresso", Quantity = 1, UnitPrice = 8m }]
            }
        };

        var printer = new Printer
        {
            Name = "fiscal-1",
            IpAddress = "127.0.0.1",
            Port = 65400,
            Type = PrinterTypes.FiscalNet
        };

        var result = await service.PrintAsync(printer, job);

        Assert.True(result.Success);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("TL^", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.LastBody, "S^", StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrintAsync_fiscal_receipt_still_posts_sale_lines()
    {
        var handler = new StubFiscalNetHandler();
        var factory = new StubHttpClientFactory(handler);
        var httpClient = new FiscalNetHttpClient(factory, NullLogger<FiscalNetHttpClient>.Instance);
        var service = new FiscalNetPrinterService(httpClient, NullLogger<FiscalNetPrinterService>.Instance);

        var job = new PrintJob
        {
            RedisMessageId = "job-2",
            Payload = new PrintJobPayload
            {
                Type = "fiscal-receipt",
                PaymentMethod = "cash",
                FinalTotal = 10m,
                Items = [new PrintJobItem { Name = "Cafea", Quantity = 1, UnitPrice = 10m, VatGroup = 1 }]
            }
        };

        var printer = new Printer
        {
            Name = "fiscal-1",
            IpAddress = "127.0.0.1",
            Port = 65400,
            Fiscal = new FiscalPrinterSettings { DefaultVatGroup = 1, DefaultDepartment = 1 }
        };

        var result = await service.PrintAsync(printer, job);

        Assert.True(result.Success);
        Assert.Contains("S^Cafea^1000^1000^buc^1^1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("P^1^1000", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrintAsync_rejects_unknown_payload_type()
    {
        var handler = new StubFiscalNetHandler();
        var factory = new StubHttpClientFactory(handler);
        var httpClient = new FiscalNetHttpClient(factory, NullLogger<FiscalNetHttpClient>.Instance);
        var service = new FiscalNetPrinterService(httpClient, NullLogger<FiscalNetPrinterService>.Instance);

        var job = new PrintJob
        {
            Payload = new PrintJobPayload { Type = "order", OrderId = "ord-3" }
        };

        var result = await service.PrintAsync(new Printer(), job);

        Assert.False(result.Success);
        Assert.Equal("UNSUPPORTED_PAYLOAD", result.ErrorCode);
        Assert.Null(handler.LastBody);
    }

    private sealed class StubFiscalNetHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BONOK=1\n0042\n")
            });
        }
    }

    private sealed class StubHttpClientFactory(StubFiscalNetHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("http://127.0.0.1:65400") };
    }
}
