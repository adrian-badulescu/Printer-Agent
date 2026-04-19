namespace PrinterAgent.Domain;

public class Printer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public PrinterStatus Status { get; set; } = PrinterStatus.Offline;
}
