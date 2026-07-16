using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Infrastructure.Redis;

namespace PrinterAgent.Worker;

/// <summary>
/// When ProgramData agent.json changes, reload configuration and refresh the Redis connection
/// so new printers / enrollment settings apply without restarting the Windows service.
/// </summary>
public sealed class AgentConfigurationReloadHostedService : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IAppConfiguration _appConfiguration;
    private readonly IRedisConnectionMultiplexerHolder _redisHolder;
    private readonly ILogger<AgentConfigurationReloadHostedService> _logger;
    private IDisposable? _reloadSubscription;

    public AgentConfigurationReloadHostedService(
        IConfiguration configuration,
        IAppConfiguration appConfiguration,
        IRedisConnectionMultiplexerHolder redisHolder,
        ILogger<AgentConfigurationReloadHostedService> logger)
    {
        _configuration = configuration;
        _appConfiguration = appConfiguration;
        _redisHolder = redisHolder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _reloadSubscription = ChangeToken.OnChange(
            () => _configuration.GetReloadToken(),
            OnConfigurationReloaded);
        return Task.CompletedTask;
    }

    private void OnConfigurationReloaded()
    {
        _redisHolder.Reset();

        var printerIds = _appConfiguration.Printers.Select(p => p.Id).ToArray();
        _logger.LogInformation(
            "agent.json reloaded — printers={Count} [{PrinterIds}]. Redis connection will refresh on next stream read.",
            printerIds.Length,
            string.Join(", ", printerIds));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _reloadSubscription?.Dispose();
        _reloadSubscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _reloadSubscription?.Dispose();
    }
}
