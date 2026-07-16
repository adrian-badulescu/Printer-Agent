namespace PrinterAgent.Domain;

public static class PrinterTypes
{
    public const string EscPos = "escpos";
    public const string FiscalNet = "fiscalnet";
    public const int DefaultFiscalNetPort = 65400;

    public static bool IsFiscalNet(Printer printer)
    {
        if (string.Equals(printer.Type, FiscalNet, StringComparison.OrdinalIgnoreCase))
            return true;

        // Mis-saved FiscalNet entries may have type escpos but port 65400.
        if (printer.Port != DefaultFiscalNetPort)
            return false;

        return string.IsNullOrWhiteSpace(printer.Type)
               || string.Equals(printer.Type, EscPos, StringComparison.OrdinalIgnoreCase);
    }
}
