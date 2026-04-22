namespace PrinterAgent.Application.Interfaces;

/// <summary>
/// Sesiune printer-agent: token JWT, restaurantId și agentId după enroll; plus GUID stabil de instanță.
/// </summary>
public interface IAgentSessionStore
{
    string? AgentId { get; }
    string? AccessToken { get; }
    /// <summary>Token opac pentru refresh; păstrat criptat în agent.session.json pe Windows (DPAPI).</summary>
    string? RefreshToken { get; }
    string? SessionRestaurantId { get; }
    DateTimeOffset? ExpiresAtUtc { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>True dacă tokenul există și nu e expirat (cu marjă).</summary>
    bool HasUsableSession(TimeSpan expirySkew);

    Guid GetOrCreateClientInstanceId(CancellationToken cancellationToken = default);

    Task SaveSessionAsync(
        string agentId,
        string accessToken,
        string refreshToken,
        string restaurantId,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Șterge sesiunea de pe disc (ex. după 401 heartbeat). client.instance rămâne.</summary>
    Task ClearSessionAsync(CancellationToken cancellationToken = default);
}
