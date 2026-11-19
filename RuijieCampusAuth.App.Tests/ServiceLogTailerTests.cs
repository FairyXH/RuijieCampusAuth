using RuijieCampusAuth.App.Services;
using Xunit;

namespace RuijieCampusAuth.App.Tests;

public sealed class ServiceLogTailerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "rj-tailer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Tail_reads_history_and_new_lines()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "service.log");
        await File.AppendAllTextAsync(path, "before-start-line-1\nbefore-start-line-2\n");
        using var tailer = new ServiceLogTailer(path);
        var lines = new List<string>();
        var done = new TaskCompletionSource();
        tailer.LineAppended += line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
            if (lines.Count >= 2)
            {
                done.TrySetResult();
            }
        };
        tailer.Start();
        await File.AppendAllTextAsync(path, "after-start-line\n");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            ["before-start-line-2", "after-start-line"],
            lines);
    }

    [Fact]
    public async Task Tail_handles_missing_file_then_creation()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "service.log");
        using var tailer = new ServiceLogTailer(path);
        var lines = new List<string>();
        var done = new TaskCompletionSource();
        tailer.LineAppended += line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
            done.TrySetResult();
        };
        tailer.Start();
        await File.AppendAllTextAsync(path, "first line\n");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first line"], lines);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}
