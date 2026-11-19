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
