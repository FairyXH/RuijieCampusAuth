using Avalonia.Headless.XUnit;
using RuijieCampusAuth.App.ViewModels;
using Xunit;

namespace RuijieCampusAuth.App.Tests;

/// <summary>调试页每个按钮的命令级测试。</summary>
public sealed class DebugViewModelTests
{
    [AvaloniaFact]
    public async Task Refresh_loads_all_features()
    {
        var vm = new DebugViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.Features.Count >= 21, $"features = {vm.Features.Count}");
        Assert.Contains("已加载", vm.Status);
        Assert.NotNull(vm.SelectedFeature);
    }

    [AvaloniaFact]
    public async Task Save_persists_enabled_flag()
    {
        var vm = new DebugViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Enabled = true;
        await vm.SaveCommand.ExecuteAsync(null);
        // SaveAsync refreshes afterwards, so verify the persisted state.
        var settings = new RuijieCampusAuth.Core.UserStore().LoadSettings();
        Assert.True(settings.Debug.Enabled);
    }

    [AvaloniaFact]
    public async Task Run_executes_system_info()
    {
        var vm = new DebugViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        var target = vm.Features.FirstOrDefault(f => f.Name == "system-info");
        Assert.NotNull(target);
        vm.SelectedFeature = target;
        vm.FeatureEnabled = true;
        vm.Enabled = true;
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Contains("OK", vm.ResultText);

        // 执行后的结果必须留在屏幕上：Refresh 重建列表（选中项的 State 已变）
        // 不得清空 ResultText —— 这正是“输出闪一下消失”的回归点。
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("OK", vm.ResultText);
        vm.FeatureEnabled = !vm.FeatureEnabled;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("OK", vm.ResultText);
    }

    [AvaloniaFact]
    public async Task Selecting_feature_prefills_arguments_from_school_config()
    {
        var store = new RuijieCampusAuth.Core.UserStore();
        var settings = store.LoadSettings();
        settings.SelfServiceUrl = "http://netss.hxxy.edu.cn/selfservice/";
        store.SaveSettings(settings);

        var vm = new DebugViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        var target = vm.Features.FirstOrDefault(f => f.Name == "self-service");
        Assert.NotNull(target);
        vm.SelectedFeature = target;

        // 预填的参数必须能反序列化并包含档案里的 URL
        var parsed = System.Text.Json.JsonDocument.Parse(vm.ArgumentsJson);
        Assert.True(
            parsed.RootElement.TryGetProperty("url", out var url) &&
            url.GetString() == "http://netss.hxxy.edu.cn/selfservice/",
            $"args = {vm.ArgumentsJson}");
    }

    [AvaloniaFact]
    public async Task Stop_reports_result()
    {
        var vm = new DebugViewModel();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.NotNull(vm.SelectedFeature);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Contains("停止", vm.Status);
    }

    [AvaloniaFact]
    public void OpenLog_handles_missing_file()
    {
        var vm = new DebugViewModel();
        vm.OpenLogCommand.Execute(null);
        // Either opens the existing debug log or reports it is missing;
        // both paths must leave a status message and not throw.
        Assert.False(string.IsNullOrWhiteSpace(vm.Status));
    }
}
