namespace PrinterAgent.Infrastructure.Printing;

public static class ReceiptRestaurantHeaderHelper
{
    public static string FormatRegistrationLine(string? registrationNumber)
    {
        if (string.IsNullOrWhiteSpace(registrationNumber))
            return string.Empty;

        return $"Reg. No: {registrationNumber.Trim()}";
    }

    public static string SafeAscii(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value.Select(c => c <= 127 ? c : '?').ToArray());
    }
}
