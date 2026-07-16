using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalNetCommandHandlerTests
{
    [Fact]
    public void MapCommandToLines_open_drawer_returns_ds_caret()
    {
        var lines = FiscalNetCommandHandler.MapCommandToLines(FiscalCommandTypes.OpenDrawer);

        Assert.NotNull(lines);
        Assert.Single(lines);
        Assert.Equal("DS^", lines[0]);
    }

    [Theory]
    [InlineData("report-x")]
    [InlineData("")]
    [InlineData("   ")]
    public void MapCommandToLines_unknown_command_returns_null(string command)
    {
        Assert.Null(FiscalNetCommandHandler.MapCommandToLines(command));
    }

    [Fact]
    public void CanHandle_returns_true_for_fiscalnet_printer()
    {
        var handler = new FiscalNetCommandHandler(null!, null!);
        var printer = new Printer { Type = "fiscalnet", Port = 65400 };

        Assert.True(handler.CanHandle(printer));
    }

    [Fact]
    public void CanHandle_returns_false_for_escpos_printer()
    {
        var handler = new FiscalNetCommandHandler(null!, null!);
        var printer = new Printer { Type = "escpos", Port = 9100 };

        Assert.False(handler.CanHandle(printer));
    }
}
