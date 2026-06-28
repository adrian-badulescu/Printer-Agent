namespace PrinterAgent.Infrastructure.Http;

/// <summary>
/// Serialize refresh and device-renew calls — parallel refresh with rotation used to invalidate sessions.
/// </summary>
internal static class AgentAuthCoordination
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
