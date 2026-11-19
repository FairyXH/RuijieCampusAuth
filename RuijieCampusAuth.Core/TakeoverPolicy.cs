using System.Text.Json;
using Microsoft.Win32;

namespace RuijieCampusAuth.Core;

public sealed class TakeoverPolicyBackup
{
    public List<TakeoverIfeoBackup> Ifeo { get; set; } = [];
    public List<TakeoverServiceBackup> Services { get; set; } = [];
}

public sealed class TakeoverIfeoBackup
{
    public string Name { get; set; } = "";
    public string? Debugger64 { get; set; }
    public string? Debugger32 { get; set; }
}

public sealed class TakeoverServiceBackup
{
    public string Name { get; set; } = "";
    public int? Start { get; set; }
}

public static class TakeoverPolicy
{
    private const string IfeoPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string MarkerName = "RuijieTakeover";
    private const string LegacyMarkerName = "HXXYTakeover";
    private const string DebuggerValue = @"C:\Windows\System32\cmd.exe /c exit";
    private const string OfficialDirectory =
        @"C:\Program Files\Ruijie Networks\Ruijie Supplicant";

    public static string StatePath(string installDirectory) =>
        Path.Combine(installDirectory, "takeover_state.json");

    public static string HeartbeatPath(string installDirectory) =>
        Path.Combine(installDirectory, "takeover.ini");

    public static void Restore(string installDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var statePath = StatePath(installDirectory);
        try
        {
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<TakeoverPolicyBackup>(
                    File.ReadAllText(statePath));
                if (state is not null)
                {
                    foreach (var backup in state.Ifeo)
                    {
                        if (string.IsNullOrWhiteSpace(backup.Name))
                        {
                            continue;
                        }
                        RestoreIfeo(
                            backup.Name,
                            RegistryView.Registry64,
                            backup.Debugger64);
                        RestoreIfeo(
                            backup.Name,
                            RegistryView.Registry32,
                            backup.Debugger32);
                    }
                    foreach (var backup in state.Services)
                    {
                        if (backup.Start is not null)
                        {
                            WriteServiceStart(backup.Name, backup.Start.Value);
                        }
                    }
                }
            }
            else
            {
                CleanupMarkerlessIfeo();
            }
        }
        catch
        {
            // Best effort: the launcher must continue to the original client.
        }
        finally
        {
            TryDelete(statePath);
            TryDelete(HeartbeatPath(installDirectory));
        }
    }

    public static void CleanupOwnedIfeo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        CleanupMarkerlessIfeo();
    }

    private static void CleanupMarkerlessIfeo()
    {
        if (!Directory.Exists(OfficialDirectory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(
                     OfficialDirectory,
                     "*.exe",
                     SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            RestoreIfeo(name, RegistryView.Registry64, null);
            RestoreIfeo(name, RegistryView.Registry32, null);
        }
    }

    private static void RestoreIfeo(
        string name,
        RegistryView view,
        string? originalDebugger)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey($@"{IfeoPath}\{name}", writable: true);
            if (key is null)
            {
                return;
            }
            var marker = key.GetValue(MarkerName) as string;
            var legacyMarker = key.GetValue(LegacyMarkerName) as string;
            var debugger = key.GetValue("Debugger") as string;
            if (!string.Equals(marker, "RuijieCampusAuth", StringComparison.Ordinal) &&
                !string.Equals(legacyMarker, "RuijieCampusAuth", StringComparison.Ordinal) &&
                !string.Equals(debugger, DebuggerValue, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            key.DeleteValue(MarkerName, throwOnMissingValue: false);
            key.DeleteValue(LegacyMarkerName, throwOnMissingValue: false);
            if (string.IsNullOrEmpty(originalDebugger))
            {
                key.DeleteValue("Debugger", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("Debugger", originalDebugger, RegistryValueKind.String);
            }
        }
        catch
        {
            // Protected keys or races: keep going.
        }
    }

    private static void WriteServiceStart(string name, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{name}",
                writable: true);
            key?.SetValue("Start", value, RegistryValueKind.DWord);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Ignore.
        }
    }
}
