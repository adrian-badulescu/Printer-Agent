using System.Text;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Worker.Logging;

/// <summary>
/// Scrie loguri în <c>%ProgramData%\URSPrinterAgent\logs\worker.log</c> (util când serviciul Windows nu are consolă și Event Viewer e gol / filtrat).
/// La peste 1 MiB, rotește: păstrează <c>worker.log</c> (activ) și arhive <c>worker.log.1</c>–<c>.4</c> (maxim 5 fișiere în total).
/// </summary>
public sealed class ProgramDataFileLoggerProvider : ILoggerProvider
{
    private const string LogFileName = "worker.log";
    private const long MaxLogFileBytes = 1024 * 1024;

    private StreamWriter _writer;
    private readonly string _logDir;
    private readonly string _activeLogPath;
    private readonly object _lock = new();

    public ProgramDataFileLoggerProvider()
    {
        _ = AgentProgramData.Root;
        _logDir = Path.Combine(AgentProgramData.Root, "logs");
        Directory.CreateDirectory(_logDir);
        _activeLogPath = Path.Combine(_logDir, LogFileName);
        _writer = OpenWriter();
    }

    private StreamWriter OpenWriter()
    {
        return new StreamWriter(
            new FileStream(_activeLogPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private void Rotate()
    {
        _writer.Flush();
        _writer.Dispose();

        TryDelete(ArchivedPath(4));
        TryMove(ArchivedPath(3), ArchivedPath(4));
        TryMove(ArchivedPath(2), ArchivedPath(3));
        TryMove(ArchivedPath(1), ArchivedPath(2));
        TryMove(_activeLogPath, ArchivedPath(1));

        _writer = OpenWriter();
    }

    private string ArchivedPath(int part) => Path.Combine(_logDir, $"{LogFileName}.{part}");

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void TryMove(string source, string destination)
    {
        if (File.Exists(source))
            File.Move(source, destination, overwrite: true);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose() => _writer.Dispose();

    internal void WriteLine(string line)
    {
        lock (_lock)
        {
            _writer.WriteLine(line);
            _writer.Flush();
            if (_writer.BaseStream is FileStream fs && fs.Length >= MaxLogFileBytes)
                Rotate();
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
