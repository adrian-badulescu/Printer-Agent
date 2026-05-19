using System.Text.Json;

namespace PrinterAgent.Worker;

/// <summary>Debug-mode NDJSON (session 7379f5). Removed after verification.</summary>
internal static class DebugSessionLog
{
    private const string SessionId = "7379f5";
    private const string IngestUrl = "http://127.0.0.1:7278/ingest/659d4b68-7820-48ed-a0b7-72ad405fac18";
    private static readonly string NdjsonPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "URSPrinterAgent",
        "logs",
        "debug-7379f5.ndjson");

    public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        // #region agent log
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["runId"] = runId,
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(payload);
            var dir = Path.GetDirectoryName(NdjsonPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(NdjsonPath, line + Environment.NewLine);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    using var content = new StringContent(line, System.Text.Encoding.UTF8, "application/json");
                    content.Headers.Add("X-Debug-Session-Id", SessionId);
                    await http.PostAsync(IngestUrl, content).ConfigureAwait(false);
                }
                catch { /* agent PC may not reach Cursor ingest */ }
            });
        }
        catch { /* ignore */ }
        // #endregion
    }
}
