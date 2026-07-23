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
    public string? FiscalNumber { get; init; }
    public string? ZReportNumber { get; init; }
    public string? FiscalDate { get; init; }

    public static PrintJobResult Ok(
        string? fiscalReceiptNumber = null,
        string? fiscalNumber = null,
        string? zReportNumber = null,
        string? fiscalDate = null) =>
        new()
        {
            Success = true,
            FiscalReceiptNumber = fiscalReceiptNumber,
            FiscalNumber = fiscalNumber ?? fiscalReceiptNumber,
            ZReportNumber = zReportNumber,
            FiscalDate = fiscalDate,
        };

    public static PrintJobResult Failed(string? errorCode, string? deviceErrorCode = null, string? errorMessage = null) =>
        new()
        {
            Success = false,
            ErrorCode = errorCode,
            DeviceErrorCode = deviceErrorCode,
            ErrorMessage = errorMessage,
        };
}
