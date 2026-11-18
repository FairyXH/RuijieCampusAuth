using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Service;

public sealed class TakeoverService(
    ILogger<TakeoverService> logger,
    TakeoverGuard guard) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        guard.CleanupStale();
        bool? applied = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var enabled = ServiceConfig.ReadTakeoverEnabled(ServiceConfig.ConfigPath);
                if (applied != enabled)
                {
                    guard.Apply(enabled);
                    applied = enabled;
                    logger.LogInformation(
                        "接管模式配置: {Enabled}",
                        enabled ? "开启" : "关闭");
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "读取接管模式配置失败");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        guard.Apply(false);
        return base.StopAsync(cancellationToken);
    }
}
