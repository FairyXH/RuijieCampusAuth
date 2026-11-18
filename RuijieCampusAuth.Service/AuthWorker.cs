using System.Net.Sockets;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Service;

public sealed class AuthWorker(ILogger<AuthWorker> logger) : BackgroundService
{
    private readonly UserStore _store = new(ServiceConfig.ServiceRoot);
    private GuardianClient? _guardian;
    private int _guardianFailures;
    private DateTimeOffset _nextGuardianCheck = DateTimeOffset.MinValue;
    private bool? _lastGuardianDecision;

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
        if (config.Settings.GuardianEnabled)
        {
            try
            {
                _guardian = new GuardianClient(config.Settings.GuardianUrl);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "网络管家地址无效，系统服务回退本机探测");
            }
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

        _guardian?.Dispose();
        _guardian = null;
        ServiceStatus.Write("stopped", "服务停止");
        logger.LogInformation("认证服务停止");
    }

    private (bool ok, string? server, string interfaceName) Authenticate(
        ServiceConfig config,
        CancellationToken stoppingToken)
    {
        var interfaceName = ResolveInterface(config.Settings);
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
        var description = PcapNative.ListDevices()
            .FirstOrDefault(item => item.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase))
            ?.Description ?? interfaceName;
        if (supplicant.Connected)
        {
            _store.SaveDiscovery(
                interfaceName,
                description,
                supplicant.SourceMac,
                supplicant.ServerMac ?? "");
            _store.UpsertServer(
                supplicant.ServerMac ?? "",
                interfaceName,
                description,
                ok);
        }
        else
        {
            _store.ClearDiscovery();
        }
        return (ok, supplicant.ServerMac, interfaceName);
    }

    private string ResolveInterface(ServiceSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Interface))
        {
            return settings.Interface;
        }
        var devices = PcapNative.ListDevices();
        var cached = _store.LoadDiscovery();
        if (cached is not null &&
            devices.Any(item =>
                item.Name.Equals(cached.Interface, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogInformation(
                "使用缓存认证通道: {Interface} -> {Server}",
                cached.Description,
                cached.ServerMac);
            return cached.Interface;
        }
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
        _guardianFailures = 0;
        _nextGuardianCheck = DateTimeOffset.MinValue;
        _lastGuardianDecision = null;
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

    private async Task<bool> OnlineAsync(
        ServiceSettings settings,
        CancellationToken stoppingToken)
    {
        if (_guardian is not null)
        {
            var now = DateTimeOffset.Now;
            if (now >= _nextGuardianCheck)
            {
                _nextGuardianCheck = now.AddSeconds(Math.Max(1, settings.GuardianInterval));
                try
                {
                    var probe = await _guardian.ProbeAsync(settings.Interface, stoppingToken);
                    if (probe.Online is bool online)
                    {
                        if (online && !probe.CaptivePortal)
                        {
                            _guardianFailures = 0;
                            _lastGuardianDecision = true;
                            logger.LogInformation("网络管家: {Status}", probe.Short());
                            return true;
                        }
                        _guardianFailures++;
                        logger.LogWarning(
                            "网络管家: {Status} | 连续异常 {Count}/{Threshold}",
                            probe.Short(),
                            _guardianFailures,
                            Math.Max(1, settings.GuardianFailures));
                        if (_guardianFailures >= Math.Max(1, settings.GuardianFailures))
                        {
                            _lastGuardianDecision = false;
                            return false;
                        }
                        _lastGuardianDecision = true;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("网络管家不可用，回退本机探测: {Error}", ex.Message);
                }
            }
            else if (_lastGuardianDecision is { } cached)
            {
                return cached;
            }
        }
        return await TcpOnlineAsync(settings.ConnectivityUrl, stoppingToken);
    }

    private static async Task<bool> TcpOnlineAsync(
        string connectivityUrl,
        CancellationToken stoppingToken)
    {
        var url = connectivityUrl.Trim();
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
