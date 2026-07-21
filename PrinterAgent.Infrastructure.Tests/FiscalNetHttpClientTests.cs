using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FiscalNetHttpClientTests
{
    [Fact]
    public void ResolveFiscalHttpScheme_fiscalnet_uses_http_even_when_useHttps_true()
    {
        var printer = new Printer
        {
            Type = PrinterTypes.FiscalNet,
            Port = 65400,
            Fiscal = new FiscalPrinterSettings { UseHttps = true },
        };

        Assert.Equal("http", PrinterTypes.ResolveFiscalHttpScheme(printer));
    }

    [Fact]
    public void ResolveFiscalHttpScheme_epson_respects_useHttps()
    {
        var printer = new Printer
        {
            Type = PrinterTypes.EpsonFiscal,
            Port = 443,
            Fiscal = new FiscalPrinterSettings { UseHttps = true },
        };

        Assert.Equal("https", PrinterTypes.ResolveFiscalHttpScheme(printer));
    }

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
    public void ParseResponse_failure_when_escpos_emulator_receipt_status_json()
    {
        const string body =
            """{"ReceiptStatus": false,"ReceiptNumber": "","ReceiptInfo": "","ErrorCode": "","ErrorInfo": ""}""";

        var result = FiscalNetHttpClient.ParseResponse(body);

        Assert.False(result.Success);
        Assert.Equal("NOT_FISCALNET_API", result.ErrorCode);
        Assert.Contains("ReceiptStatus", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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
