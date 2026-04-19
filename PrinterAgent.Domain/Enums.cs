namespace PrinterAgent.Domain;

public enum PrintJobStatus
{
    Received,
    Printing,
    Success,
    Failed
}

public enum PrinterStatus
{
    Online,
    Offline,
    Error
}
