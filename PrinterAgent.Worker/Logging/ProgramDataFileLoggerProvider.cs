using System.Text;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Worker.Logging;

/// <summary>
/// Scrie loguri în <c>%ProgramData%\URSPrinterAgent\logs\worker.log</c> (util când serviciul Windows nu are consolă și Event Viewer e gol / filtrat).
/// </summary>
public sealed class ProgramDataFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public ProgramDataFileLoggerProvider()
    {
        _ = AgentProgramData.Root;
        var dir = Path.Combine(AgentProgramData.Root, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "worker.log");
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose() => _writer.Dispose();

    internal void WriteLine(string line)
    {
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly ProgramDataFileLoggerProvider _provider;

        public FileLogger(string category, ProgramDataFileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var msg = formatter?.Invoke(state, exception) ?? state?.ToString() ?? string.Empty;
            var sb = new StringBuilder()
                .Append(DateTime.UtcNow.ToString("O"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(_category)
                .Append(": ")
                .Append(msg);
            if (exception != null)
            {
                sb.AppendLine().Append(exception);
            }

            _provider.WriteLine(sb.ToString());
        }
    }
}
