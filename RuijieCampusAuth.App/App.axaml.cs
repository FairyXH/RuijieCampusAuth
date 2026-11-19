using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using RuijieCampusAuth.App.ViewModels;
using RuijieCampusAuth.App.Views;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.App;

public partial class App : Application
{
    private TrayIcon? _tray;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UserStore.MigrateLegacy(AppContext.BaseDirectory);
            new UserStore(AppContext.BaseDirectory).UpgradePasswordProtection();
            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            var arguments = desktop.Args ?? [];
            var trayMode = arguments.Contains("--tray");
            DebugLog($"args={string.Join(",", arguments)}");
            try
            {
                _tray = CreateTray(window, desktop);
                DebugLog("tray created");
            }
            catch (Exception ex)
            {
                DebugLog($"tray failed: {ex}");
                _tray = null;
            }
            DebugLog($"trayMode={trayMode} tray={_tray is not null}");
            if (!trayMode || _tray is null)
            {
                window.Show();
            }
            else
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.Closing += (_, args) =>
                {
                    args.Cancel = true;
                    window.Hide();
                };
                window.Hide();
                Avalonia.Threading.Dispatcher.UIThread.Post(window.Hide);
            }
            if (arguments.Contains("--auto"))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => viewModel.StartCommand.Execute(null));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void DebugLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "gui_boot.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must not break startup.
        }
    }

    private TrayIcon? CreateTray(
        MainWindow window,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(
            new Uri("avares://RuijieCampusAuth/Assets/avalonia-logo.ico")));
        var menu = new NativeMenu();
        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        };
        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => desktop.Shutdown();
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "锐捷认证器",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        return tray;
    }
}
