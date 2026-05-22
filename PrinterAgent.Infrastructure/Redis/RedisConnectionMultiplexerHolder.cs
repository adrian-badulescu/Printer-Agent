using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Infrastructure.Diagnostics;
using StackExchange.Redis;

namespace PrinterAgent.Infrastructure.Redis;

public sealed class RedisConnectionMultiplexerHolder : IRedisConnectionMultiplexerHolder, IDisposable
{
    private readonly IAppConfiguration _appConfiguration;
    private readonly ILogger<RedisConnectionMultiplexerHolder> _logger;
    private readonly object _gate = new();
    private IConnectionMultiplexer? _multiplexer;

    public RedisConnectionMultiplexerHolder(
        IAppConfiguration appConfiguration,
        ILogger<RedisConnectionMultiplexerHolder> logger)
    {
        _appConfiguration = appConfiguration;
        _logger = logger;
    }

    public IConnectionMultiplexer Get()
    {
        lock (_gate)
        {
            if (_multiplexer is { IsConnected: true })
                return _multiplexer;

            _multiplexer?.Dispose();
            _multiplexer = null;

            _logger.LogInformation(
                "Redis: opening connection ({Conn}).",
                _appConfiguration.RedisConnectionSummary);

            var sw = Stopwatch.StartNew();
            try
            {
                _multiplexer = ConnectionMultiplexer.Connect(_appConfiguration.RedisConnectionString);
                sw.Stop();
                // #region agent log
                DebugSessionLog.Write("H1", "RedisConnectionMultiplexerHolder.Get", "redis_connect_ok", new
                {
                    elapsedMs = sw.ElapsedMilliseconds,
                    isConnected = _multiplexer.IsConnected,
                    summary = _appConfiguration.RedisConnectionSummary
                });
                // #endregion
                return _multiplexer;
            }
            catch (Exception ex)
            {
                sw.Stop();
                // #region agent log
                DebugSessionLog.Write("H1", "RedisConnectionMultiplexerHolder.Get", "redis_connect_failed", new
                {
                    elapsedMs = sw.ElapsedMilliseconds,
                    exType = ex.GetType().Name,
                    isNoAuth = ex.Message.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("AuthenticationFailure", StringComparison.OrdinalIgnoreCase),
                    message = ex.Message,
                    summary = _appConfiguration.RedisConnectionSummary
                });
                // #endregion
                throw;
            }
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
