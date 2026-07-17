using System.Text;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var receiptCounter = 0;

app.MapPost("/cgi-bin/fpmate.cgi", async (HttpRequest request) =>
{
    var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] POST /cgi-bin/fpmate.cgi");

    var innerXml = ExtractSoapBody(body);
    if (innerXml.Contains("openDrawer", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Content(BuildSoapResponse(success: true), "text/xml", Encoding.UTF8);
    }

    if (innerXml.Contains("queryPrinterStatus", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Content(BuildSoapResponse(success: true), "text/xml", Encoding.UTF8);
    }

    if (innerXml.Contains("printerFiscalReceipt", StringComparison.OrdinalIgnoreCase)
        || innerXml.Contains("printerNonFiscal", StringComparison.OrdinalIgnoreCase))
    {
        receiptCounter++;
        return Results.Content(
            BuildSoapResponse(success: true, receiptNumber: receiptCounter.ToString("D4")),
            "text/xml",
            Encoding.UTF8);
    }

    return Results.Content(BuildSoapResponse(success: false, code: "UNSUPPORTED"), "text/xml", Encoding.UTF8);
});

var port = int.TryParse(Environment.GetEnvironmentVariable("FPMATE_STUB_PORT"), out var p)
    ? p
    : PrinterAgent.Domain.PrinterTypes.DefaultEpsonFpMateDevPort;

Console.WriteLine($"FpMate stub listening on http://127.0.0.1:{port}/cgi-bin/fpmate.cgi");
app.Run($"http://127.0.0.1:{port}");

static string ExtractSoapBody(string soap)
{
    try
    {
        var doc = XDocument.Parse(soap);
        var body = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Body");
        return body?.ToString(SaveOptions.DisableFormatting) ?? soap;
    }
    catch
    {
        return soap;
    }
}

static string BuildSoapResponse(bool success, string? code = null, string? receiptNumber = null)
{
    var addInfo = receiptNumber == null
        ? string.Empty
        : $"<addInfo><elementList>fiscalReceiptNumber</elementList><fiscalReceiptNumber>{receiptNumber}</fiscalReceiptNumber></addInfo>";

    var response = success
        ? $"""<response success="true" code="{code ?? ""}" status="0">{addInfo}</response>"""
        : $"""<response success="false" code="{code ?? "ERROR"}" status="1"></response>""";

    return $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>{response}</s:Body></s:Envelope>""";
}
