using System.Text.Json;

namespace PrinterAgent.Infrastructure.Observability;

/// <summary>NDJSON debug log for Cursor debug session (no secrets).</summary>
public static class DebugSessionLog
{
    private const string SessionId = "38fcde";
    private static readonly string[] LogPaths =
    [
        @"C:\Users\adria\Projects\Printer-Agent\debug-38fcde.log",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "URSPrinterAgent",
            "debug-38fcde.log")
    ];

    public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        // #region agent log
        var payload = new Dictionary<string, object?>
        {
            ["sessionId"] = SessionId,
            ["hypothesisId"] = hypothesisId,
            ["location"] = location,
            ["message"] = message,
            ["data"] = data,
            ["runId"] = runId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
        foreach (var path in LogPaths)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, line);
            }
            catch
            {
                // ignore debug log failures per path
            }
        }
        // #endregion
    }
}
