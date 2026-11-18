using System.Net.Sockets;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Service;

public sealed class AuthWorker(ILogger<AuthWorker> logger) : BackgroundService
{
    private readonly UserStore _store = new(ServiceConfig.ServiceRoot);
    private static readonly TimeSpan PausedProbeInterval = TimeSpan.FromSeconds(30);
    private GuardianClient? _guardian;
    private string _guardianKey = "";
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

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = ResolveSettings(config);
            EnsureGuardian(settings);
            var accounts = ResolveAccounts(config);
            if (accounts.Count == 0)
            {
                ServiceStatus.Write("error", "没有可用账号");
                logger.LogError("accounts.json 和 service.json 都没有可用账号");
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, settings.RetryInterval)),
                    stoppingToken);
                continue;
            }

            var authenticated = false;
            foreach (var account in accounts)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                logger.LogInformation("尝试账号: {Account}", account.Username);
                (bool ok, bool connected, string? server, string interfaceName) result;
                try
                {
                    result = await Task.Run(
                        () => Authenticate(
                            settings,
                            account.Username,
                            account.Password,
                            stoppingToken),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "认证过程异常");
                    ServiceStatus.Write("error", $"认证异常: {ex.Message}");
                    result = (false, false, null, settings.Interface);
                }
                if (!result.ok)
                {
                    logger.LogWarning(
                        "账号 {Account} 认证失败，尝试下一个账号",
                        account.Username);
                    if (!result.connected &&
                        await OnlineAsync(settings, stoppingToken))
                    {
                        await PauseForHomeNetworkAsync(settings, stoppingToken);
                        break;
                    }
                    continue;
                }
                authenticated = true;
                ServiceStatus.Write(
                    "authenticated",
                    "认证成功",
                    result.interfaceName,
                    result.server);
                logger.LogInformation(
                    "认证成功: {Account} | {Interface} -> {Server}",
                    account.Username,
                    result.interfaceName,
                    result.server ?? "-");
                if (!settings.Preempt)
                {
                    ServiceStatus.Write("idle", "认证成功，服务保持运行");
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    break;
                }
                await HoldAsync(settings, stoppingToken);
                break;
            }
            if (!authenticated)
            {
                ServiceStatus.Write("retrying", "认证失败，等待重试");
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, settings.RetryInterval)),
                    stoppingToken);
            }
        }

        _guardian?.Dispose();
        _guardian = null;
        ServiceStatus.Write("stopped", "服务停止");
        logger.LogInformation("认证服务停止");
    }

    private async Task PauseForHomeNetworkAsync(
        ServiceSettings settings,
        CancellationToken stoppingToken)
    {
        ServiceStatus.Write(
            "paused",
            "未检测到认证服务器，当前网络在线，暂停认证");
        logger.LogWarning(
            "未检测到认证服务器，当前网络在线，暂停认证；每 {Interval} 秒复查",
            (int)PausedProbeInterval.TotalSeconds);
        _guardianFailures = 0;
        _nextGuardianCheck = DateTimeOffset.MinValue;
        _lastGuardianDecision = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PausedProbeInterval, stoppingToken);
            if (!await OnlineAsync(settings, stoppingToken))
            {
                ServiceStatus.Write("retrying", "网络已离线，恢复认证");
                logger.LogInformation("网络已离线，恢复自动认证");
                return;
            }
        }
    }

    private ServiceSettings ResolveSettings(ServiceConfig config)
    {
        try
        {
            if (File.Exists(_store.SettingsPath))
            {
                return ServiceSettings.FromUser(_store.LoadSettings());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取 settings.json 失败，回退 service.json");
        }
        return config.Settings;
    }

    private List<(string Username, string Password)> ResolveAccounts(ServiceConfig config)
    {
        var result = new List<(string Username, string Password)>();
        try
        {
            var activeId = _store.LoadSettings().ActiveAccountId;
            var accounts = _store.LoadAccounts();
            var active = accounts.FirstOrDefault(account =>
                account.Id == activeId &&
                account.Enabled &&
                !string.IsNullOrWhiteSpace(account.Username) &&
                !string.IsNullOrWhiteSpace(account.Password));
            if (active is not null)
            {
                result.Add((active.Username, active.Password));
            }
            foreach (var account in accounts.Where(account =>
                         account != active &&
                         account.Enabled &&
                         !string.IsNullOrWhiteSpace(account.Username) &&
                         !string.IsNullOrWhiteSpace(account.Password)))
            {
                result.Add((account.Username, account.Password));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取 accounts.json 失败，回退 service.json");
        }
        if (result.Count == 0 &&
            !string.IsNullOrWhiteSpace(config.Username) &&
            !string.IsNullOrWhiteSpace(config.Password))
        {
            result.Add((config.Username, config.Password));
            logger.LogWarning("accounts.json 无可用账号，回退 service.json 账号");
        }
        else if (result.Count > 0)
        {
            logger.LogInformation("账号池加载: {Count} 个可用账号", result.Count);
        }
        return result;
    }

    private void EnsureGuardian(ServiceSettings settings)
    {
        var key = settings.GuardianEnabled ? settings.GuardianUrl.Trim() : "";
        if (key.Equals(_guardianKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _guardian?.Dispose();
        _guardian = null;
        _guardianKey = key;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        try
        {
            _guardian = new GuardianClient(key);
            logger.LogInformation("网络管家联动已启用: {Url}", key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "网络管家地址无效，系统服务回退本机探测");
        }
    }

    private (bool ok, bool connected, string? server, string interfaceName) Authenticate(
        ServiceSettings settings,
        string username,
        string password,
        CancellationToken stoppingToken)
    {
        var interfaceName = ResolveInterface(settings);
        logger.LogInformation("使用网卡: {Interface}", interfaceName);
        ServiceStatus.Write("authenticating", "正在认证", interfaceName);
        var locked = settings.ServerLock &&
                     !string.IsNullOrWhiteSpace(settings.LockedServer)
            ? settings.LockedServer
            : null;
        using var supplicant = new Supplicant(
            interfaceName,
            username,
            password,
            timeout: settings.Timeout,
            retries: settings.Retries,
            repeats: settings.Repeats,
            startTimeout: settings.StartTimeout,
            lockedServer: locked,
            serviceName: settings.ServiceName,
            staticId: settings.StaticId,
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
        return (ok, supplicant.Connected, supplicant.ServerMac, interfaceName);
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

    private async Task HoldAsync(ServiceSettings settings, CancellationToken stoppingToken)
    {
        _guardianFailures = 0;
        _nextGuardianCheck = DateTimeOffset.MinValue;
        _lastGuardianDecision = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await OnlineAsync(settings, stoppingToken))
            {
                logger.LogWarning("检测到掉线，开始补认证");
                ServiceStatus.Write("offline", "检测到掉线，开始补认证");
                return;
            }
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(0.5, settings.CheckInterval)),
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
