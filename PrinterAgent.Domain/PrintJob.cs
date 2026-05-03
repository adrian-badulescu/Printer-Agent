using System.Text.Json.Serialization;

namespace PrinterAgent.Domain;

public class PrintJob
{
    [JsonPropertyName("restaurantId")]
    public string RestaurantId { get; set; } = string.Empty;

    [JsonPropertyName("printerId")]
    public string PrinterId { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public PrintJobPayload Payload { get; set; } = new();

    [JsonIgnore]
    public string RedisMessageId { get; set; } = string.Empty;
}

public class PrintJobPayload
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("restaurantName")]
    public string? RestaurantName { get; set; }

    [JsonPropertyName("tableName")]
    public string? TableName { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("subTotal")]
    public decimal? SubTotal { get; set; }

    [JsonPropertyName("finalTotal")]
    public decimal? FinalTotal { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("closedAtUtc")]
    public DateTime? ClosedAtUtc { get; set; }

    [JsonPropertyName("items")]
    public List<PrintJobItem> Items { get; set; } = new();
}

public class PrintJobItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    // legacy: some payloads used "price" (single value) for printing.
    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal? UnitPrice { get; set; }
}
