namespace PrinterAgent.Application.Interfaces;

/// <summary>
/// Recuperează sesiunea prin <c>POST api/agents/renew</c> când refresh-ul nu mai funcționează.
/// </summary>
public interface IAgentDeviceRenewalService
{
    Task<bool> TryRenewWithDeviceCredentialAsync(CancellationToken cancellationToken = default);
}
