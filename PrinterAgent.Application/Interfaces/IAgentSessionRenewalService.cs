namespace PrinterAgent.Application.Interfaces;

/// <summary>
/// Reîmprospătează perechea access/refresh prin <c>POST api/agents/refresh</c> când JWT-ul de acces e expirat (în marja dată).
/// </summary>
public interface IAgentSessionRenewalService
{
    /// <param name="force">
    /// Dacă e true, apelează refresh chiar dacă access-ul încă pare valid (ex. după 401 la heartbeat, token respins de server).
    /// </param>
    Task<bool> TryRenewIfAccessExpiredAsync(
        TimeSpan expirySkew,
        CancellationToken cancellationToken = default,
        bool force = false);
}
