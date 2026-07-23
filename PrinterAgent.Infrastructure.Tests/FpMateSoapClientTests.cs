using PrinterAgent.Infrastructure.Printing.Fiscal;
using Xunit;

namespace PrinterAgent.Infrastructure.Tests;

public sealed class FpMateSoapClientTests
{
    [Fact]
    public void Parse_success_with_fiscal_receipt_number()
    {
        const string body = """
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <response success="true" code="" status="0">
                  <addInfo>
                    <elementList>fiscalReceiptNumber</elementList>
                    <fiscalReceiptNumber>0042</fiscalReceiptNumber>
                  </addInfo>
                </response>
              </s:Body>
            </s:Envelope>
            """;

        var result = FpMateFiscalResponse.Parse(body);

        Assert.True(result.Success);
        Assert.Equal("0042", result.FiscalReceiptNumber);
    }

    [Fact]
    public void Parse_success_with_document_z_and_date()
    {
        const string body = """
            <?xml version="1.0" encoding="utf-8"?>
            <response success="true" code="" status="0">
              <addInfo>
                <fiscalReceiptNumber>0042</fiscalReceiptNumber>
                <fiscalDocumentNumber>1001</fiscalDocumentNumber>
                <zRepNumber>0001</zRepNumber>
                <fiscalDate>22072026</fiscalDate>
              </addInfo>
            </response>
            """;

        var result = FpMateFiscalResponse.Parse(body);
        var jobResult = result.ToPrintJobResult();

        Assert.True(result.Success);
        Assert.Equal("1001", result.FiscalDocumentNumber);
        Assert.Equal("0001", result.ZReportNumber);
        Assert.Equal("22072026", result.FiscalDate);
        Assert.Equal("1001", jobResult.FiscalNumber);
        Assert.Equal("0001", jobResult.ZReportNumber);
    }

    [Fact]
    public void Parse_failure_when_success_false()
    {
        const string body = """<response success="false" code="FP_NO_ANSWER" status="1"></response>""";
        var result = FpMateFiscalResponse.Parse(body);

        Assert.False(result.Success);
        Assert.Equal("FP_NO_ANSWER", result.ErrorCode);
    }

    [Fact]
    public void ResolveFpmateUrl_https_default_port_443()
    {
        var url = FpMateSoapClient.ResolveFpmateUrl(new Domain.Printer
        {
            IpAddress = "192.168.0.50",
            Port = 443,
            Fiscal = new Domain.FiscalPrinterSettings { UseHttps = true },
        });

        Assert.Equal("https://192.168.0.50/cgi-bin/fpmate.cgi", url);
    }

    [Fact]
    public void ResolveFpmateUrl_http_dev_stub_port()
    {
        var url = FpMateSoapClient.ResolveFpmateUrl(new Domain.Printer
        {
            IpAddress = "127.0.0.1",
            Port = 9102,
            Fiscal = new Domain.FiscalPrinterSettings { UseHttps = false },
        });

        Assert.Equal("http://127.0.0.1:9102/cgi-bin/fpmate.cgi", url);
    }

    [Fact]
    public void WrapSoapEnvelope_includes_inner_xml()
    {
        var soap = FpMateSoapClient.WrapSoapEnvelope("<printerCommand></printerCommand>");
        Assert.Contains("<printerCommand></printerCommand>", soap, StringComparison.Ordinal);
        Assert.Contains("soap/envelope", soap, StringComparison.Ordinal);
    }
}
