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

            var connectionString = _appConfiguration.RedisConnectionString;

            // #region agent log
            try
            {
                var opts = ConfigurationOptions.Parse(connectionString);
                var pwd = opts.Password ?? string.Empty;
                DebugSessionLog.Write("H6", "RedisConnectionMultiplexerHolder.Get", "parsed_options", new
                {
                    parsedPasswordLength = pwd.Length,
                    parsedPasswordStartsWithQuote = pwd.Length > 0 && pwd[0] == '"',
                    parsedPasswordEndsWithQuote = pwd.Length > 0 && pwd[pwd.Length - 1] == '"',
                    parsedPasswordContainsHash = pwd.Contains('#'),
                    parsedPasswordHashCount = pwd.Count(c => c == '#'),
                    parsedUserPresent = !string.IsNullOrEmpty(opts.User),
                    parsedUser = opts.User ?? string.Empty,
                    parsedEndpoint = opts.EndPoints.Count > 0 ? opts.EndPoints[0].ToString() ?? string.Empty : string.Empty
                });
            }
            catch (Exception parseEx)
            {
                DebugSessionLog.Write("H6", "RedisConnectionMultiplexerHolder.Get", "parse_options_failed", new
                {
                    message = parseEx.Message
                });
            }
            // #endregion

            var sw = Stopwatch.StartNew();
            try
            {
                _multiplexer = ConnectionMultiplexer.Connect(connectionString);
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
                    innerType = ex.InnerException?.GetType().FullName ?? string.Empty,
                    innerMessage = ex.InnerException?.Message ?? string.Empty,
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
