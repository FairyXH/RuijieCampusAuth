using System.Text;
using Microsoft.Extensions.Logging;

namespace RuijieCampusAuth.Service;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
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
            {
                return;
            }
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }
            owner.Write(line);
        }
    }
}
