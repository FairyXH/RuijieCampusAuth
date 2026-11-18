using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private CancellationTokenSource? _cancellation;
    private Supplicant? _activeSupplicant;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _serviceName = "电信宽带服务";
    [ObservableProperty] private string _staticId = "Static:AB45A862";
    [ObservableProperty] private double _timeout = 20;
    [ObservableProperty] private int _retries = 4;
    [ObservableProperty] private int _repeats = 3;
    [ObservableProperty] private double _startTimeout = 3;
    [ObservableProperty] private double _checkInterval = 2;
    [ObservableProperty] private double _retryInterval = 3;
    [ObservableProperty] private string _connectivityUrl = "http://www.baidu.com/";
    [ObservableProperty] private bool _serverLock;
    [ObservableProperty] private string _lockedServer = "";
    [ObservableProperty] private bool _preempt = true;
    [ObservableProperty] private bool _takeoverEnabled;
    [ObservableProperty] private PcapDeviceInfo? _selectedInterface;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private string _serviceStatus = "未检测";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<PcapDeviceInfo> Interfaces { get; } = [];

    public MainViewModel()
    {
        foreach (var device in PcapNative.ListDevices())
        {
            Interfaces.Add(device);
        }
        var settings = GuiSettingsStore.Load();
        Username = settings.Username;
        Password = settings.Password;
        ServiceName = settings.ServiceName;
        StaticId = settings.StaticId;
        Timeout = settings.Timeout;
        Retries = settings.Retries;
        Repeats = settings.Repeats;
        StartTimeout = settings.StartTimeout;
        CheckInterval = settings.CheckInterval;
        RetryInterval = settings.RetryInterval;
        ConnectivityUrl = settings.ConnectivityUrl;
        ServerLock = settings.ServerLock;
        LockedServer = settings.LockedServer;
        Preempt = settings.Preempt;
        TakeoverEnabled = settings.TakeoverEnabled;
        SelectedInterface = Interfaces.FirstOrDefault(item =>
            item.Name.Equals(settings.Interface, StringComparison.OrdinalIgnoreCase))
            ?? Interfaces.FirstOrDefault(item =>
                item.Description.Contains(settings.Interface, StringComparison.OrdinalIgnoreCase))
            ?? Interfaces.FirstOrDefault();
        RefreshService();
    }

    partial void OnSelectedInterfaceChanged(PcapDeviceInfo? value)
    {
        if (value is not null)
        {
            AppendLog($"选择网卡: {value.Description}");
        }
    }

    [RelayCommand]
    private void Save()
    {
        GuiSettingsStore.Save(BuildSettings());
        Status = "配置已保存";
        AppendLog("配置已保存");
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }
        if (ServiceInstaller.Query().Running)
        {
            Status = "系统服务正在托管认证，请先停止服务";
            return;
        }
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            Status = "请填写用户名和密码";
            return;
        }
        if (SelectedInterface is null)
        {
            Status = "请选择网卡";
            return;
        }
        Save();
        IsBusy = true;
        Status = "正在认证...";
        _cancellation = new CancellationTokenSource();
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var settings = GuiSettingsStore.Load();
                using var supplicant = new Supplicant(
                    SelectedInterface.Name,
                    Username,
                    Password,
                    timeout: settings.Timeout,
                    retries: settings.Retries,
                    repeats: settings.Repeats,
                    startTimeout: settings.StartTimeout,
                    lockedServer: settings.ServerLock ? settings.LockedServer : null,
                    serviceName: settings.ServiceName,
                    staticId: settings.StaticId,
                    log: AppendLog,
                    error: message =>
                    {
                        AppendLog(message);
                        Status = message;
                    });
                _activeSupplicant = supplicant;
                var ok = await Task.Run(supplicant.Authenticate, _cancellation.Token);
                _activeSupplicant = null;
                if (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                if (!ok)
                {
                    Status = "认证失败，等待重试";
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, settings.RetryInterval)),
                        _cancellation.Token);
                    continue;
                }
                Status = "认证成功";
                AppendLog("认证成功");
                if (!settings.Preempt)
                {
                    break;
                }
                while (!_cancellation.IsCancellationRequested)
                {
                    if (!await OnlineAsync(settings.ConnectivityUrl, _cancellation.Token))
                    {
                        Status = "检测到掉线，重新认证";
                        AppendLog("检测到掉线，重新认证");
                        break;
                    }
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(0.5, settings.CheckInterval)),
                        _cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            Status = $"认证异常: {ex.Message}";
            AppendLog(Status);
        }
        finally
        {
            _activeSupplicant = null;
            _cancellation?.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
        if (!IsBusy && Status == "正在认证...")
        {
            Status = "已停止";
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _activeSupplicant?.Stop();
        _cancellation?.Cancel();
        Status = "正在停止...";
    }

    [RelayCommand]
    private void RefreshService()
    {
        var state = ServiceInstaller.Query();
        var detail = state.Detail;
        var status = RuijieCampusAuth.Core.ServiceStatus.Read();
        if (status is not null)
        {
            detail += $" | {status.State}: {status.Message}";
        }
        ServiceStatus = detail;
    }

    [RelayCommand]
    private void InstallService()
    {
        try
        {
            Save();
            var settings = GuiSettingsStore.Load();
            ServiceConfig.Save(
                ServiceConfig.ConfigPath,
                settings.Username,
                settings.Password,
                ToServiceSettings(settings));
            ServiceInstaller.Install();
            if (OperatingSystem.IsWindows())
            {
                ServiceInstaller.Start();
                AppendLog("系统服务已安装并启动");
            }
            else
            {
                AppendLog(
                    "systemd unit 已写入；请执行 systemctl daemon-reload && systemctl enable --now hxxy-campus-auth.service");
            }
            RefreshService();
        }
        catch (Exception ex)
        {
            Status = $"安装服务失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    [RelayCommand]
    private void ApplyServiceConfig()
    {
        try
        {
            Save();
            var settings = GuiSettingsStore.Load();
            ServiceConfig.Save(
                ServiceConfig.ConfigPath,
                settings.Username,
                settings.Password,
                ToServiceSettings(settings));
            RefreshService();
            Status = TakeoverEnabled
                ? "服务配置已更新，接管模式将临时阻止原版客户端"
                : "服务配置已更新";
            AppendLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"更新服务配置失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    [RelayCommand]
    private void UninstallService()
    {
        try
        {
            ServiceInstaller.Uninstall();
            RefreshService();
            AppendLog("系统服务已卸载");
        }
        catch (Exception ex)
        {
            Status = $"卸载服务失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    [RelayCommand]
    private void StartService()
    {
        try
        {
            ServiceInstaller.Start();
            RefreshService();
            AppendLog("系统服务已启动");
        }
        catch (Exception ex)
        {
            Status = $"启动服务失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    [RelayCommand]
    private void StopService()
    {
        try
        {
            ServiceInstaller.Stop();
            RefreshService();
            AppendLog("系统服务已停止");
        }
        catch (Exception ex)
        {
            Status = $"停止服务失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    private GuiSettings BuildSettings()
    {
        return new GuiSettings
        {
            Username = Username.Trim(),
            Password = Password,
            Interface = SelectedInterface?.Name ?? "",
            ServiceName = string.IsNullOrWhiteSpace(ServiceName) ? "电信宽带服务" : ServiceName.Trim(),
            StaticId = string.IsNullOrWhiteSpace(StaticId) ? "Static:AB45A862" : StaticId.Trim(),
            Timeout = Timeout,
            Retries = Retries,
            Repeats = Repeats,
            StartTimeout = StartTimeout,
            CheckInterval = CheckInterval,
            RetryInterval = RetryInterval,
            ConnectivityUrl = ConnectivityUrl,
            ServerLock = ServerLock,
            LockedServer = LockedServer.Trim(),
            Preempt = Preempt,
            TakeoverEnabled = TakeoverEnabled,
        };
    }

    private static ServiceSettings ToServiceSettings(GuiSettings settings)
    {
        return new ServiceSettings
        {
            Interface = settings.Interface,
            ServiceName = settings.ServiceName,
            StaticId = settings.StaticId,
            Timeout = settings.Timeout,
            Retries = settings.Retries,
            Repeats = settings.Repeats,
            StartTimeout = settings.StartTimeout,
            Preempt = settings.Preempt,
            CheckInterval = settings.CheckInterval,
            RetryInterval = settings.RetryInterval,
            ConnectivityUrl = settings.ConnectivityUrl,
            ServerLock = settings.ServerLock,
            LockedServer = settings.LockedServer,
            TakeoverEnabled = settings.TakeoverEnabled,
        };
    }

    private static async Task<bool> OnlineAsync(string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return true;
        }
        if (uri.Host is "localhost" or "127.0.0.1" or "::1")
        {
            return true;
        }
        var port = uri.Port > 0 ? uri.Port : uri.Scheme == "https" ? 443 : 80;
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(uri.Host, port, timeout.Token);
            return true;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Dispatcher.UIThread.Post(() =>
        {
            LogText += line + Environment.NewLine;
            if (LogText.Length > 200_000)
            {
                LogText = LogText[^100_000..];
            }
        });
    }
}
