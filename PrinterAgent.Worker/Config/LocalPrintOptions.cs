namespace PrinterAgent.Worker.Config;

public sealed class LocalPrintOptions
{
    public const string SectionName = "LocalPrint";

    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 9247;
}
