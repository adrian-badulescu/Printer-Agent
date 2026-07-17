using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FpMateSoapClient : IEpsonFiscalClient
{
    private const string FpMatePath = "/cgi-bin/fpmate.cgi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FpMateSoapClient> _logger;

    public FpMateSoapClient(IHttpClientFactory httpClientFactory, ILogger<FpMateSoapClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PrintJobResult> SendXmlAsync(
        Printer printer,
        string innerXml,
        CancellationToken cancellationToken = default)
    {
        var response = await PostSoapAsync(printer, innerXml, cancellationToken).ConfigureAwait(false);
        return response.ToPrintJobResult();
    }

    public async Task<bool> IsReachableAsync(Printer printer, CancellationToken cancellationToken = default)
    {
        var statusXml = EpsonFiscalXmlBuilder.BuildQueryStatusXml(printer);
        var response = await PostSoapAsync(printer, statusXml, cancellationToken, probeTimeoutSeconds: 5)
            .ConfigureAwait(false);
        return response.Success;
    }

    internal async Task<FpMateFiscalResponse> PostSoapAsync(
        Printer printer,
        string innerXml,
        CancellationToken cancellationToken,
        int? probeTimeoutSeconds = null)
    {
        var url = ResolveFpmateUrl(printer);
        var soap = WrapSoapEnvelope(innerXml);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(soap, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        ApplyBasicAuth(request, printer);

        var client = CreateClient(printer, probeTimeoutSeconds);
        _logger.LogDebug("FpMate SOAP POST {Url}", url);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("FpMate HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(body));
            return FpMateFiscalResponse.Failed(((int)response.StatusCode).ToString(), Truncate(body), body);
        }

        return FpMateFiscalResponse.Parse(body);
    }

    internal static string WrapSoapEnvelope(string innerXml) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
        innerXml +
        "</s:Body></s:Envelope>";

    internal static string ResolveFpmateUrl(Printer printer)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        if (!string.IsNullOrWhiteSpace(fiscal.FpmateBaseUrl))
            return fiscal.FpmateBaseUrl.Trim().TrimEnd('/') + FpMatePath;

        var scheme = fiscal.UseHttps ? "https" : "http";
        var port = printer.Port > 0 ? printer.Port : PrinterTypes.DefaultEpsonFpMatePort;
        var host = string.IsNullOrWhiteSpace(printer.IpAddress) ? "127.0.0.1" : printer.IpAddress.Trim();
        var defaultPort = fiscal.UseHttps ? 443 : 80;
        var portSuffix = port == defaultPort ? string.Empty : $":{port}";
        return $"{scheme}://{host}{portSuffix}{FpMatePath}";
    }

    private HttpClient CreateClient(Printer printer, int? probeTimeoutSeconds)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var timeoutMs = probeTimeoutSeconds.HasValue
            ? probeTimeoutSeconds.Value * 1000
            : fiscal.TimeoutMs >= 5000 ? fiscal.TimeoutMs : 120_000;

        var client = _httpClientFactory.CreateClient("FpMate");
        client.Timeout = TimeSpan.FromMilliseconds(timeoutMs + 5_000);
        return client;
    }

    private static void ApplyBasicAuth(HttpRequestMessage request, Printer printer)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        if (string.IsNullOrWhiteSpace(fiscal.WebUser))
            return;

        var password = fiscal.WebPassword ?? string.Empty;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{fiscal.WebUser}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
