using System.Text.Json;

namespace PrinterAgent.Worker;

/// <summary>NDJSON debug log for Cursor debug session (no secrets).</summary>
internal static class DebugSessionLog
{
    private const string LogPath = @"C:\Users\adria\Projects\Printer-Agent\debug-38fcde.log";

    internal static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        // #region agent log
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = "38fcde",
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["runId"] = runId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            File.AppendAllText(LogPath, JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            // ignore debug log failures
        }
        // #endregion
    }
}
