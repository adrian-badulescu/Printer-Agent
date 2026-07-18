using System.Text;

internal sealed class FpMateStubRequestLogger
{
    private readonly string _logPath;
    private int _sequence;

    public FpMateStubRequestLogger(string logPath)
    {
        _logPath = logPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[stub] Could not create log directory for {logPath}: {ex.Message}");
        }
    }

    public static string ResolveLogPath()
    {
        var custom = Environment.GetEnvironmentVariable("FPMATE_STUB_LOG");
        if (!string.IsNullOrWhiteSpace(custom))
            return custom.Trim();

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            return Path.Combine(programData, "URSPrinterAgent", "logs", "fpmate-stub.log");
        }

        return Path.Combine(AppContext.BaseDirectory, "fpmate-stub.log");
    }

    public void LogRequest(
        HttpRequest request,
        string body,
        string innerXml,
        string action,
        string responseSummary)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var remote = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        var remotePort = request.HttpContext.Connection.RemotePort;
        var soapAction = request.Headers["SOAPAction"].ToString();
        var contentType = request.ContentType ?? "(none)";
        var userAgent = request.Headers.UserAgent.ToString();

        var header = $"#{seq} {DateTime.UtcNow:O} POST {request.Path} from {remote}:{remotePort} action={action} response={responseSummary}";
        Console.WriteLine(header);
        Console.WriteLine($"  Content-Type: {contentType}");
        if (!string.IsNullOrWhiteSpace(soapAction))
            Console.WriteLine($"  SOAPAction: {soapAction}");
        if (!string.IsNullOrWhiteSpace(userAgent))
            Console.WriteLine($"  User-Agent: {userAgent}");
        Console.WriteLine($"  BodyLength: {body.Length} chars");
        Console.WriteLine($"  InnerXml: {TruncateForConsole(innerXml)}");

        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine(header);
        sb.AppendLine($"Content-Type: {contentType}");
        sb.AppendLine($"SOAPAction: {soapAction}");
        sb.AppendLine($"User-Agent: {userAgent}");
        sb.AppendLine($"BodyLength: {body.Length}");
        sb.AppendLine("--- SOAP body ---");
        sb.AppendLine(body);
        sb.AppendLine("--- Inner XML ---");
        sb.AppendLine(innerXml);
        sb.AppendLine("--- Response ---");
        sb.AppendLine(responseSummary);
        sb.AppendLine();

        WriteToFile(sb.ToString());
    }

    public void LogStartup(string listenUrl, string logPath)
    {
        var line =
            $"{DateTime.UtcNow:O} STUB START listen={listenUrl} log={logPath} pid={Environment.ProcessId}{Environment.NewLine}" +
            $"Waiting for POST /cgi-bin/fpmate.cgi from Printer Agent (not from backend directly).{Environment.NewLine}" +
            $"If this file stays empty after print jobs, check worker.log for the FpMate URL (must hit this stub port).{Environment.NewLine}{Environment.NewLine}";
        Console.WriteLine($"FpMate stub listening on {listenUrl} — log file: {logPath}");
        WriteToFile(line);
    }

    private void WriteToFile(string text)
    {
        try
        {
            File.AppendAllText(_logPath, text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[stub] Failed to write log file {_logPath}: {ex.Message}");
        }
    }

    private static string TruncateForConsole(string value) =>
        value.Length <= 240 ? value : value[..240] + "...";
}
