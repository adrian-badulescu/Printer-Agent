using Microsoft.Extensions.Logging.Abstractions;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class EpsonFiscalCommandHandlerTests
{
    [Fact]
    public void CanHandle_only_epson_fiscal_printers()
    {
        var handler = new EpsonFiscalCommandHandler(null!, NullLogger<EpsonFiscalCommandHandler>.Instance);

        Assert.True(handler.CanHandle(new Printer { Type = PrinterTypes.EpsonFiscal }));
        Assert.False(handler.CanHandle(new Printer { Type = PrinterTypes.FiscalNet }));
        Assert.False(handler.CanHandle(new Printer { Type = PrinterTypes.EscPos }));
    }

    [Fact]
    public async Task ExecuteAsync_unsupported_command_returns_failed()
    {
        var handler = new EpsonFiscalCommandHandler(null!, NullLogger<EpsonFiscalCommandHandler>.Instance);
        var result = await handler.ExecuteAsync(
            new Printer { Type = PrinterTypes.EpsonFiscal, Name = "Epson" },
            new FiscalCommandRequest { Command = "unknown" });

        Assert.False(result.Success);
        Assert.Equal("UNSUPPORTED_FISCAL_COMMAND", result.ErrorCode);
    }
}
