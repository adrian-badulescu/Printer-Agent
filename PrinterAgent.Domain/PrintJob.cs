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

    [JsonPropertyName("items")]
    public List<PrintJobItem> Items { get; set; } = new();
}

public class PrintJobItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }
}
