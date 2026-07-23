using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
var useHttps = HasTruthyFlag(args, "--https")
    || IsTruthyEnvironmentVariable("FPMATE_STUB_HTTPS");

var port = int.TryParse(Environment.GetEnvironmentVariable("FPMATE_STUB_PORT"), out var p)
    ? p
    : useHttps
        ? PrinterAgent.Domain.PrinterTypes.DefaultEpsonFpMatePort
        : PrinterAgent.Domain.PrinterTypes.DefaultEpsonFpMateDevPort;

var bindHost = Environment.GetEnvironmentVariable("FPMATE_STUB_BIND")?.Trim();
if (string.IsNullOrWhiteSpace(bindHost))
    bindHost = "0.0.0.0";

if (IsPortInUse(port))
{
    WritePortInUseHelp(port);
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

X509Certificate2? httpsCertificate = null;
if (useHttps)
{
    httpsCertificate = FpMateStubSelfSignedCertificate.Create(bindHost);
    builder.WebHost.ConfigureKestrel(serverOptions =>
        ConfigureKestrelListen(serverOptions, bindHost, port, httpsCertificate));
}

var app = builder.Build();

var receiptCounter = 0;
var logger = new FpMateStubRequestLogger(FpMateStubRequestLogger.ResolveLogPath());

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
        case "fiscalDocument":
        case "nonFiscal":
            receiptCounter++;
            var zReport = "0001";
            var fiscalDate = DateTime.UtcNow.ToString("ddMMyyyy");
            var documentNumber = action == "fiscalDocument" ? receiptCounter.ToString("D4") : null;
            responseXml = BuildSoapResponse(
                success: true,
                receiptNumber: receiptCounter.ToString("D4"),
                documentNumber: documentNumber,
                zReportNumber: zReport,
                fiscalDate: fiscalDate);
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

var scheme = useHttps ? "https" : "http";
var listenUrl = $"{scheme}://{bindHost}:{port}";
var logPath = FpMateStubRequestLogger.ResolveLogPath();

logger.LogStartup(
    listenUrl + "/cgi-bin/fpmate.cgi",
    logPath,
    useHttps,
    httpsCertificate?.Thumbprint,
    port);

try{
    if (useHttps)
        app.Run();
    else
        app.Run(listenUrl);

    return 0;
}
catch (IOException ex) when (IsAddressInUse(ex))
{
    WritePortInUseHelp(port);
    return 1;
}

static bool IsPortInUse(int port) =>
    IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners()
        .Any(endpoint => endpoint.Port == port);

static bool IsAddressInUse(Exception ex) =>
    ex is IOException
    && (ex.InnerException is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase));

static void WritePortInUseHelp(int port)
{
    Console.Error.WriteLine($"FpMate stub: port {port} is already in use.");
    Console.Error.WriteLine("Another stub (HTTP or HTTPS) or another app is listening on this port.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Fix:");
    Console.Error.WriteLine($"  1. Stop the other process:  netstat -ano | findstr \":{port}\"");
    Console.Error.WriteLine("     then:  taskkill /PID <pid> /F");
    Console.Error.WriteLine($"  2. Or use another port:  set FPMATE_STUB_PORT=9103");
    Console.Error.WriteLine("     (update agent.json fiscal port + useHttps to match)");
}
static void ConfigureKestrelListen(
    Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options,
    string bindHost,
    int port,
    X509Certificate2 certificate)
{
    void ConfigureListen(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions) =>
        listenOptions.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
        {
            ServerCertificate = certificate,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });
    if (bindHost is "0.0.0.0" or "*" or "+")
    {
        options.ListenAnyIP(port, ConfigureListen);
        return;
    }

    if (IPAddress.TryParse(bindHost, out var ip))
    {
        options.Listen(ip, port, ConfigureListen);
        return;
    }

    options.ListenAnyIP(port, ConfigureListen);
}

static bool HasTruthyFlag(string[] args, string flag) =>
    args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static bool IsTruthyEnvironmentVariable(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}

static string ClassifyAction(string innerXml)
{
    if (innerXml.Contains("openDrawer", StringComparison.OrdinalIgnoreCase))
        return "openDrawer";
    if (innerXml.Contains("queryPrinterStatus", StringComparison.OrdinalIgnoreCase))
        return "queryPrinterStatus";
    if (innerXml.Contains("printerFiscalDocument", StringComparison.OrdinalIgnoreCase))
        return "fiscalDocument";
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

static string BuildSoapResponse(
    bool success,
    string? code = null,
    string? receiptNumber = null,
    string? documentNumber = null,
    string? zReportNumber = null,
    string? fiscalDate = null)
{
    string addInfo = string.Empty;
    if (receiptNumber != null || documentNumber != null || zReportNumber != null || fiscalDate != null)
    {
        var parts = new List<string>();
        if (receiptNumber != null)
            parts.Add($"<fiscalReceiptNumber>{receiptNumber}</fiscalReceiptNumber>");
        if (documentNumber != null)
            parts.Add($"<fiscalDocumentNumber>{documentNumber}</fiscalDocumentNumber>");
        if (zReportNumber != null)
            parts.Add($"<zRepNumber>{zReportNumber}</zRepNumber>");
        if (fiscalDate != null)
            parts.Add($"<fiscalDate>{fiscalDate}</fiscalDate>");

        addInfo = $"<addInfo>{string.Join(string.Empty, parts)}</addInfo>";
    }

    var response = success
        ? $"""<response success="true" code="{code ?? ""}" status="0">{addInfo}</response>"""
        : $"""<response success="false" code="{code ?? "ERROR"}" status="1"></response>""";

    return $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>{response}</s:Body></s:Envelope>""";
}
