using System.Text.Json;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Worker.Config;

/// <summary>
/// Reads operator <c>%ProgramData%\URSPrinterAgent\agent.json</c> when it was created or updated after the host started
/// (optional AddJsonFile does not watch files that did not exist at startup).
/// </summary>
internal static class ProgramDataAgentJsonReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly object Gate = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteUtc;
    private static JsonDocument? _cachedDoc;

    public static string? GetString(string propertyName)
    {
        var root = GetRoot();
        if (root is null)
            return null;

        if (!root.Value.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    public static JsonElement? GetRootElement()
    {
        var root = GetRoot();
        return root;
    }

    /// <summary>True when ProgramData agent.json defines <c>Redis.Password</c> as empty (operator opts into per-restaurant ACL).</summary>
    public static bool ProgramDataOptedIntoPerRestaurantRedisCredentials()
    {
        var root = GetRootElement();
        if (root is null || !root.Value.TryGetProperty("Redis", out var redis))
            return false;
        if (!redis.TryGetProperty("Password", out var password))
            return false;
        return password.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(password.GetString());
    }

    private static JsonElement? GetRoot()
    {
        var path = Path.Combine(AgentProgramData.Root, "agent.json");
        if (!File.Exists(path))
            return null;

        var writeUtc = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            if (_cachedDoc is not null && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase)
                && _cachedWriteUtc == writeUtc)
            {
                return _cachedDoc.RootElement;
            }

            _cachedDoc?.Dispose();
            _cachedPath = path;
            _cachedWriteUtc = writeUtc;
            try
            {
                var bytes = File.ReadAllBytes(path);
                _cachedDoc = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                return _cachedDoc.RootElement;
            }
            catch
            {
                _cachedDoc = null;
                return null;
            }
        }
    }

    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _cachedDoc?.Dispose();
            _cachedDoc = null;
        }
    }
}
