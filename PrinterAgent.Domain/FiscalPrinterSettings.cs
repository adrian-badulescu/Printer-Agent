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
    public bool UseHttps { get; set; }
}
