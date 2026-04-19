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
    private readonly IMacResolver _macResolver;
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        IRedisStreamConsumer redisConsumer,
        IHeartbeatService heartbeatService,
        IUpdateService updateService,
        IMacResolver macResolver,
        IAppConfiguration appConfiguration,
        ILogger<AgentWorker> logger)
    {
        _redisConsumer = redisConsumer;
        _heartbeatService = heartbeatService;
        _updateService = updateService;
        _macResolver = macResolver;
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent Worker starting. AgentId: {AgentId}", _macResolver.GetMacAddress());

        // Start Redis consumer in background
        _ = _redisConsumer.StartConsumingAsync(_appConfiguration.RestaurantId, stoppingToken);

        // Heartbeat and Update checking loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await _heartbeatService.SendHeartbeatAsync(stoppingToken);
            await _updateService.CheckAndApplyUpdateAsync(_macResolver.GetMacAddress(), stoppingToken);

            // Wait 30 seconds before next heartbeat
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
