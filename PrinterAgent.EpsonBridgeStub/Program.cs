using System.Text;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var receiptCounter = 0;
var logger = new FpMateStubRequestLogger(FpMateStubRequestLogger.ResolveLogPath());

var port = int.TryParse(Environment.GetEnvironmentVariable("FPMATE_STUB_PORT"), out var p)
    ? p
    : PrinterAgent.Domain.PrinterTypes.DefaultEpsonFpMateDevPort;

var bindHost = Environment.GetEnvironmentVariable("FPMATE_STUB_BIND")?.Trim();
if (string.IsNullOrWhiteSpace(bindHost))
    bindHost = "0.0.0.0";

var listenUrl = $"http://{bindHost}:{port}";

app.MapPost("/cgi-bin/fpmate.cgi", async (HttpRequest request) =>
{
    var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
    var innerXml = ExtractSoapBody(body);
    var action = ClassifyAction(innerXml);

    string responseXml;
    string responseSummary;
    switch (action)
    {
        case "openDrawer":
        case "queryPrinterStatus":
            responseXml = BuildSoapResponse(success: true);
            responseSummary = $"{action} success=true";
            break;
        case "fiscalReceipt":
        case "nonFiscal":
            receiptCounter++;
            responseXml = BuildSoapResponse(success: true, receiptNumber: receiptCounter.ToString("D4"));
            responseSummary = $"{action} success=true receipt={receiptCounter:D4}";
            break;
        default:
            responseXml = BuildSoapResponse(success: false, code: "UNSUPPORTED");
            responseSummary = "unsupported success=false code=UNSUPPORTED";
            break;
    }

    logger.LogRequest(request, body, innerXml, action, responseSummary);
    return Results.Content(responseXml, "text/xml", Encoding.UTF8);
});

app.MapFallback(async (HttpRequest request) =>
{
    var body = request.ContentLength > 0
        ? await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync()
        : string.Empty;
    logger.LogRequest(
        request,
        body,
        innerXml: string.Empty,
        action: $"fallback-{request.Method}",
        responseSummary: "404 Not Found");
    return Results.NotFound("FpMate stub only handles POST /cgi-bin/fpmate.cgi");
});

logger.LogStartup($"{listenUrl}/cgi-bin/fpmate.cgi", FpMateStubRequestLogger.ResolveLogPath());
app.Run(listenUrl);

static string ClassifyAction(string innerXml)
{
    if (innerXml.Contains("openDrawer", StringComparison.OrdinalIgnoreCase))
        return "openDrawer";
    if (innerXml.Contains("queryPrinterStatus", StringComparison.OrdinalIgnoreCase))
        return "queryPrinterStatus";
    if (innerXml.Contains("printerFiscalReceipt", StringComparison.OrdinalIgnoreCase))
        return "fiscalReceipt";
    if (innerXml.Contains("printerNonFiscal", StringComparison.OrdinalIgnoreCase))
        return "nonFiscal";
    return "unsupported";
}

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
