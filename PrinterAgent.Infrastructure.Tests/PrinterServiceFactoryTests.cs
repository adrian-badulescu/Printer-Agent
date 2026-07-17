using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class PrinterServiceFactoryTests
{
    [Fact]
    public void Resolve_routes_by_printer_type()
    {
        var escPos = new EscPosPrinterService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EscPosPrinterService>.Instance,
            new TestAppConfiguration(),
            new NoOpMacCapture());
        var fiscalNet = new FiscalNetPrinterService(
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FiscalNetPrinterService>.Instance);
        var epsonFiscal = new EpsonFiscalPrinterService(
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EpsonFiscalPrinterService>.Instance);
        var factory = new PrinterServiceFactory(escPos, fiscalNet, epsonFiscal);

        Assert.Same(escPos, factory.Resolve(new Printer { Type = PrinterTypes.EscPos }));
        Assert.Same(fiscalNet, factory.Resolve(new Printer { Type = PrinterTypes.FiscalNet }));
        Assert.Same(epsonFiscal, factory.Resolve(new Printer { Type = PrinterTypes.EpsonFiscal }));
        Assert.Same(fiscalNet, factory.Resolve(new Printer { Type = PrinterTypes.EscPos, Port = 65400 }));
        Assert.Same(escPos, factory.Resolve(new Printer()));
    }

    private sealed class TestAppConfiguration : IAppConfiguration
    {
        public string RestaurantId => "test";
        public string? EnrollmentCode => null;
        public string BackendUrl => "http://localhost";
        public string BackendJwtToken => "";
        public string RedisConnectionString => "";
        public string RedisStreamKeyPrefix => "print.jobs";
        public string RedisConsumerGroup => "agents";
        public string RedisConnectionSummary => "";
        public bool HasLegacyRedisPassword => false;
        public List<Printer> Printers => [];
        public string Version => "test";
        public string UpdateSignatureSecret => "";
        public int MaxPrintRetryAttempts => 1;
        public int PrintRetryBaseDelayMs => 1;
        public int PrinterConnectTimeoutSeconds => 1;
        public bool LocalPrintEnabled => true;
        public int LocalPrintPort => 9247;
    }

    private sealed class NoOpMacCapture : IPrinterMacCapture
    {
        public Task TryPersistMacAfterSuccessfulPrintAsync(string printerId, string ipAddress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
