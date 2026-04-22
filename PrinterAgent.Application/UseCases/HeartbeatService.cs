using PrinterAgent.Domain;
using PrinterAgent.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace PrinterAgent.Application.UseCases;

public interface IHeartbeatService
{
    Task SendHeartbeatAsync(CancellationToken cancellationToken = default);
}

public class HeartbeatService : IHeartbeatService
{
    private readonly IBackendClient _backendClient;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAgentSessionRenewalService _sessionRenewal;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IBackendClient backendClient,
        IAgentSessionStore sessionStore,
        IAgentSessionRenewalService sessionRenewal,
        IAppConfiguration appConfiguration,
        ILogger<HeartbeatService> logger)
    {
        _backendClient = backendClient;
        _sessionStore = sessionStore;
        _sessionRenewal = sessionRenewal;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            var agentId = _sessionStore.AgentId;
            var restaurantId = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(restaurantId))
            {
                _logger.LogWarning("Heartbeat omis: lipsă AgentId sau RestaurantId în sesiune.");
                return;
            }

            var agentInfo = new AgentInfo
            {
                AgentId = agentId,
                RestaurantId = restaurantId,
                Version = _appConfiguration.Version,
                Printers = _appConfiguration.Printers
            };

            var ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);
            if (!ok && !string.IsNullOrWhiteSpace(_sessionStore.RefreshToken))
            {
                _logger.LogWarning(
                    "Heartbeat neautorizat pentru agentId={AgentId}; încerc refresh forțat și un al doilea heartbeat.",
                    agentId);
                _ = await _sessionRenewal.TryRenewIfAccessExpiredAsync(TimeSpan.FromMinutes(5), cancellationToken, force: true)
                    .ConfigureAwait(false);
                ok = await _backendClient.SendHeartbeatAsync(agentInfo, cancellationToken).ConfigureAwait(false);
            }

            if (!ok)
            {
                _logger.LogWarning(
                    "URS_Metric HeartbeatUnauthorized agentId={AgentId}. Sesiune ștearsă; dacă refresh a eșuat definitiv, setează EnrollmentCode în agent.json și repornește serviciul.",
                    agentId);
                await _sessionStore.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("Heartbeat sent successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat.");
        }
    }
}
