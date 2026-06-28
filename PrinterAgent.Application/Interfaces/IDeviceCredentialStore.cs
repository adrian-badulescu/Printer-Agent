namespace PrinterAgent.Application.Interfaces;

/// <summary>
/// Persistă device credential-ul emis la enroll (DPAPI) pentru recovery via POST /api/agents/renew.
/// </summary>
public interface IDeviceCredentialStore
{
    string? AgentId { get; }

    string? DeviceCredential { get; }

    bool HasCredential { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string agentId, string deviceCredential, CancellationToken cancellationToken = default);
}
