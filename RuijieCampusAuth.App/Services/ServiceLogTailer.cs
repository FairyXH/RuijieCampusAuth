using System.IO;
using System.Text;

namespace RuijieCampusAuth.App.Services;

/// <summary>
/// Tails the service log file so the GUI mirrors service output with near-zero
/// delay. The service writes with AutoFlush and FileShare.ReadWrite, so a
/// watcher-triggered read shows each line the moment it is flushed.
/// </summary>
public sealed class ServiceLogTailer : IDisposable
{
    private readonly string _path;
    private readonly string _directory;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private readonly Timer _drain;
    private long _offset;
    private bool _pending;
    private bool _started;

    public event Action<string>? LineAppended;
    public event Action<string>? RotationDetected;

    public ServiceLogTailer(string path)
    {
        _path = path;
        _directory = Path.GetDirectoryName(path) ?? ".";
        _watcher = new FileSystemWatcher(_directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                           NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        // Coalesce bursts of change events into one read per tick.
        _drain = new Timer(
            _ =>
            {
                bool fire;
                lock (_gate)
                {
                    fire = _pending;
                    _pending = false;
                }
                if (fire)
                {
                    ReadNewLines();
                }
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <summary>Loads the most recent history and starts watching.</summary>
    public void Start(int historyBytes = 48 * 1024)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists)
                {
                    _offset = Math.Max(0, info.Length - historyBytes);
                    ReadNewLines(skipFirstPartial: true);
                }
            }
            catch (IOException)
            {
                _offset = 0;
            }
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnChanged(object? sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _pending = true;
        }
        try
        {
            _drain.Change(80, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void ReadNewLines(bool skipFirstPartial = false)
    {
        List<string> lines;
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length < _offset)
                {
                    if (info.Exists && info.Length < _offset)
                    {
                        // Rotated or truncated: restart from the beginning.
                        _offset = 0;
                        RotationDetected?.Invoke(_path);
                    }
                    else
                    {
                        return;
                    }
                }
                if (info.Length == _offset)
                {
                    return;
                }
                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                stream.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                _offset = stream.Position;
                var list = new List<string>();
                var payload = text;
                if (skipFirstPartial && payload.Length > 0 && !payload.StartsWith('\n'))
                {
                    // The first chunk may start mid-line; drop the partial.
                    var index = payload.IndexOf('\n');
                    if (index >= 0)
                    {
                        payload = payload[(index + 1)..];
                    }
                    else if (_offset < info.Length)
                    {
                        return; // Wait for the rest of the line.
                    }
                    else
                    {
                        payload = "";
                    }
                }
                var builder = new StringBuilder();
                foreach (var ch in payload)
                {
                    if (ch is '\r' or '\n')
                    {
                        if (builder.Length > 0)
                        {
                            list.Add(builder.ToString());
                            builder.Clear();
                        }
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                }
                if (builder.Length > 0)
                {
                    // Keep the trailing partial line buffered for next round.
                    _offset -= Encoding.UTF8.GetByteCount(builder.ToString());
                }
                lines = list;
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
        foreach (var line in lines)
        {
            LineAppended?.Invoke(line);
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _drain.Dispose();
    }
}
