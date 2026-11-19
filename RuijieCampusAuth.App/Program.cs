using Avalonia;
using System;
using System.Threading;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--restore-client", StringComparison.OrdinalIgnoreCase))
            {
                TakeoverPolicy.Restore(AppContext.BaseDirectory);
                return;
            }
        }
        using var mutex = new Mutex(true, "RuijieCampusAuth-GUI", out var createdNew);
        if (!createdNew)
        {
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
