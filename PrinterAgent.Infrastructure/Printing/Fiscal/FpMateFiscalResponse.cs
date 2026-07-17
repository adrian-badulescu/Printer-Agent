using System.Xml.Linq;
using PrinterAgent.Domain;

namespace PrinterAgent.Infrastructure.Printing.Fiscal;

public sealed class FpMateFiscalResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? FiscalReceiptNumber { get; init; }
    public string? RawResponse { get; init; }

    public PrintJobResult ToPrintJobResult() =>
        Success
            ? PrintJobResult.Ok(FiscalReceiptNumber)
            : PrintJobResult.Failed(ErrorCode, null, Message);

    public static FpMateFiscalResponse Failed(string errorCode, string? message = null, string? raw = null) =>
        new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            RawResponse = raw,
        };

    internal static FpMateFiscalResponse Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Failed("EMPTY_RESPONSE");

        try
        {
            var doc = XDocument.Parse(body);
            var response = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "response");
            if (response == null)
                return Failed("INVALID_RESPONSE", "Missing response element.", body);

            var successAttr = response.Attribute("success")?.Value;
            var success = string.Equals(successAttr, "true", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(successAttr, "1", StringComparison.OrdinalIgnoreCase);
            var code = response.Attribute("code")?.Value;
            var status = response.Attribute("status")?.Value;

            string? receiptNumber = null;
            var addInfo = response.Elements().FirstOrDefault(x => x.Name.LocalName == "addInfo");
            if (addInfo != null)
            {
                var receiptEl = addInfo.Elements().FirstOrDefault(x => x.Name.LocalName == "fiscalReceiptNumber");
                receiptNumber = receiptEl?.Value;
            }

            if (success)
            {
                return new FpMateFiscalResponse
                {
                    Success = true,
                    FiscalReceiptNumber = receiptNumber,
                    RawResponse = body,
                };
            }

            var errorCode = string.IsNullOrWhiteSpace(code) ? status ?? "FPMATE_ERROR" : code;
            return Failed(errorCode, status, body);
        }
        catch (Exception ex)
        {
            return Failed("INVALID_RESPONSE", ex.Message, body);
        }
    }
}
