using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FiscalNetHttpClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FiscalNetHttpClient> _logger;

    public FiscalNetHttpClient(IHttpClientFactory httpClientFactory, ILogger<FiscalNetHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FiscalNetResponse> SendReceiptAsync(
        Printer printer,
        string[] receiptLines,
        CancellationToken cancellationToken = default)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var scheme = fiscal.UseHttps ? "https" : "http";
        var port = printer.Port > 0 ? printer.Port : 65400;
        var host = string.IsNullOrWhiteSpace(printer.IpAddress) ? "127.0.0.1" : printer.IpAddress.Trim();
        var timeoutMs = fiscal.TimeoutMs >= 5000 ? fiscal.TimeoutMs : 120_000;
        var url = $"{scheme}://{host}:{port}/api/Receipt";

        var json = JsonSerializer.Serialize(receiptLines);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var client = _httpClientFactory.CreateClient("FiscalNet");
        client.Timeout = TimeSpan.FromMilliseconds(timeoutMs + 5_000);

        _logger.LogDebug("FiscalNet POST {Url} lines={Count}", url, receiptLines.Length);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("FiscalNet HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(body));
            return BuildFailureResponse(((int)response.StatusCode).ToString(), body);
        }

        return ParseResponse(body);
    }

    public static FiscalNetResponse ParseResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return BuildFailureResponse("EMPTY_RESPONSE", body);

        var bonOk = TryReadBonOk(body);
        if (bonOk == 0)
            return BuildFailureResponse("BONOK=0", body);

        if (bonOk == -1)
            return BuildFailureResponse("BONOK=-1", body);

        var receiptNumber = TryReadReceiptNumber(body);
        var success = bonOk == 1 || (bonOk is null && !string.IsNullOrWhiteSpace(receiptNumber));
        if (success)
        {
            return new FiscalNetResponse
            {
                Success = true,
                FiscalReceiptNumber = receiptNumber,
                RawResponse = body,
            };
        }

        return BuildFailureResponse("BON_FAILED", body);
    }

    private static FiscalNetResponse BuildFailureResponse(string fallbackCode, string? body)
    {
        var parsed = FiscalDeviceErrorParser.TryParse(body);
        return new FiscalNetResponse
        {
            Success = false,
            ErrorCode = parsed?.ErrorCode ?? fallbackCode,
            DeviceErrorCode = parsed?.DeviceErrorCode,
            ErrorMessage = parsed?.RawSnippet,
            RawResponse = body,
        };
    }

    private static int? TryReadBonOk(string body)
    {
        foreach (var line in EnumerateCandidateLines(body))
        {
            if (!line.StartsWith("BONOK=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["BONOK=".Length..].Trim();
            return int.TryParse(value, out var n) ? n : null;
        }

        return null;
    }

    private static string? TryReadReceiptNumber(string body)
    {
        var lines = EnumerateCandidateLines(body).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("BONOK=", StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < lines.Count && !string.IsNullOrWhiteSpace(lines[i + 1]))
                return lines[i + 1].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("fiscalReceiptNumber", out var n))
                    return n.GetString();
                if (doc.RootElement.TryGetProperty("receiptNumber", out var r))
                    return r.GetString();
            }
        }
        catch (JsonException)
        {
            // not JSON
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateLines(string body)
    {
        if (body.TrimStart().StartsWith('['))
        {
            string[]? arr = null;
            try
            {
                arr = JsonSerializer.Deserialize<string[]>(body);
            }
            catch (JsonException)
            {
                // fall through to line split
            }

            if (arr is not null)
            {
                foreach (var line in arr)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        yield return line.Trim();
                }

                yield break;
            }
        }

        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            yield return line.Trim();
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
