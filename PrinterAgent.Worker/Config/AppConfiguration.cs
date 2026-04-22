using Microsoft.Extensions.Configuration;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Worker.Config;

public class AppConfiguration : IAppConfiguration
{
    private readonly IConfiguration _configuration;

    public AppConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string RestaurantId => _configuration["RestaurantId"] ?? string.Empty;

    public string? EnrollmentCode
    {
        get
        {
            var v = _configuration["EnrollmentCode"];
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }

    public string BackendUrl => _configuration["BackendUrl"] ?? string.Empty;
    public string BackendJwtToken => _configuration["BackendJwtToken"] ?? string.Empty;

    public string RedisConnectionString => BuildFinalRedisConnectionString();

    public string RedisStreamKeyPrefix =>
        (_configuration["Redis:StreamKeyPrefix"] ?? "print.jobs").Trim();

    public string RedisConsumerGroup =>
        (_configuration["Redis:ConsumerGroup"] ?? "printer-agents").Trim();

    public string RedisConnectionSummary =>
        RedisConnectionHelper.RedactForLogs(BuildFinalRedisConnectionString());

    private string BuildFinalRedisConnectionString()
    {
        var resolved = ResolveRedisConnectionString();
        if (string.IsNullOrWhiteSpace(resolved))
            return string.Empty;

        // StackExchange.Redis: abortConnect=false = nu renunța la start dacă Redis apare mai târziu (ex. VPN).
        var abortConnect = _configuration.GetValue("Redis:AbortConnect", true);
        return RedisConnectionHelper.EnsureAbortConnect(resolved, abortConnect);
    }

    public string Version => _configuration["Version"] ?? "1.0.0";

    public string UpdateSignatureSecret => _configuration["UpdateSignatureSecret"] ?? string.Empty;

    public int MaxPrintRetryAttempts => int.TryParse(_configuration["MaxPrintRetryAttempts"], out var n) ? Math.Clamp(n, 1, 30) : 5;

    public int PrintRetryBaseDelayMs => int.TryParse(_configuration["PrintRetryBaseDelayMs"], out var ms) ? Math.Clamp(ms, 100, 60_000) : 1000;

    public int PrinterConnectTimeoutSeconds =>
        int.TryParse(_configuration["PrinterConnectTimeoutSeconds"], out var s) ? Math.Clamp(s, 1, 120) : 15;

    public List<Printer> Printers
    {
        get
        {
            var printers = new List<Printer>();
            _configuration.GetSection("Printers").Bind(printers);
            return printers;
        }
    }

    /// <summary>
    /// (1) <c>RedisConnectionString</c> la rădăcină sau <c>Redis:ConnectionString</c>.
    /// (6) Altfel se compune din <c>Redis:Host</c>, <c>Redis:Port</c>, <c>Redis:User</c>, <c>Redis:Password</c>, <c>Redis:Ssl</c>
    /// — format compatibil ACL Redis 6+ și StackExchange.Redis.
    /// </summary>
    private string ResolveRedisConnectionString()
    {
        var direct = _configuration["RedisConnectionString"]
                     ?? _configuration["Redis:ConnectionString"];

        if (!string.IsNullOrWhiteSpace(direct))
            return direct.Trim();

        var host = _configuration["Redis:Host"];
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        var port = _configuration["Redis:Port"];
        if (string.IsNullOrWhiteSpace(port))
            port = "6379";

        var user = _configuration["Redis:User"];
        var password = _configuration["Redis:Password"];
        var ssl = _configuration.GetValue("Redis:Ssl", false);
        var clientName = _configuration["Redis:ClientName"] ?? "URSPrinterAgent";

        var endpoint = $"{host.Trim()}:{port.Trim()}";
        var parts = new List<string> { endpoint };

        if (!string.IsNullOrEmpty(password))
        {
            if (!string.IsNullOrWhiteSpace(user))
                parts.Add($"user={user.Trim()}");

            parts.Add($"password={password}");
        }

        parts.Add(ssl ? "ssl=true" : "ssl=false");
        parts.Add($"name={clientName.Trim()}");

        return string.Join(",", parts);
    }
}
