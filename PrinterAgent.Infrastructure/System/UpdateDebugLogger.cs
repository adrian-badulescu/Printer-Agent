using System.Text.Json;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.System;

/// <summary>NDJSON debug sink for auto-update investigation (debug session 25e5dc).</summary>
internal static class UpdateDebugLogger
{
    private static readonly string LogPath = Path.Combine(AgentProgramData.Root, "debug-25e5dc.log");

    internal static void Log(string hypothesisId, string location, string message, object data, string runId = "agent")
    {
        try
        {
            var entry = new Dictionary<string, object?>
            {
                ["sessionId"] = "25e5dc",
                ["runId"] = runId,
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(entry);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // best effort — never break update flow
        }
    }
}
