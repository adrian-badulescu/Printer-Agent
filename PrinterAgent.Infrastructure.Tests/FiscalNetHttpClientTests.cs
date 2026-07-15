using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalNetHttpClientTests
{
    [Fact]
    public void ParseResponse_success_from_multiline_text()
    {
        const string body = "BONOK=1\n0042\n";
        var result = FiscalNetHttpClient.ParseResponse(body);

        Assert.True(result.Success);
        Assert.Equal("0042", result.FiscalReceiptNumber);
    }

    [Fact]
    public void ParseResponse_failure_when_bonok_zero()
    {
        const string body = "BONOK=0\n";
        var result = FiscalNetHttpClient.ParseResponse(body);

        Assert.False(result.Success);
        Assert.Equal("BONOK=0", result.ErrorCode);
    }

    [Fact]
    public void ParseResponse_failure_when_bonok_minus_one()
    {
        const string body = "BONOK=-1\n";
        var result = FiscalNetHttpClient.ParseResponse(body);

        Assert.False(result.Success);
        Assert.Equal("BONOK=-1", result.ErrorCode);
    }
}
