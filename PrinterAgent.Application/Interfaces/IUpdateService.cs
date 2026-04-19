namespace PrinterAgent.Application.Interfaces;

public interface IUpdateService
{
    Task CheckAndApplyUpdateAsync(string agentId, CancellationToken cancellationToken = default);
}
