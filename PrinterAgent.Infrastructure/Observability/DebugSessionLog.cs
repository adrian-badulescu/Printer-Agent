using System.Text.Json;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.Observability;

public static class DebugSessionLog
{
    private const string SessionId = "7379f5";

    private static readonly string[] CandidatePaths =
    [
        @"c:\W\QRFE\debug-7379f5.log",
        Path.Combine(AgentProgramData.Root, "debug-7379f5.log"),
    ];

    public static void Write(string location, string message, object? data = null, string? hypothesisId = null, string? runId = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                sessionId = SessionId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                location,
                message,
                data,
                hypothesisId,
                runId,
            });

            var path = Environment.GetEnvironmentVariable("DEBUG_SESSION_LOG");
            if (!string.IsNullOrWhiteSpace(path))
            {
                AppendLine(path, line);
                return;
            }

            foreach (var candidate in CandidatePaths)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                    AppendLine(candidate, line);
                    return;
                }
                catch
                {
                    // try next
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void AppendLine(string path, string line) =>
        File.AppendAllText(path, line + Environment.NewLine);
}
