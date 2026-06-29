using System.Text.Json;

namespace PrinterAgent.Infrastructure.Observability;

public static class DebugSessionLog
{
    private const string SessionId = "38fcde";
    private const string IngestUrl = "http://127.0.0.1:7341/ingest/5b84ace2-df1e-4f3a-9af6-330c89f47519";
    private static readonly string[] LogPaths =
    [
        Path.Combine(AppContext.BaseDirectory, "debug-38fcde.log"),
        @"C:\ProgramData\URSPrinterAgent\debug-38fcde.log",
        @"C:\Users\adria\Projects\Printer-Agent\debug-38fcde.log",
    ];

    public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        var payload = new Dictionary<string, object?>
        {
            ["sessionId"] = SessionId,
            ["hypothesisId"] = hypothesisId,
            ["location"] = location,
            ["message"] = message,
            ["data"] = data,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["runId"] = runId,
        };

        var line = JsonSerializer.Serialize(payload);

        foreach (var path in LogPaths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // ignore path failures
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var content = new StringContent(line, global::System.Text.Encoding.UTF8, "application/json");
                content.Headers.Add("X-Debug-Session-Id", SessionId);
                await client.PostAsync(IngestUrl, content).ConfigureAwait(false);
            }
            catch
            {
                // ignore ingest failures
            }
        });
    }
}
