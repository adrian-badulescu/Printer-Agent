namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FiscalNetResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? DeviceErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? ZReportNumber { get; init; }
    public string? FiscalDate { get; init; }
    public string? RawResponse { get; init; }

    public Domain.PrintJobResult ToPrintJobResult() =>
        Success
            ? Domain.PrintJobResult.Ok(
                FiscalReceiptNumber,
                fiscalNumber: FiscalReceiptNumber,
                zReportNumber: ZReportNumber,
                fiscalDate: FiscalDate)
            : Domain.PrintJobResult.Failed(ErrorCode, DeviceErrorCode, ErrorMessage);
}
