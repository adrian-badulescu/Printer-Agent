namespace PrinterAgent.Domain;

public static class PrinterTypes
{
    public const string EscPos = "escpos";
    public const string FiscalNet = "fiscalnet";
    public const string EpsonFiscal = "epson-fiscal";

    public const int DefaultFiscalNetPort = 65400;
    public const int DefaultEpsonFpMatePort = 443;
    public const int DefaultEpsonFpMateDevPort = 9102;

    public static bool IsFiscalNet(Printer printer)
    {
        if (string.Equals(printer.Type, FiscalNet, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsEpsonFiscal(printer))
            return false;

        // Mis-saved FiscalNet entries may have type escpos but port 65400.
        if (printer.Port != DefaultFiscalNetPort)
            return false;

        return string.IsNullOrWhiteSpace(printer.Type)
               || string.Equals(printer.Type, EscPos, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEpsonFiscal(Printer printer) =>
        string.Equals(printer.Type, EpsonFiscal, StringComparison.OrdinalIgnoreCase);

    public static bool IsFiscalPrinter(Printer printer) =>
        IsFiscalNet(printer) || IsEpsonFiscal(printer);

    /// <summary>FiscalNet driver/stub on :65400 uses HTTP; Epson fpmate uses fiscal.UseHttps.</summary>
    public static string ResolveFiscalHttpScheme(Printer printer)
    {
        var fiscal = printer.Fiscal ?? new FiscalPrinterSettings();
        return IsFiscalNet(printer)
            ? "http"
            : fiscal.UseHttps ? "https" : "http";
    }
}
