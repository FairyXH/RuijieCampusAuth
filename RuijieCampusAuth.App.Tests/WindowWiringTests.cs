using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RuijieCampusAuth.App.ViewModels;
using RuijieCampusAuth.App.Views;
using Xunit;

namespace RuijieCampusAuth.App.Tests;

/// <summary>
/// Window-level smoke: the XAML must instantiate against the real view
/// models, and every button command referenced in the XAML must exist on the
/// view model (no dead buttons).
/// </summary>
public sealed class WindowWiringTests
{
    [AvaloniaFact]
    public void MainWindow_constructs_with_six_tabs()
    {
        var vm = new MainViewModel(new NoopServiceBridge(), () =>
            [
                new RuijieCampusAuth.Core.PcapDeviceInfo(
                    @"\Device\NPF_{TEST}",
                    "Test Adapter"),
            ],
            _ => { });
        var window = new MainWindow { DataContext = vm };
        var tabs = window.GetControl<Avalonia.Controls.TabControl>("MainTabs");
        Assert.True(tabs.Items.Count >= 6, $"tabs = {tabs.Items.Count}");
        window.Close();
    }

    [AvaloniaFact]
    public void Every_command_in_xaml_exists_on_view_model()
    {
        var xaml = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "RuijieCampusAuth.App", "Views", "MainWindow.axaml"));
        Assert.False(string.IsNullOrWhiteSpace(xaml));

        var commandNames = Regex.Matches(xaml, @"\{Binding\s+(\w+Command)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.True(commandNames.Count >= 24, $"commands in xaml = {commandNames.Count}");

        var mainCommands = typeof(MainViewModel).GetProperties()
            .Where(p => p.Name.EndsWith("Command"))
            .Select(p => p.Name)
            .ToHashSet();
        var debugCommands = typeof(DebugViewModel).GetProperties()
            .Where(p => p.Name.EndsWith("Command"))
            .Select(p => p.Name)
            .ToHashSet();

        var missing = commandNames
            .Where(name => !mainCommands.Contains(name) && !debugCommands.Contains(name))
            .ToList();
        Assert.True(
            missing.Count == 0,
            $"XAML references commands that do not exist: {string.Join(", ", missing)}");
    }

    private sealed class NoopServiceBridge : RuijieCampusAuth.App.ViewModels.IServiceBridge
    {
        public RuijieCampusAuth.Core.ServiceState Query() =>
            new(true, false, "noop");
        public void Install() { }
        public void Uninstall() { }
        public void Start() { }
        public void Stop() { }
    }
}
