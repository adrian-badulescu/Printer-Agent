using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PrinterAgent.Infrastructure.Diagnostics;

// #region agent log
/// <summary>
/// Fire-and-forget NDJSON debug logger for session 7379f5. Writes locally to ProgramData
/// and posts to the local ingest endpoint when available. NEVER throws.
/// </summary>
public static class DebugSessionLog
{
    private const string SessionId = "7379f5";
    private const string IngestUrl = "http://127.0.0.1:7278/ingest/659d4b68-7820-48ed-a0b7-72ad405fac18";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(750) };

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "URSPrinterAgent",
        "logs",
        "debug-7379f5.ndjson");

    public static void Write(string hypothesisId, string location, string message, object data, string runId = "post-fix")
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                sessionId = SessionId,
                runId,
                hypothesisId,
                location,
                message,
                data,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            TryAppendFile(json);
            TryPostHttp(json);
        }
        catch
        {
            // never throw from diagnostics
        }
    }

    private static void TryAppendFile(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, json + Environment.NewLine);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryPostHttp(string json)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, IngestUrl);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("X-Debug-Session-Id", SessionId);
            _ = Http.SendAsync(req);
        }
        catch
        {
            // ignore
        }
    }
}
// #endregion
