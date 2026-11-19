using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core.Debug;

public sealed class SystemInfoDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "system-info";
    public override string DisplayName => "系统信息";
    public override string Category => "system";
    public override string Phase => "P1";
    public override string Description => "显示程序、运行时、路径、服务状态与调试开关快照。";

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var assembly = Assembly.GetEntryAssembly();
        var application = assembly?.GetName();
        JsonObject? service = null;
        try
        {
            var state = ServiceInstaller.Query();
            service = new JsonObject
            {
                ["installed"] = state.Installed,
                ["running"] = state.Running,
                ["detail"] = state.Detail,
            };
        }
        catch (Exception ex)
        {
            service = new JsonObject
            {
                ["error"] = ex.Message,
            };
        }

        var settings = store.LoadSettings();
        var data = new JsonObject
        {
            ["application"] = new JsonObject
            {
                ["name"] = application?.Name ?? "unknown",
                ["version"] = application?.Version?.ToString() ?? "unknown",
                ["base_directory"] = store.BaseDirectory,
                ["settings_path"] = store.SettingsPath,
            },
            ["runtime"] = new JsonObject
            {
                ["os"] = RuntimeInformation.OSDescription,
                ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
                ["framework"] = RuntimeInformation.FrameworkDescription,
                ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["64_bit_process"] = Environment.Is64BitProcess,
            },
            ["service"] = service,
            ["debug"] = new JsonObject
            {
                ["enabled"] = settings.Debug.Enabled,
                ["features"] = settings.Debug.Features.Count,
            },
            ["accounts"] = new JsonObject
            {
                ["count"] = store.LoadAccounts().Count,
            },
        };
        return Ok("系统信息已采集", data.ToJsonString());
    }
}
