using System.Text;
using Microsoft.Extensions.Logging;

namespace RuijieCampusAuth.Service;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private StreamWriter _writer;

    public FileLoggerProvider(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _writer = OpenWriter(path);
    }

    private static StreamWriter OpenWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                _writer.WriteLine(line);
            }
            catch
            {
                // Logging must never take down the auth service.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (_writer.BaseStream.Length < MaxBytes)
            {
                return;
            }
            _writer.Dispose();
            var backup = _path + ".1";
            File.Delete(backup);
            File.Move(_path, backup);
            _writer = OpenWriter(_path);
        }
        catch
        {
            try
            {
                _writer = OpenWriter(_path);
            }
            catch
            {
                // Keep the service alive even if logging is unavailable.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

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
