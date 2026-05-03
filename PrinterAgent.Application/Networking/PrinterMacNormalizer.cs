using System.Globalization;

namespace PrinterAgent.Application.Networking;

public static class PrinterMacNormalizer
{
    /// <summary>Produces uppercase AA:BB:CC:DD:EE:FF for comparisons.</summary>
    public static string? Normalize(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return null;
        var hex = new string(mac.Where(static c => char.IsAsciiHexDigit(c)).ToArray());
        if (hex.Length != 12)
            return null;
        try
        {
            var parts = Enumerable.Range(0, 6)
                .Select(i => hex.Substring(i * 2, 2))
                .Select(s => byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();
            return string.Join(':',
                parts.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        }
        catch
        {
            return null;
        }
    }
}
