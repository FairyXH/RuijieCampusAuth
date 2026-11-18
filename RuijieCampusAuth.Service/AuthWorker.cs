using System.Net.Sockets;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Service;

public sealed class AuthWorker(ILogger<AuthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceConfig? config = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                config = ServiceConfig.Load(ServiceConfig.ConfigPath);
                logger.LogInformation("加载服务配置: {Path}", ServiceConfig.ConfigPath);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "加载服务配置失败");
                ServiceStatus.Write("error", $"配置加载失败: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        if (config is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await Task.Run(
                () => Authenticate(config, stoppingToken),
                stoppingToken);
            if (result.ok)
            {
                ServiceStatus.Write(
                    "authenticated",
                    "认证成功",
                    result.interfaceName,
                    result.server);
                logger.LogInformation(
                    "认证成功: {Interface} -> {Server}",
                    result.interfaceName,
                    result.server ?? "-");
                if (!config.Settings.Preempt)
                {
                    ServiceStatus.Write("idle", "认证成功，服务保持运行");
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    break;
                }
                await HoldAsync(config, stoppingToken);
            }
            else
            {
                ServiceStatus.Write("retrying", "认证失败，等待重试");
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, config.Settings.RetryInterval)),
                    stoppingToken);
            }
        }

        ServiceStatus.Write("stopped", "服务停止");
        logger.LogInformation("认证服务停止");
    }

    private (bool ok, string? server, string interfaceName) Authenticate(
        ServiceConfig config,
        CancellationToken stoppingToken)
    {
        var interfaceName = ResolveInterface(config.Settings.Interface);
        logger.LogInformation("使用网卡: {Interface}", interfaceName);
        ServiceStatus.Write("authenticating", "正在认证", interfaceName);
        var locked = config.Settings.ServerLock &&
                     !string.IsNullOrWhiteSpace(config.Settings.LockedServer)
            ? config.Settings.LockedServer
            : null;
        using var supplicant = new Supplicant(
            interfaceName,
            config.Username,
            config.Password,
            timeout: config.Settings.Timeout,
            retries: config.Settings.Retries,
            repeats: config.Settings.Repeats,
            startTimeout: config.Settings.StartTimeout,
            lockedServer: locked,
            serviceName: config.Settings.ServiceName,
            staticId: config.Settings.StaticId,
            log: message => logger.LogInformation("{Message}", message),
            error: message => logger.LogError("{Message}", message));
        using var registration = stoppingToken.Register(supplicant.Stop);
        var ok = supplicant.Authenticate();
        return (ok, supplicant.ServerMac, interfaceName);
    }

    private static string ResolveInterface(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        var devices = PcapNative.ListDevices();
        var selected = devices
            .Where(item =>
                !item.Name.Contains("loopback", StringComparison.OrdinalIgnoreCase) &&
                !item.Description.Contains("loopback", StringComparison.OrdinalIgnoreCase) &&
                !item.Description.Contains("vmware", StringComparison.OrdinalIgnoreCase) &&
                !item.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item =>
                item.Description.Contains("USB GbE", StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return selected?.Name
               ?? devices.FirstOrDefault()?.Name
               ?? throw new InvalidOperationException("没有可用的抓包网卡");
    }

    private async Task HoldAsync(ServiceConfig config, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await OnlineAsync(config.Settings, stoppingToken))
            {
                logger.LogWarning("检测到掉线，开始补认证");
                ServiceStatus.Write("offline", "检测到掉线，开始补认证");
                return;
            }
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(0.5, config.Settings.CheckInterval)),
                stoppingToken);
        }
    }

    private static async Task<bool> OnlineAsync(
        ServiceSettings settings,
        CancellationToken stoppingToken)
    {
        var url = settings.ConnectivityUrl.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return true;
        }
        if (uri.Host is "localhost" or "127.0.0.1" or "::1")
        {
            return true;
        }
        var port = uri.Port > 0
            ? uri.Port
            : uri.Scheme == "https" ? 443 : 80;
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(uri.Host, port, timeout.Token);
            return true;
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
