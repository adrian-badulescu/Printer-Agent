using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;

namespace PrinterAgent.Worker;

public class AgentWorker : BackgroundService
{
    private readonly IRedisStreamConsumer _redisConsumer;
    private readonly IHeartbeatService _heartbeatService;
    private readonly IUpdateService _updateService;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        IRedisStreamConsumer redisConsumer,
        IHeartbeatService heartbeatService,
        IUpdateService updateService,
        IAgentSessionStore sessionStore,
        IAppConfiguration appConfiguration,
        ILogger<AgentWorker> logger)
    {
        _redisConsumer = redisConsumer;
        _heartbeatService = heartbeatService;
        _updateService = updateService;
        _sessionStore = sessionStore;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _sessionStore.LoadAsync(stoppingToken).ConfigureAwait(false);

        var agentId = _sessionStore.AgentId;
        var restaurantId = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(restaurantId))
        {
            _logger.LogCritical("Agent oprit: lipsă AgentId sau RestaurantId după enroll.");
            return;
        }

        _logger.LogInformation("Agent Worker starting. AgentId: {AgentId}, RestaurantId: {RestaurantId}", agentId, restaurantId);

        _ = RunRedisConsumerSafelyAsync(restaurantId, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _heartbeatService.SendHeartbeatAsync(stoppingToken);
            await _updateService.CheckAndApplyUpdateAsync(agentId, stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>
    /// Consumatorul Redis rulează în paralel; prindem excepțiile neprevăzute aici (înainte erau pe Task neobservat).
    /// </summary>
    private async Task RunRedisConsumerSafelyAsync(string restaurantId, CancellationToken stoppingToken)
    {
        try
        {
            await _redisConsumer.StartConsumingAsync(restaurantId, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // oprire serviciu
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Redis stream consumer oprit cu eroare (RestaurantId={RestaurantId}).", restaurantId);
        }
    }
}
