using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using System.Linq;

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
        // Do not exit on startup when enrollment hasn't happened yet; wait for session to appear.
        while (!stoppingToken.IsCancellationRequested)
        {
            await _sessionStore.LoadAsync(stoppingToken).ConfigureAwait(false);

            var agentId0 = _sessionStore.AgentId;
            var restaurantId0 = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
            if (!string.IsNullOrWhiteSpace(agentId0) && !string.IsNullOrWhiteSpace(restaurantId0))
                break;

            _logger.LogWarning("Agent worker waiting for enrollment/session (AgentId/RestaurantId missing).");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        }

        var agentId = _sessionStore.AgentId;
        var restaurantId = _sessionStore.SessionRestaurantId ?? _appConfiguration.RestaurantId;
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(restaurantId))
            return;

        _logger.LogInformation("Agent Worker starting. AgentId: {AgentId}, RestaurantId: {RestaurantId}", agentId, restaurantId);

        var printerCount = _appConfiguration.Printers.Count;
        if (printerCount == 0)
        {
            _logger.LogWarning(
                "No printers in merged agent.json — jobs will fail until Printers[] is configured. Edit %ProgramData%\\URSPrinterAgent\\agent.json or run Configurator; restart the service after saving.");
        }
        else
        {
            _logger.LogInformation(
                "Printers loaded: {Count} — Ids: [{PrinterIds}]",
                printerCount,
                string.Join(", ", _appConfiguration.Printers.Select(p => p.Id)));
        }

        _ = RunRedisConsumerSafelyAsync(restaurantId, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _heartbeatService.SendHeartbeatAsync(stoppingToken);
            await _updateService.CheckAndApplyUpdateAsync(agentId, stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>
    /// Consumatorul Redis rulează în paralel; la erori fatale (ex. tunel WG indisponibil la pornire) reîncearcă.
    /// </summary>
    private async Task RunRedisConsumerSafelyAsync(string restaurantId, CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _redisConsumer.StartConsumingAsync(restaurantId, stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Redis stream consumer stopped (RestaurantId={RestaurantId}); retrying in {DelaySeconds}s.",
                    restaurantId,
                    retryDelay.TotalSeconds);

                try
                {
                    await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }
    }
}
