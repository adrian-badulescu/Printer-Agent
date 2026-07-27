using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Domain;

namespace PrinterAgent.Worker.Config;

public class AppConfiguration : IAppConfiguration
{
    private readonly IConfiguration _configuration;
    private readonly IRedisRuntimeCredentials _redisRuntimeCredentials;
    /// <summary>
    /// agent.json next to the EXE. When %ProgramData%\...\agent.json has empty string placeholders,
    /// the merged <see cref="_configuration" /> still overrides install-dir; we fall back to the
    /// install file for the same key so EnrollmentCode, Redis, etc. are not wiped.
    /// </summary>
    private readonly IConfiguration? _bundledInInstallDir;

    public AppConfiguration(IConfiguration configuration, IRedisRuntimeCredentials redisRuntimeCredentials)
    {
        _configuration = configuration;
        _redisRuntimeCredentials = redisRuntimeCredentials;
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "agent.json");
        if (File.Exists(bundledPath))
        {
            _bundledInInstallDir = new ConfigurationBuilder()
                .AddJsonFile(bundledPath, optional: true, reloadOnChange: false)
                .Build();
        }
    }

    /// <summary>
    /// Factory-shipped keys come from install-dir <c>agent.json</c> (next to the EXE) so delivered builds
    /// work without manual ProgramData edits. Operator overrides (enrollment, printers) use ProgramData first.
    /// </summary>
    private static readonly HashSet<string> BundledFirstKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "BackendUrl",
        "UpdateSignatureSecret",
        "UpdateManifestUrl",
        "Version",
        "Redis:Host",
        "Redis:Port",
        "Redis:Password",
        "Redis:User",
        "Redis:Ssl",
        "Redis:AbortConnect",
        "Redis:ClientName",
        "Redis:StreamKeyPrefix",
        "Redis:ConsumerGroup",
        "RedisConnectionString",
        "Redis:ConnectionString",
        "Connectivity:VerifyAtStartup",
        "Connectivity:BackendHealthPath",
        "Connectivity:BackendHealthTimeoutSeconds",
        "WireGuard:Enabled",
        "WireGuard:ConfigFilePath",
        "WireGuard:WindowsTunnelServiceName",
        "WireGuard:WaitForTunnelServiceSeconds",
        "WireGuard:StartServiceIfStopped"
    };

    /// <summary>Uses merged host config; if null/whitespace, uses install-dir <c>agent.json</c> only.</summary>
    private string? MergedString(string key)
    {
        if (string.Equals(key, "Redis:Password", StringComparison.OrdinalIgnoreCase)
            && ProgramDataAgentJsonReader.ProgramDataOptedIntoPerRestaurantRedisCredentials())
        {
            return string.Empty;
        }

        if (BundledFirstKeys.Contains(key))
        {
            var bundled = _bundledInInstallDir?[key];
            if (!string.IsNullOrWhiteSpace(bundled))
                return bundled;
            return _configuration[key];
        }

        return !string.IsNullOrWhiteSpace(_configuration[key]) ? _configuration[key] : _bundledInInstallDir?[key];
    }

    /// <summary>Install-dir <c>agent.json</c> only (MSI factory defaults). Ignores ProgramData overrides.</summary>
    private string? BundledInstallDirString(string key) =>
        _bundledInInstallDir?[key];

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
            if (string.IsNullOrWhiteSpace(v))
                v = ProgramDataAgentJsonReader.GetString("EnrollmentCode");
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }

    public string BackendUrl => MergedString("BackendUrl") ?? string.Empty;
    public string BackendJwtToken => MergedString("BackendJwtToken") ?? string.Empty;

    public string RedisConnectionString => BuildFinalRedisConnectionString();

    public string RedisStreamKeyPrefix =>
        _redisRuntimeCredentials.HasCredentials && !string.IsNullOrWhiteSpace(_redisRuntimeCredentials.StreamKeyPrefix)
            ? _redisRuntimeCredentials.StreamKeyPrefix!.Trim()
            : (MergedString("Redis:StreamKeyPrefix") ?? "print.jobs").Trim();

    public string RedisConsumerGroup =>
        _redisRuntimeCredentials.HasCredentials && !string.IsNullOrWhiteSpace(_redisRuntimeCredentials.ConsumerGroup)
            ? _redisRuntimeCredentials.ConsumerGroup!.Trim()
            : (MergedString("Redis:ConsumerGroup") ?? "printer-agents").Trim();

    public string RedisConnectionSummary =>
        RedisConnectionHelper.RedactForLogs(BuildFinalRedisConnectionString());

    public bool HasLegacyRedisPassword =>
        !string.IsNullOrWhiteSpace(BundledInstallDirString("Redis:Password"))
        && !ProgramDataAgentJsonReader.ProgramDataOptedIntoPerRestaurantRedisCredentials();

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

    public string UpdateManifestUrl => MergedString("UpdateManifestUrl") ?? string.Empty;

    public int MaxPrintRetryAttempts =>
        int.TryParse(MergedString("MaxPrintRetryAttempts"), out var n) ? Math.Clamp(n, 1, 30) : 5;

    public int PrintRetryBaseDelayMs =>
        int.TryParse(MergedString("PrintRetryBaseDelayMs"), out var ms) ? Math.Clamp(ms, 100, 60_000) : 1000;

    public int PrinterConnectTimeoutSeconds =>
        int.TryParse(MergedString("PrinterConnectTimeoutSeconds"), out var s) ? Math.Clamp(s, 1, 120) : 15;

    public bool LocalPrintEnabled => MergedBool("LocalPrint:Enabled", true);

    public int LocalPrintPort =>
        int.TryParse(MergedString("LocalPrint:Port"), out var p) ? Math.Clamp(p, 1, 65535) : 9247;

    public List<Printer> Printers
    {
        get
        {
            var printers = new List<Printer>();
            _configuration.GetSection("Printers").Bind(printers);
            if (printers.Count == 0 && _bundledInInstallDir != null)
                _bundledInInstallDir.GetSection("Printers").Bind(printers);
            if (printers.Count == 0)
                printers = BindPrintersFromProgramData();
            return printers;
        }
    }

    private static List<Printer> BindPrintersFromProgramData()
    {
        var root = ProgramDataAgentJsonReader.GetRootElement();
        if (root is null || !root.Value.TryGetProperty("Printers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<Printer>();
        foreach (var item in arr.EnumerateArray())
        {
            try
            {
                var p = JsonSerializer.Deserialize<Printer>(item.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (p is not null && !string.IsNullOrWhiteSpace(p.Id))
                    list.Add(p);
            }
            catch
            {
                // skip invalid entry
            }
        }

        return list;
    }

    /// <summary>
    /// (1) <c>RedisConnectionString</c> at root or <c>Redis:ConnectionString</c>.
    /// (2) Else built from <c>Redis:Host</c> etc. (StackExchange.Redis, ACL).
    /// </summary>
    private string ResolveRedisConnectionString()
    {
        if (_redisRuntimeCredentials.HasCredentials)
        {
            return BuildRedisConnectionString(
                _redisRuntimeCredentials.Host!.Trim(),
                (_redisRuntimeCredentials.Port > 0 ? _redisRuntimeCredentials.Port : 6379).ToString(),
                _redisRuntimeCredentials.User?.Trim(),
                _redisRuntimeCredentials.Password,
                MergedBool("Redis:Ssl", false),
                (MergedString("Redis:ClientName") ?? "URSPrinterAgent").Trim());
        }

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
        // Password: runtime creds handled above; legacy MSI password is install-dir only (not stale ProgramData).
        var password = BundledInstallDirString("Redis:Password");
        var ssl = MergedBool("Redis:Ssl", false);
        var clientName = (MergedString("Redis:ClientName") ?? "URSPrinterAgent").Trim();

        return BuildRedisConnectionString(host.Trim(), port.Trim(), user, password, ssl, clientName);
    }

    private static string BuildRedisConnectionString(
        string host,
        string port,
        string? user,
        string? password,
        bool ssl,
        string clientName)
    {
        var endpoint = $"{host}:{port}";
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
