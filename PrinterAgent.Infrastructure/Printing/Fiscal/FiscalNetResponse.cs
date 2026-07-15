namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FiscalNetResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? DeviceErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? RawResponse { get; init; }

    public Domain.PrintJobResult ToPrintJobResult() =>
        Success
            ? Domain.PrintJobResult.Ok(FiscalReceiptNumber)
            : Domain.PrintJobResult.Failed(ErrorCode, DeviceErrorCode, ErrorMessage);
}
