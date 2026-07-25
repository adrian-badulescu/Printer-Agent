using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Printing;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public static class FiscalNetNonFiscalLineBuilder
{
    private const int MaxLineLength = 48;
    private const string Separator = "--------------------------------";

    public static string[] Build(PrintJob job, Printer printer)
    {
        _ = printer;
        var lines = new List<string>();

        var bundledHeader = Path.Combine(AppContext.BaseDirectory, ReceiptHeaderAsciiReader.FileName);
        foreach (var header in ReceiptHeaderAsciiReader.ReadLines(bundledPath: bundledHeader))
            lines.Add(ToTextLine(header));

        lines.Add(ToTextLine("Non Fiscal"));

        var restaurantName = ReceiptRestaurantHeaderHelper.SafeAscii(job.Payload.RestaurantName).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(restaurantName))
            lines.Add(ToTextLine(restaurantName));

        var registrationLine = ReceiptRestaurantHeaderHelper.SafeAscii(
            ReceiptRestaurantHeaderHelper.FormatRegistrationLine(job.Payload.RegistrationNumber)).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(registrationLine))
            lines.Add(ToTextLine(registrationLine));

        var orderId = SafeAscii(job.Payload.OrderId);
        if (!string.IsNullOrWhiteSpace(orderId))
            lines.Add(ToTextLine($"Order:{orderId}"));

        var tableName = SafeAscii(job.Payload.TableName);
        if (!string.IsNullOrWhiteSpace(tableName))
            lines.Add(ToTextLine($"TABLE: {tableName}"));

        if (job.Payload.ClosedAtUtc is { } closedAt)
            lines.Add(ToTextLine($"DATE: {closedAt:yyyy-MM-dd HH:mm} UTC"));

        lines.Add(ToTextLine(Separator));

        decimal computed = 0m;
        foreach (var item in job.Payload.Items)
        {
            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var unit = FiscalNetReceiptLineBuilder.RoundMoney(item.UnitPrice ?? item.Price);
            var lineTotal = unit * qty;
            computed += lineTotal;

            lines.Add(ToTextLine($"{qty}x {SafeAscii(item.Name)}"));
            var currency = SafeAscii(job.Payload.Currency);
            var amount = string.IsNullOrWhiteSpace(currency)
                ? lineTotal.ToString("F2")
                : $"{lineTotal:F2} {currency}";
            lines.Add(ToTextLine(amount));
        }

        lines.Add(ToTextLine(Separator));

        var final = job.Payload.FinalTotal ?? computed;
        var finalCurrency = SafeAscii(job.Payload.Currency);
        var finalStr = string.IsNullOrWhiteSpace(finalCurrency)
            ? final.ToString("F2")
            : $"{final:F2} {finalCurrency}";
        lines.Add(ToTextLine($"TOTAL: {finalStr}"));

        return lines.ToArray();
    }

    private static string ToTextLine(string text) =>
        $"TL^{FiscalNetReceiptLineBuilder.Truncate(FiscalNetReceiptLineBuilder.Sanitize(text), MaxLineLength)}";

    private static string SafeAscii(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value.Select(c => c <= 127 ? c : '?').ToArray());
    }
}
