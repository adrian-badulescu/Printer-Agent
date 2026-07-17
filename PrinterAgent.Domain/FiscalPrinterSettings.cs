using System.Text.Json.Serialization;

namespace PrinterAgent.Domain;

public sealed class FiscalPrinterSettings
{
    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 120_000;

    [JsonPropertyName("defaultVatGroup")]
    public int DefaultVatGroup { get; set; } = 1;

    [JsonPropertyName("defaultDepartment")]
    public int DefaultDepartment { get; set; } = 1;

    [JsonPropertyName("useHttps")]
    public bool UseHttps { get; set; } = true;

    /// <summary>Epson fiscal operator id (1–12) sent in ePOS-Fiscal-Print XML.</summary>
    [JsonPropertyName("operatorId")]
    public int OperatorId { get; set; } = 1;

    /// <summary>Optional override for the full fpmate base URL without path (e.g. https://192.168.0.10).</summary>
    [JsonPropertyName("fpmateBaseUrl")]
    public string? FpmateBaseUrl { get; set; }

    /// <summary>Optional HTTP basic auth user for the printer web server.</summary>
    [JsonPropertyName("webUser")]
    public string? WebUser { get; set; }

    /// <summary>Optional HTTP basic auth password for the printer web server.</summary>
    [JsonPropertyName("webPassword")]
    public string? WebPassword { get; set; }
}
