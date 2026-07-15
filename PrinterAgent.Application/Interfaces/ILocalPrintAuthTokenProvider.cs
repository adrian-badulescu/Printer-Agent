namespace PrinterAgent.Application.Interfaces;

public interface ILocalPrintAuthTokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}
