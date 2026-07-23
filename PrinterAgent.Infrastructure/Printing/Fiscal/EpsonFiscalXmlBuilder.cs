using System.Globalization;
using System.Security;
using System.Text;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public static class EpsonFiscalXmlBuilder
{
    private const int FiscalHeaderMessageMaxLength = 46;
    private const int InvoiceMessageMaxLength = 37;

    public static string BuildPrintXml(PrintJobPayload payload, Printer printer)
    {
        var type = (payload.Type ?? string.Empty).Trim().ToLowerInvariant();
        return type switch
        {
            "bill" => BuildNonFiscalBillXml(payload, printer),
            "fiscal-receipt" => BuildFiscalReceiptXml(payload, printer),
            "fiscal-invoice" => BuildDirectInvoiceXml(payload, printer),
            "fiscal-storno-reso" => BuildCommercialRefundXml(payload, printer),
            _ => throw new ArgumentException($"Unsupported payload type '{payload.Type}'.", nameof(payload)),
        };
    }

    public static string BuildOpenDrawerXml(Printer printer)
    {
        var op = GetOperator(printer);
        return $"<printerCommand><openDrawer operator=\"{op}\" /></printerCommand>";
    }

    public static string BuildQueryStatusXml(Printer printer)
    {
        var op = GetOperator(printer);
        return $"<printerCommand><queryPrinterStatus operator=\"{op}\" statusType=\"0\" /></printerCommand>";
    }

    internal static string BuildFiscalReceiptXml(PrintJobPayload payload, Printer printer)
    {
        var op = GetOperator(printer);
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var sb = new StringBuilder();
        sb.Append("<printerFiscalReceipt>");
        AppendFiscalHeaderMessages(sb, op, payload);
        sb.Append(CultureInfo.InvariantCulture, $"<beginFiscalReceipt operator=\"{op}\" />");

        AppendFiscalItems(sb, op, payload, fiscal, useRefund: false);

        if (!string.IsNullOrWhiteSpace(payload.FooterMessage))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<printRecMessage operator=\"{op}\" messageType=\"3\" index=\"1\" font=\"4\" message=\"{Escape(payload.FooterMessage)}\" />");
        }

        AppendFiscalPayment(sb, op, payload);
        sb.Append(CultureInfo.InvariantCulture, $"<endFiscalReceipt operator=\"{op}\" />");
        sb.Append("</printerFiscalReceipt>");
        return sb.ToString();
    }

    internal static string BuildDirectInvoiceXml(PrintJobPayload payload, Printer printer)
    {
        var op = GetOperator(printer);
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var sb = new StringBuilder();
        sb.Append("<printerFiscalDocument>");

        var headerIndex = AppendInvoiceMessage(sb, op, 5, 1, payload.RestaurantName);
        headerIndex = AppendInvoiceMessage(sb, op, 5, headerIndex, payload.TableName);
        if (!string.IsNullOrWhiteSpace(payload.OrderId)
            && !payload.OrderId.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
        {
            headerIndex = AppendInvoiceMessage(sb, op, 5, headerIndex, "Order: " + payload.OrderId.Trim());
        }

        var clientIndex = 1;
        clientIndex = AppendInvoiceMessage(sb, op, 6, clientIndex, payload.CustomerName);
        clientIndex = AppendInvoiceMessage(sb, op, 6, clientIndex, payload.CustomerFiscalCode);
        clientIndex = AppendInvoiceMessage(sb, op, 6, clientIndex, payload.CustomerAddressLine1);
        AppendInvoiceMessage(sb, op, 6, clientIndex, payload.CustomerAddressLine2);

        var documentNumber = payload.DocumentNumber is >= 0 and <= 99999 ? payload.DocumentNumber.Value : 0;
        sb.Append(CultureInfo.InvariantCulture,
            $"<beginFiscalDocument operator=\"{op}\" documentType=\"directInvoice\" documentNumber=\"{documentNumber}\" />");

        AppendFiscalItems(sb, op, payload, fiscal, useRefund: false);

        AppendFiscalPayment(sb, op, payload);
        sb.Append(CultureInfo.InvariantCulture, $"<endFiscalDocument operator=\"{op}\" />");
        sb.Append("</printerFiscalDocument>");
        return sb.ToString();
    }

    internal static string BuildCommercialRefundXml(PrintJobPayload payload, Printer printer)
    {
        ValidateRefundReference(payload);

        var op = GetOperator(printer);
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var refundMessage = BuildRefundReferenceMessage(payload);
        var sb = new StringBuilder();
        sb.Append("<printerFiscalReceipt>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<printRecMessage operator=\"{op}\" messageType=\"4\" index=\"1\" message=\"{Escape(refundMessage)}\" />");

        AppendFiscalItems(sb, op, payload, fiscal, useRefund: true);
        AppendFiscalPayment(sb, op, payload);
        sb.Append(CultureInfo.InvariantCulture, $"<endFiscalReceipt operator=\"{op}\" />");
        sb.Append("</printerFiscalReceipt>");
        return sb.ToString();
    }

    internal static string BuildNonFiscalBillXml(PrintJobPayload payload, Printer printer)
    {
        var op = GetOperator(printer);
        var sb = new StringBuilder();
        sb.Append("<printerNonFiscal>");
        sb.Append(CultureInfo.InvariantCulture, $"<beginNonFiscal operator=\"{op}\" />");

        AppendPrintNormal(sb, op, payload.RestaurantName);
        AppendPrintNormal(sb, op, payload.TableName);
        if (!string.IsNullOrWhiteSpace(payload.OrderId)
            && !payload.OrderId.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
        {
            AppendPrintNormal(sb, op, "Order: " + payload.OrderId);
        }

        foreach (var item in payload.Items)
            AppendPrintNormal(sb, op, FormatBillLine(item));

        if (payload.FinalTotal.HasValue)
        {
            var currency = string.IsNullOrWhiteSpace(payload.Currency) ? "EUR" : payload.Currency.Trim();
            AppendPrintNormal(sb, op, $"Total: {payload.FinalTotal.Value:0.00} {currency}");
        }

        AppendPrintNormal(sb, op, payload.FooterMessage);
        sb.Append(CultureInfo.InvariantCulture, $"<endNonFiscal operator=\"{op}\" />");
        sb.Append("</printerNonFiscal>");
        return sb.ToString();
    }

    internal static int GetOperator(Printer printer)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        var op = fiscal.OperatorId;
        return op is >= 1 and <= 12 ? op : 1;
    }

    internal static int MapPaymentType(string? paymentMethod)
    {
        var normalized = (paymentMethod ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "card" or "credit" or "carta" or "cardul" => 2,
            "ticket" or "tickets" => 4,
            _ => 0,
        };
    }

    internal static string MapPaymentDescription(string? paymentMethod)
    {
        var normalized = (paymentMethod ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "cash" => "CONTANTE",
            "card" or "credit" => "CARTA",
            "" => "CONTANTE",
            _ => paymentMethod!.Trim().ToUpperInvariant(),
        };
    }

    internal static string FormatAmount(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    internal static decimal SumItems(PrintJobPayload payload)
    {
        decimal total = 0m;
        foreach (var item in payload.Items)
        {
            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var unit = item.UnitPrice ?? item.Price;
            total += unit * qty;
        }

        return total;
    }

    internal static string BuildRefundReferenceMessage(PrintJobPayload payload)
    {
        var zReport = payload.FiscalReferenceZReport!.Trim();
        var receiptNumber = payload.FiscalReferenceReceiptNumber!.Trim();
        var date = FormatRefundReferenceDate(payload.FiscalReferenceDate, payload.ClosedAtUtc);
        return $"REFUND {zReport} {receiptNumber} {date}";
    }

    internal static string FormatRefundReferenceDate(string? referenceDate, DateTime? closedAtUtc)
    {
        if (!string.IsNullOrWhiteSpace(referenceDate))
        {
            var trimmed = referenceDate.Trim();
            if (trimmed.Length == 8 && trimmed.All(char.IsDigit))
                return trimmed;

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                || DateTime.TryParse(trimmed, out parsed))
            {
                return parsed.ToString("ddMMyyyy", CultureInfo.InvariantCulture);
            }
        }

        if (closedAtUtc.HasValue)
            return closedAtUtc.Value.ToString("ddMMyyyy", CultureInfo.InvariantCulture);

        throw new ArgumentException("Fiscal reference date is required for refund documents.", nameof(referenceDate));
    }

    private static void ValidateRefundReference(PrintJobPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.FiscalReferenceZReport)
            || string.IsNullOrWhiteSpace(payload.FiscalReferenceReceiptNumber))
        {
            throw new ArgumentException(
                "Fiscal refund reference requires fiscalReferenceZReport and fiscalReferenceReceiptNumber.",
                nameof(payload));
        }

        if (string.IsNullOrWhiteSpace(payload.FiscalReferenceDate) && !payload.ClosedAtUtc.HasValue)
        {
            throw new ArgumentException(
                "Fiscal refund reference requires fiscalReferenceDate or closedAtUtc.",
                nameof(payload));
        }
    }

    private static void AppendFiscalItems(
        StringBuilder sb,
        int op,
        PrintJobPayload payload,
        FiscalPrinterSettings fiscal,
        bool useRefund)
    {
        foreach (var item in payload.Items)
        {
            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var unitPrice = item.UnitPrice ?? item.Price;
            var department = item.Department ?? item.VatGroup ?? fiscal.DefaultDepartment;
            if (department <= 0)
                department = fiscal.DefaultDepartment > 0 ? fiscal.DefaultDepartment : 1;

            var tag = useRefund ? "printRecRefund" : "printRecItem";
            sb.Append(CultureInfo.InvariantCulture,
                $"<{tag} operator=\"{op}\" description=\"{Escape(item.Name)}\" quantity=\"{qty}\" unitPrice=\"{FormatAmount(unitPrice)}\" department=\"{department}\" justification=\"1\" />");
        }
    }

    private static void AppendFiscalPayment(StringBuilder sb, int op, PrintJobPayload payload)
    {
        var total = payload.FinalTotal ?? SumItems(payload);
        var paymentType = MapPaymentType(payload.PaymentMethod);
        var paymentDescription = MapPaymentDescription(payload.PaymentMethod);
        sb.Append(CultureInfo.InvariantCulture,
            $"<printRecTotal operator=\"{op}\" description=\"{Escape(paymentDescription)}\" payment=\"{FormatAmount(total)}\" paymentType=\"{paymentType}\" index=\"0\" justification=\"1\" />");
    }

    private static void AppendFiscalHeaderMessages(StringBuilder sb, int op, PrintJobPayload payload)
    {
        var index = 1;
        index = AppendFiscalHeaderMessage(sb, op, index, payload.RestaurantName);
        if (!string.IsNullOrWhiteSpace(payload.OrderId)
            && !payload.OrderId.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
        {
            index = AppendFiscalHeaderMessage(sb, op, index, "Order: " + payload.OrderId.Trim());
        }
    }

    private static int AppendFiscalHeaderMessage(StringBuilder sb, int op, int index, string? text)
    {
        if (index is < 1 or > 9 || string.IsNullOrWhiteSpace(text))
            return index;

        var message = Truncate(text.Trim(), FiscalHeaderMessageMaxLength);
        sb.Append(CultureInfo.InvariantCulture,
            $"<printRecMessage operator=\"{op}\" messageType=\"1\" index=\"{index}\" message=\"{Escape(message)}\" />");
        return index + 1;
    }

    private static int AppendInvoiceMessage(StringBuilder sb, int op, int messageType, int index, string? text)
    {
        if (index is < 1 or > 9 || string.IsNullOrWhiteSpace(text))
            return index;

        var message = Truncate(text.Trim(), InvoiceMessageMaxLength);
        sb.Append(CultureInfo.InvariantCulture,
            $"<printRecMessage operator=\"{op}\" messageType=\"{messageType}\" index=\"{index}\" message=\"{Escape(message)}\" />");
        return index + 1;
    }

    private static void AppendPrintNormal(StringBuilder sb, int op, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        sb.Append(CultureInfo.InvariantCulture,
            $"<printNormal operator=\"{op}\" font=\"2\" data=\"{Escape(text)}\" />");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string FormatBillLine(PrintJobItem item)
    {
        var qty = item.Quantity <= 0 ? 1 : item.Quantity;
        var unit = item.UnitPrice ?? item.Price;
        var total = unit * qty;
        return qty == 1
            ? $"{item.Name} {total:0.00}"
            : $"{qty}x {item.Name} {total:0.00}";
    }

    private static string Escape(string? value) =>
        SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
