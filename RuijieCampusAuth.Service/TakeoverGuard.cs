using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Service;

public sealed class TakeoverSavedState
{
    public List<IfeoBackup> Ifeo { get; set; } = [];
    public List<ServiceBackup> Services { get; set; } = [];
}

public sealed class IfeoBackup
{
    public string Name { get; set; } = "";
    public string? Debugger64 { get; set; }
    public string? Debugger32 { get; set; }
}

public sealed class ServiceBackup
{
    public string Name { get; set; } = "";
    public int? Start { get; set; }
}

public sealed class TakeoverGuard
{
    private const string OfficialDirectory = @"C:\Program Files\Ruijie Networks\Ruijie Supplicant";
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string MarkerName = "RuijieTakeover";
    private const string LegacyMarkerName = "HXXYTakeover";
    private const string MarkerValue = "RuijieCampusAuth";
    private const string DebuggerValue = @"C:\Windows\System32\cmd.exe /c exit";
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "7z.exe",
        "unins000.exe",
    };

    private readonly ILogger<TakeoverGuard> _logger;
    private readonly object _gate = new();
    private readonly List<string> _processNames = [];
    private readonly List<string> _serviceNames = [];
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _active;

    public static string StatePath => Path.Combine(AppContext.BaseDirectory, "takeover_state.json");
    public static string HeartbeatPath => Path.Combine(AppContext.BaseDirectory, "takeover.ini");
    public bool Active => _active;

    public TakeoverGuard(ILogger<TakeoverGuard> logger)
    {
        _logger = logger;
    }

    public void Apply(bool enabled)
    {
        lock (_gate)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (enabled)
                {
                    _logger.LogWarning("接管模式仅支持 Windows");
                }
                return;
            }
            if (enabled == _active)
            {
                return;
            }
            if (enabled)
            {
                RestoreLocked(force: true);
                ApplyLocked();
            }
            else
            {
                RestoreLocked(force: false);
            }
        }
    }

    public void CleanupStale()
    {
        lock (_gate)
        {
            if (OperatingSystem.IsWindows())
            {
                RestoreLocked(force: true);
            }
        }
    }

    private void ApplyLocked()
    {
        try
        {
            if (!Directory.Exists(OfficialDirectory))
            {
                _logger.LogWarning("未找到官方客户端目录，跳过接管阻止: {Path}", OfficialDirectory);
                return;
            }
            _processNames.Clear();
            _processNames.AddRange(
                Directory.EnumerateFiles(OfficialDirectory, "*.exe", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Where(name => !ExcludedProcesses.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            _serviceNames.Clear();
            _serviceNames.AddRange(FindRelatedServices());

            var state = new TakeoverSavedState();
            foreach (var name in _processNames)
            {
                var backup = new IfeoBackup
                {
                    Name = name,
                    Debugger64 = ReadDebugger(name, RegistryView.Registry64),
                    Debugger32 = ReadDebugger(name, RegistryView.Registry32),
                };
                state.Ifeo.Add(backup);
                WriteIfeo(name, RegistryView.Registry64);
                WriteIfeo(name, RegistryView.Registry32);
            }
            foreach (var name in _serviceNames)
            {
                var start = ReadServiceStart(name);
                state.Services.Add(new ServiceBackup { Name = name, Start = start });
                SetServiceDisabled(name);
                StopService(name);
                _logger.LogWarning("已成功阻止原版服务运行: {Service}", name);
            }
            File.WriteAllText(
                StatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            WriteHeartbeat();
            _active = true;
            _loopCancellation = new CancellationTokenSource();
            _loopTask = Task.Run(() => WatchLoop(_loopCancellation.Token));
            _logger.LogWarning(
                "接管模式已启用: {Processes} 个进程名, {Services} 个服务",
                _processNames.Count,
                _serviceNames.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启用接管模式失败");
            RestoreLocked(force: true);
        }
    }

    private IEnumerable<string> FindRelatedServices()
    {
        var result = new List<string>();
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return result;
        }
        foreach (var name in services.GetSubKeyNames())
        {
            // Never block our own service.
            if (name.Equals(ServiceInstaller.ServiceName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("HXXY-CampusAuth", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            using var key = services.OpenSubKey(name);
            if (key is null)
            {
                continue;
            }
            var image = key.GetValue("ImagePath") as string ?? "";
            var display = key.GetValue("DisplayName") as string ?? "";
            var expanded = Environment.ExpandEnvironmentVariables(image.Trim('"'));
            var related = expanded.Contains(OfficialDirectory, StringComparison.OrdinalIgnoreCase) ||
                          image.Contains(OfficialDirectory, StringComparison.OrdinalIgnoreCase) ||
                          display.Contains("Ruijie", StringComparison.OrdinalIgnoreCase) ||
                          display.Contains("锐捷", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("RJSu", StringComparison.OrdinalIgnoreCase);
            if (related)
            {
                result.Add(name);
            }
        }
        return result;
    }

    private void WatchLoop(CancellationToken token)
    {
        var reportedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!token.IsCancellationRequested)
        {
            foreach (var name in _processNames)
            {
                var processName = Path.GetFileNameWithoutExtension(name);
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        _logger.LogWarning(
                            "已成功阻止原版进程运行: {Process} (PID {Pid})",
                            name,
                            process.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "结束进程失败: {Process}", name);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            foreach (var name in _serviceNames)
            {
                try
                {
                    using var service = new ServiceController(name);
                    if (service.Status != ServiceControllerStatus.Stopped)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(2));
                        if (reportedServices.Add(name))
                        {
                            _logger.LogWarning("已成功阻止原版服务运行: {Service}", name);
                        }
                    }
                    else
                    {
                        reportedServices.Remove(name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "停止原版服务失败: {Service}", name);
                }
            }
            WriteHeartbeat();
            try
            {
                Task.Delay(500, token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RestoreLocked(bool force)
    {
        try
        {
            _loopCancellation?.Cancel();
            try
            {
                _loopTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Ignore shutdown races.
            }
            _loopCancellation?.Dispose();
            _loopCancellation = null;
            _loopTask = null;

            if (!File.Exists(StatePath))
            {
                // A frozen C: baseline may already contain our IFEO values.
                TakeoverPolicy.CleanupOwnedIfeo();
                _active = false;
                DeleteHeartbeat();
                return;
            }
            var state = JsonSerializer.Deserialize<TakeoverSavedState>(
                File.ReadAllText(StatePath));
            if (state is not null)
            {
                foreach (var backup in state.Ifeo)
                {
                    RestoreIfeo(backup.Name, RegistryView.Registry64, backup.Debugger64);
                    RestoreIfeo(backup.Name, RegistryView.Registry32, backup.Debugger32);
                }
                foreach (var backup in state.Services)
                {
                    if (backup.Start is not null)
                    {
                        WriteServiceStart(backup.Name, backup.Start.Value);
                    }
                }
                _logger.LogWarning("接管模式已关闭，原版进程/服务策略已恢复");
            }
            File.Delete(StatePath);
            TakeoverPolicy.CleanupOwnedIfeo();
            DeleteHeartbeat();
            _active = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复接管策略失败");
            if (force)
            {
                _active = false;
            }
        }
    }

    private static RegistryKey? OpenIfeo(string name, RegistryView view, bool writable)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        var path = $@"{IfeoPath}\{name}";
        return writable ? baseKey.CreateSubKey(path) : baseKey.OpenSubKey(path);
    }

    private static string? ReadDebugger(string name, RegistryView view)
    {
        using var key = OpenIfeo(name, view, writable: false);
        var debugger = key?.GetValue("Debugger") as string;
        if (string.Equals(debugger, DebuggerValue, StringComparison.OrdinalIgnoreCase))
        {
            // Our own marker may already be in the frozen C: baseline.
            return null;
        }
        return debugger;
    }

    private static void WriteIfeo(string name, RegistryView view)
    {
        using var key = OpenIfeo(name, view, writable: true);
        if (key is null)
        {
            return;
        }
        key.SetValue("Debugger", DebuggerValue, RegistryValueKind.String);
        key.SetValue(MarkerName, MarkerValue, RegistryValueKind.String);
    }

    private static void RestoreIfeo(string name, RegistryView view, string? debugger)
    {
        using var key = OpenIfeo(name, view, writable: true);
        if (key is null)
        {
            return;
        }
        try
        {
            key.DeleteValue(MarkerName, throwOnMissingValue: false);
            key.DeleteValue(LegacyMarkerName, throwOnMissingValue: false);
            if (string.IsNullOrEmpty(debugger))
            {
                key.DeleteValue("Debugger", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("Debugger", debugger, RegistryValueKind.String);
            }
        }
        catch
        {
            // Best effort; the service may not have access to a protected key.
        }
    }

    private static int? ReadServiceStart(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("Start") is int value ? value : null;
    }

    private static void WriteServiceStart(string name, int value)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
        key?.SetValue("Start", value, RegistryValueKind.DWord);
    }

    private static void SetServiceDisabled(string name)
    {
        try
        {
            WriteServiceStart(name, 4);
        }
        catch
        {
            // Stop below still prevents a running instance.
        }
    }

    private static void StopService(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3));
            }
        }
        catch
        {
            // The service may be absent or protected.
        }
    }

    private void WriteHeartbeat()
    {
        try
        {
            var content =
                $"enabled=1{Environment.NewLine}" +
                $"pid={Environment.ProcessId}{Environment.NewLine}" +
                $"exe={Environment.ProcessPath}{Environment.NewLine}";
            File.WriteAllText(HeartbeatPath, content);
        }
        catch
        {
            // Heartbeat failures are non-fatal.
        }
    }

    private static void DeleteHeartbeat()
    {
        try
        {
            File.Delete(HeartbeatPath);
        }
        catch
        {
            // Ignore.
        }
    }
}
