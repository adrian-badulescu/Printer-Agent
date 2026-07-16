using System.Text.Json;

namespace PrinterAgent.Application.Observability;

/// <summary>NDJSON debug logs for Cursor debug sessions. No secrets/PII.</summary>
public static class DebugSessionLog
{
    private const string SessionId = "6b7cb8";
    private const string IngestUrl = "http://127.0.0.1:7341/ingest/5b84ace2-df1e-4f3a-9af6-330c89f47519";

    // #region agent log
    public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        var payload = JsonSerializer.Serialize(new
        {
            sessionId = SessionId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            runId,
        });

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "URSPrinterAgent");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug-6b7cb8.log"), payload + Environment.NewLine);
        }
        catch
        {
            // ignore file errors
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                content.Headers.Add("X-Debug-Session-Id", SessionId);
                await client.PostAsync(IngestUrl, content).ConfigureAwait(false);
            }
            catch
            {
                // ignore ingest errors
            }
        });
    }
    // #endregion
}
