using System.Globalization;
using System.Text;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public static class FiscalNetReceiptLineBuilder
{
    private const int MaxNameLength = 48;

    public static string[] Build(PrintJob job, Printer printer)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var defaultVat = ClampVatGroup(fiscal.DefaultVatGroup);
        var defaultDept = fiscal.DefaultDepartment > 0 ? fiscal.DefaultDepartment : 1;
        var lines = new List<string>();

        var restaurantName = ReceiptRestaurantHeaderHelper.SafeAscii(job.Payload.RestaurantName).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(restaurantName))
            lines.Add($"TL^{Truncate(Sanitize(restaurantName), MaxNameLength)}");

        var registrationLine = ReceiptRestaurantHeaderHelper.SafeAscii(
            ReceiptRestaurantHeaderHelper.FormatRegistrationLine(job.Payload.RegistrationNumber)).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(registrationLine))
            lines.Add($"TL^{Truncate(Sanitize(registrationLine), MaxNameLength)}");

        var tableName = ReceiptRestaurantHeaderHelper.SafeAscii(job.Payload.TableName);
        if (!string.IsNullOrWhiteSpace(tableName))
            lines.Add($"TL^{Truncate(Sanitize($"TABLE: {tableName}"), MaxNameLength)}");

        var customer = job.Payload.CustomerFiscalCode?.Trim();
        if (!string.IsNullOrWhiteSpace(customer))
            lines.Add($"CF^{Sanitize(customer)}");

        foreach (var item in job.Payload.Items)
        {
            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var unit = RoundMoney(item.UnitPrice ?? item.Price);
            var name = FormatItemDisplayName(item.Name, item.VatPercent);
            var vatGroup = item.VatGroup is >= 1 and <= 5 ? item.VatGroup.Value : defaultVat;
            var department = item.Department ?? defaultDept;
            lines.Add($"S^{name}^{FormatPrice(unit)}^{FormatQuantity(qty)}^buc^{vatGroup}^{department}");
        }

        if (!string.IsNullOrWhiteSpace(job.Payload.FooterMessage))
            lines.Add($"TL^{Truncate(Sanitize(job.Payload.FooterMessage), MaxNameLength)}");

        foreach (var paymentLine in BuildPaymentLines(job))
            lines.Add(paymentLine);

        return lines.ToArray();
    }

    /// <summary>
    /// Post-issuance storno bon: VS lines (negative sale) + reference to original receipt.
    /// See BonuriTest bon_complex.txt — VS voids/corrects item lines; ST^ is subtotal, not storno.
    /// </summary>
    public static string[] BuildStorno(PrintJob job, Printer printer)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var defaultVat = ClampVatGroup(fiscal.DefaultVatGroup);
        var defaultDept = fiscal.DefaultDepartment > 0 ? fiscal.DefaultDepartment : 1;
        var lines = new List<string>();

        var referenceLine = BuildStornoReferenceLine(job.Payload);
        if (!string.IsNullOrWhiteSpace(referenceLine))
            lines.Add($"TL^{Truncate(Sanitize(referenceLine), MaxNameLength)}");

        var customer = job.Payload.CustomerFiscalCode?.Trim();
        if (!string.IsNullOrWhiteSpace(customer))
            lines.Add($"CF^{Sanitize(customer)}");

        foreach (var item in job.Payload.Items)
        {
            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var unit = RoundMoney(item.UnitPrice ?? item.Price);
            var name = FormatItemDisplayName(item.Name, item.VatPercent);
            var vatGroup = item.VatGroup is >= 1 and <= 5 ? item.VatGroup.Value : defaultVat;
            var department = item.Department ?? defaultDept;
            lines.Add($"VS^{name}^{FormatPrice(unit)}^{FormatQuantity(qty)}^buc^{vatGroup}^{department}");
        }

        if (!string.IsNullOrWhiteSpace(job.Payload.FooterMessage))
            lines.Add($"TL^{Truncate(Sanitize(job.Payload.FooterMessage), MaxNameLength)}");

        foreach (var paymentLine in BuildPaymentLines(job))
            lines.Add(paymentLine);

        return lines.ToArray();
    }

    internal static string BuildStornoReferenceLine(PrintJobPayload payload)
    {
        var receipt = payload.FiscalReferenceReceiptNumber?.Trim();
        var zReport = payload.FiscalReferenceZReport?.Trim();
        var date = payload.FiscalReferenceDate?.Trim();

        if (string.IsNullOrWhiteSpace(receipt)
            && string.IsNullOrWhiteSpace(zReport)
            && string.IsNullOrWhiteSpace(date))
        {
            return string.Empty;
        }

        var parts = new List<string> { "STORNO" };
        if (!string.IsNullOrWhiteSpace(receipt))
            parts.Add($"BON {receipt}");
        if (!string.IsNullOrWhiteSpace(zReport))
            parts.Add($"Z{zReport}");
        if (!string.IsNullOrWhiteSpace(date))
            parts.Add(date);

        return string.Join(' ', parts);
    }

    internal static IEnumerable<string> BuildPaymentLines(PrintJob job)
    {
        var computedTotal = job.Payload.Items.Sum(i =>
        {
            var qty = i.Quantity <= 0 ? 1 : i.Quantity;
            var unit = RoundMoney(i.UnitPrice ?? i.Price);
            return unit * qty;
        });

        var total = computedTotal > 0
            ? computedTotal
            : RoundMoney(job.Payload.FinalTotal
                ?? job.Payload.SubTotal
                ?? 0m);

        if (total <= 0)
            yield break;

        var (type, amount) = MapPayment(job.Payload.PaymentMethod, total);
        var platformLabel = MapPaymentPlatformLabel(job.Payload.PaymentMethod);
        if (!string.IsNullOrWhiteSpace(platformLabel))
            yield return $"TL^{platformLabel}";

        yield return $"P^{type}^{FormatPrice(amount)}";
    }

    internal static (int Type, decimal Amount) MapPayment(string? paymentMethod, decimal total)
    {
        var method = NormalizePaymentMethod(paymentMethod);
        var type = method switch
        {
            "cash" => 1,
            "card" or "credit" => 2,
            "ticket" or "meal-ticket" => 4,
            "value-ticket" => 5,
            "voucher" => 6,
            "glovo" or "tazz" or "bolt" or "bolt-food" or "external" or "delivery-platform" => 7,
            _ => 1,
        };
        return (type, total);
    }

    /// <summary>
    /// Label printed via TL^ before P^7 so Glovo/Tazz/Bolt appear distinctly on the receipt
    /// (FiscalNet type 7 is generic "plată modernă").
    /// </summary>
    internal static string? MapPaymentPlatformLabel(string? paymentMethod)
    {
        return NormalizePaymentMethod(paymentMethod) switch
        {
            "glovo" => "GLOVO",
            "tazz" => "TAZZ",
            "bolt" or "bolt-food" => "BOLT FOOD",
            _ => null,
        };
    }

    internal static string NormalizePaymentMethod(string? paymentMethod) =>
        (paymentMethod ?? "cash").Trim().ToLowerInvariant();

    internal static int FormatPrice(decimal amount) =>
        (int)Math.Round(RoundMoney(amount) * 100m, MidpointRounding.AwayFromZero);

    internal static decimal RoundMoney(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    internal static int FormatQuantity(int quantity) =>
        quantity * 1000;

    internal static int ClampVatGroup(int value) =>
        value is >= 1 and <= 5 ? value : 1;

    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value.Where(c => c is >= ' ' and <= '~' && c != '^').ToArray();
        return new string(chars).Trim();
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    internal static string FormatItemDisplayName(string? name, decimal? vatPercent)
    {
        var sanitized = Sanitize(name);
        if (string.IsNullOrWhiteSpace(sanitized))
            return string.Empty;

        if (!vatPercent.HasValue || vatPercent.Value is < 0 or > 100)
            return Truncate(sanitized, MaxNameLength);

        var pct = vatPercent.Value % 1m == 0m
            ? ((int)vatPercent.Value).ToString(CultureInfo.InvariantCulture)
            : vatPercent.Value.ToString("0.##", CultureInfo.InvariantCulture);
        var suffix = $" TVA {pct}%";
        var maxNameLength = Math.Max(1, MaxNameLength - suffix.Length);
        return Truncate(sanitized, maxNameLength) + suffix;
    }
}
