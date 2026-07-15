using System.Text.RegularExpressions;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed record FiscalDeviceError(string ErrorCode, string? DeviceErrorCode, string? RawSnippet);

public static partial class FiscalDeviceErrorParser
{
    private static readonly (string Pattern, string Code)[] TextPatterns =
    [
        ("Object reference not set to an instance of an object", "FISCALNET_NULL_REFERENCE"),
        ("Index was outside the bounds of the array", "FISCALNET_INDEX_OUT_OF_BOUNDS"),
        ("imput string is not a valid integer", "FISCALNET_INVALID_INTEGER"),
        ("Unable to connect to the remote server", "FISCALNET_REMOTE_SERVER"),
        ("Opening COM: File not found", "FISCALNET_COM_NOT_FOUND"),
        ("Unable to get version of server definitions", "DAISY_103"),
        ("ServSockConnectionFailed", "TREMOL_30"),
        ("Socket connect FAILED", "TREMOL_30"),
        ("server conection error", "TREMOL_101"),
        ("server connection error", "TREMOL_101"),
        ("ef_NoFisPrnMode", "ORGTECH_xC3"),
        ("Mod imprimanta fiscala neactivat", "ORGTECH_xC3"),
    ];

    private static readonly string[] DatecsCodes =
    [
        "33022", "10500", "112202", "10524", "109983", "10505", "111015", "33029",
        "111021", "111003", "111005", "111063",
    ];

    private static readonly string[] DaisyCodes = ["82", "21", "24", "255", "103"];

    public static FiscalDeviceError? TryParse(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return null;

        var text = rawResponse.Trim();

        if (TryMatchOrgtech(text, out var orgtech))
            return orgtech;

        if (TryMatchTremol(text, out var tremol))
            return tremol;

        if (TryMatchVendorNumeric(text, "DATECS", DatecsCodes, out var datecs))
            return datecs;

        if (TryMatchVendorNumeric(text, "DAISY", DaisyCodes, out var daisy))
            return daisy;

        foreach (var (pattern, code) in TextPatterns)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return new FiscalDeviceError(code, null, Truncate(text));
        }

        return null;
    }

    private static bool TryMatchOrgtech(string text, out FiscalDeviceError? result)
    {
        result = null;
        if (text.Contains("xC3", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ef_NoFisPrnMode", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Mod imprimanta fiscala neactivat", StringComparison.OrdinalIgnoreCase))
        {
            result = new FiscalDeviceError("ORGTECH_xC3", "xC3", Truncate(text));
            return true;
        }

        return false;
    }

    private static bool TryMatchTremol(string text, out FiscalDeviceError? result)
    {
        result = null;
        var err30 = TremolErrCodeRegex().Match(text);
        if (err30.Success && err30.Groups[1].Value == "30")
        {
            result = new FiscalDeviceError("TREMOL_30", "30", Truncate(text));
            return true;
        }

        if (text.Contains("ServSockConnectionFailed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Socket connect FAILED", StringComparison.OrdinalIgnoreCase))
        {
            result = new FiscalDeviceError("TREMOL_30", "30", Truncate(text));
            return true;
        }

        var err101 = TremolError101Regex().Match(text);
        if (err101.Success)
        {
            result = new FiscalDeviceError("TREMOL_101", "101", Truncate(text));
            return true;
        }

        return false;
    }

    private static bool TryMatchVendorNumeric(
        string text,
        string vendor,
        string[] codes,
        out FiscalDeviceError? result)
    {
        result = null;
        foreach (var code in codes)
        {
            if (!text.Contains(code, StringComparison.Ordinal))
                continue;

            result = new FiscalDeviceError($"{vendor}_{code}", code, Truncate(text));
            return true;
        }

        var errCode = DaisyErrCodeRegex().Match(text);
        if (errCode.Success && vendor == "DAISY")
        {
            var code = errCode.Groups[1].Value;
            result = new FiscalDeviceError($"DAISY_{code}", code, Truncate(text));
            return true;
        }

        return false;
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];

    [GeneratedRegex(@"ErrCode:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TremolErrCodeRegex();

    [GeneratedRegex(@"Tremol\s+error\s+101", RegexOptions.IgnoreCase)]
    private static partial Regex TremolError101Regex();

    [GeneratedRegex(@"ErrCode:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DaisyErrCodeRegex();
}
