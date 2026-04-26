using Microsoft.Extensions.Configuration;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Worker.Config;

public class AppConfiguration : IAppConfiguration
{
    private readonly IConfiguration _configuration;
    /// <summary>
    /// agent.json next to the EXE. When %ProgramData%\...\agent.json has empty string placeholders,
    /// the merged <see cref="_configuration" /> still overrides install-dir; we fall back to the
    /// install file for the same key so EnrollmentCode, Redis, etc. are not wiped.
    /// </summary>
    private readonly IConfiguration? _bundledInInstallDir;

    public AppConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "agent.json");
        if (File.Exists(bundledPath))
        {
            _bundledInInstallDir = new ConfigurationBuilder()
                .AddJsonFile(bundledPath, optional: true, reloadOnChange: false)
                .Build();
        }
    }

    /// <summary>Uses merged host config; if null/whitespace, uses install-dir <c>agent.json</c> only.</summary>
    private string? MergedString(string key) =>
        !string.IsNullOrWhiteSpace(_configuration[key]) ? _configuration[key] : _bundledInInstallDir?[key];

    private bool MergedBool(string key, bool defaultValue)
    {
        var s = MergedString(key);
        if (!string.IsNullOrWhiteSpace(s) && bool.TryParse(s, out var parsed))
            return parsed;
        return _configuration.GetValue(key, _bundledInInstallDir?.GetValue(key, defaultValue) ?? defaultValue);
    }

    public string RestaurantId => MergedString("RestaurantId") ?? string.Empty;

    public string? EnrollmentCode
    {
        get
        {
            var v = MergedString("EnrollmentCode");
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }

    public string BackendUrl => MergedString("BackendUrl") ?? string.Empty;
    public string BackendJwtToken => MergedString("BackendJwtToken") ?? string.Empty;

    public string RedisConnectionString => BuildFinalRedisConnectionString();

    public string RedisStreamKeyPrefix =>
        (MergedString("Redis:StreamKeyPrefix") ?? "print.jobs").Trim();

    public string RedisConsumerGroup =>
        (MergedString("Redis:ConsumerGroup") ?? "printer-agents").Trim();

    public string RedisConnectionSummary =>
        RedisConnectionHelper.RedactForLogs(BuildFinalRedisConnectionString());

    private string BuildFinalRedisConnectionString()
    {
        var resolved = ResolveRedisConnectionString();
        if (string.IsNullOrWhiteSpace(resolved))
            return string.Empty;

        // StackExchange.Redis: abortConnect=false = do not give up at startup if Redis comes up later (e.g. VPN).
        var abortConnect = MergedBool("Redis:AbortConnect", true);
        return RedisConnectionHelper.EnsureAbortConnect(resolved, abortConnect);
    }

    public string Version => MergedString("Version") ?? "1.0.0";

    public string UpdateSignatureSecret => MergedString("UpdateSignatureSecret") ?? string.Empty;

    public int MaxPrintRetryAttempts =>
        int.TryParse(MergedString("MaxPrintRetryAttempts"), out var n) ? Math.Clamp(n, 1, 30) : 5;

    public int PrintRetryBaseDelayMs =>
        int.TryParse(MergedString("PrintRetryBaseDelayMs"), out var ms) ? Math.Clamp(ms, 100, 60_000) : 1000;

    public int PrinterConnectTimeoutSeconds =>
        int.TryParse(MergedString("PrinterConnectTimeoutSeconds"), out var s) ? Math.Clamp(s, 1, 120) : 15;

    public List<Printer> Printers
    {
        get
        {
            var printers = new List<Printer>();
            _configuration.GetSection("Printers").Bind(printers);
            if (printers.Count == 0 && _bundledInInstallDir != null)
                _bundledInInstallDir.GetSection("Printers").Bind(printers);
            return printers;
        }
    }

    /// <summary>
    /// (1) <c>RedisConnectionString</c> at root or <c>Redis:ConnectionString</c>.
    /// (2) Else built from <c>Redis:Host</c> etc. (StackExchange.Redis, ACL).
    /// </summary>
    private string ResolveRedisConnectionString()
    {
        var direct = (MergedString("RedisConnectionString") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(direct))
            direct = (MergedString("Redis:ConnectionString") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        var host = MergedString("Redis:Host");
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        var port = MergedString("Redis:Port");
        if (string.IsNullOrWhiteSpace(port))
            port = "6379";

        var user = MergedString("Redis:User");
        var password = MergedString("Redis:Password");
        var ssl = MergedBool("Redis:Ssl", false);
        var clientName = (MergedString("Redis:ClientName") ?? "URSPrinterAgent").Trim();

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
