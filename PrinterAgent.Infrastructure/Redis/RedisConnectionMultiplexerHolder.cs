using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using StackExchange.Redis;

namespace PrinterAgent.Infrastructure.Redis;

public sealed class RedisConnectionMultiplexerHolder : IRedisConnectionMultiplexerHolder, IDisposable
{
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<RedisConnectionMultiplexerHolder> _logger;
    private readonly object _gate = new();
    private IConnectionMultiplexer? _multiplexer;
    private string? _cachedConnectionString;

    public RedisConnectionMultiplexerHolder(
        IAppConfiguration appConfiguration,
        ILogger<RedisConnectionMultiplexerHolder> logger)
    {
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public IConnectionMultiplexer Get()
    {
        var connectionString = _appConfiguration.RedisConnectionString;
        lock (_gate)
        {
            if (_multiplexer is { IsConnected: true }
                && !string.IsNullOrWhiteSpace(_cachedConnectionString)
                && string.Equals(_cachedConnectionString, connectionString, StringComparison.Ordinal))
            {
                return _multiplexer;
            }

            _multiplexer?.Dispose();
            _multiplexer = null;
            _cachedConnectionString = connectionString;

            _logger.LogInformation(
                "Redis: opening connection ({Conn}).",
                _appConfiguration.RedisConnectionSummary);

            _multiplexer = ConnectionMultiplexer.Connect(connectionString);

            return _multiplexer;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_multiplexer == null)
                return;

            _logger.LogWarning("Redis: disposing connection multiplexer for reconnect.");
            _multiplexer.Dispose();
            _multiplexer = null;
            _cachedConnectionString = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _multiplexer?.Dispose();
            _multiplexer = null;
        }
    }
}
