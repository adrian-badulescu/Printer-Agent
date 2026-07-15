namespace PrinterAgent.Domain;

public static class PrinterTypes
{
    public const string EscPos = "escpos";
    public const string FiscalNet = "fiscalnet";

    public static bool IsFiscalNet(Printer printer) =>
        string.Equals(printer.Type, FiscalNet, StringComparison.OrdinalIgnoreCase);
}
