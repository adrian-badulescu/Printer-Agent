using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.UseCases;
using PrinterAgent.Infrastructure.Redis;
using StackExchange.Redis;

namespace PrinterAgent.Worker;

public class AgentWorker : BackgroundService
{
    private readonly IRedisStreamConsumer _redisConsumer;
    private readonly IRedisConnectionMultiplexerHolder _redisHolder;
    private readonly IHeartbeatService _heartbeatService;
    private readonly IUpdateService _updateService;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IRedisRuntimeCredentials _redisRuntimeCredentials;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        IRedisStreamConsumer redisConsumer,
        IRedisConnectionMultiplexerHolder redisHolder,
        IHeartbeatService heartbeatService,
        IUpdateService updateService,
        IAgentSessionStore sessionStore,
        IRedisRuntimeCredentials redisRuntimeCredentials,
        IAppConfiguration appConfiguration,
        ILogger<AgentWorker> logger)
    {
        _redisConsumer = redisConsumer;
        _redisHolder = redisHolder;
        _heartbeatService = heartbeatService;
        _updateService = updateService;
        _sessionStore = sessionStore;
        _redisRuntimeCredentials = redisRuntimeCredentials;
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
            if (string.IsNullOrWhiteSpace(agentId0) || string.IsNullOrWhiteSpace(restaurantId0))
            {
                _logger.LogWarning("Agent worker waiting for enrollment/session (AgentId/RestaurantId missing).");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (_appConfiguration.HasLegacyRedisPassword)
                break;

            await _redisRuntimeCredentials.LoadAsync(stoppingToken).ConfigureAwait(false);
            if (_redisRuntimeCredentials.HasCredentials)
                break;

            _logger.LogWarning(
                "Agent worker waiting for per-restaurant Redis credentials (enrollment service is provisioning).");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
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
            await _sessionStore.LoadAsync(stoppingToken).ConfigureAwait(false);
            var currentAgentId = _sessionStore.AgentId;
            if (string.IsNullOrWhiteSpace(currentAgentId))
            {
                _logger.LogWarning("Agent worker: session lost; waiting for re-enrollment.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                continue;
            }

            await _heartbeatService.SendHeartbeatAsync(stoppingToken);

            // #region agent log
            DebugSessionLog.Write(
                "H3",
                "AgentWorker.ExecuteAsync",
                "update check agentId",
                new { currentAgentId, sessionAgentId = _sessionStore.AgentId });
            // #endregion

            await _updateService.CheckAndApplyUpdateAsync(currentAgentId, stoppingToken);

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
            catch (RedisConnectionException ex)
            {
                _redisHolder.Reset();
                _logger.LogError(
                    ex,
                    "Redis stream consumer stopped (RestaurantId={RestaurantId}); connection reset, retrying in {DelaySeconds}s.",
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
