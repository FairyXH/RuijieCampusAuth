using Microsoft.Extensions.Logging;
using RuijieCampusAuth.Service;

var builder = Host.CreateApplicationBuilder(args);
var root = AppContext.BaseDirectory;
Directory.CreateDirectory(Path.Combine(root, "logs"));
builder.Logging.AddProvider(
    new FileLoggerProvider(Path.Combine(root, "logs", "service.log")));

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "RuijieCampusAuth";
    });
}
else if (OperatingSystem.IsLinux() &&
         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
{
    builder.Services.AddSystemd();
}

builder.Services.AddSingleton<TakeoverGuard>();
builder.Services.AddHostedService<TakeoverService>();
builder.Services.AddHostedService<AuthWorker>();
builder.Services.AddHostedService<DebugPipeServer>();

var host = builder.Build();
host.Run();
