using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Worker.Config;
using StackExchange.Redis;

namespace PrinterAgent.Worker;

/// <summary>
/// După WireGuard (dacă e activ): verifică Redis (PING) și reachability HTTP la backend (fără JWT).
/// PING folosește o conexiune temporară; consumul stream folosește <see cref="Lazy{T}"/> pentru <see cref="StackExchange.Redis.IConnectionMultiplexer"/> (conectare la prima citire).
/// </summary>
public sealed class StartupConnectivityHostedService : IHostedService
{
    private readonly IAppConfiguration _appConfiguration;
    private readonly IOptions<ConnectivityOptions> _options;
    private readonly ILogger<StartupConnectivityHostedService> _logger;

    public StartupConnectivityHostedService(
        IAppConfiguration appConfiguration,
        IOptions<ConnectivityOptions> options,
        ILogger<StartupConnectivityHostedService> logger)
    {
        _appConfiguration = appConfiguration;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opt = _options.Value;
        if (!opt.VerifyAtStartup)
        {
            _logger.LogInformation("Connectivity: startup checks disabled (Connectivity:VerifyAtStartup=false).");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_appConfiguration.RedisConnectionString))
        {
            try
            {
                await using var mux = await ConnectionMultiplexer.ConnectAsync(_appConfiguration.RedisConnectionString).ConfigureAwait(false);
                var latency = await mux.GetDatabase().PingAsync().ConfigureAwait(false);
                _logger.LogInformation("Connectivity: Redis PING OK ({Ms:F0} ms).", latency.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Connectivity: Redis did not respond. Check network / VPN / connection string.");
            }
        }
        else
        {
            _logger.LogWarning("Connectivity: Redis is not configured — skipping Redis PING.");
        }

        var baseUrl = _appConfiguration.BackendUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning("Connectivity: BackendUrl is missing — skipping HTTP check.");
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
        {
            _logger.LogWarning("Connectivity: BackendUrl is not a valid absolute URL: {Url}", baseUrl);
            return;
        }

        var path = opt.BackendHealthPath.TrimStart('/');
        var healthUrl = new Uri(root, path);

        try
        {
            using var handler = new HttpClientHandler();
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.BackendHealthTimeoutSeconds, 1, 120))
            };

            using var response = await client.GetAsync(healthUrl, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Connectivity: backend HTTP OK ({Status}) {Url}.", (int)response.StatusCode, healthUrl);
            }
            else
            {
                _logger.LogWarning(
                    "Connectivity: backend returned {Status} for {Url}.",
                    (int)response.StatusCode,
                    healthUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Connectivity: cannot reach backend {Url}. With WireGuard.Enabled=false, try the same URL in a browser or curl.",
                healthUrl);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
