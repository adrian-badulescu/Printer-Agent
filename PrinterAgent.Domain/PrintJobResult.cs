namespace PrinterAgent.Domain;

public sealed class PrintJobResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? DeviceErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? FiscalReceiptAmount { get; init; }
    public string? FiscalReceiptDate { get; init; }
    public string? FiscalReceiptTime { get; init; }

    public static PrintJobResult Ok(string? fiscalReceiptNumber = null) =>
        new() { Success = true, FiscalReceiptNumber = fiscalReceiptNumber };

    public static PrintJobResult Failed(string? errorCode, string? deviceErrorCode = null, string? errorMessage = null) =>
        new()
        {
            Success = false,
            ErrorCode = errorCode,
            DeviceErrorCode = deviceErrorCode,
            ErrorMessage = errorMessage,
        };
}
