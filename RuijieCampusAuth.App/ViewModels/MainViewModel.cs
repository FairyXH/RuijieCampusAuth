using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuijieCampusAuth.App.Services;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.App.ViewModels;

/// <summary>
/// Seam over the service installer so every service button can be exercised
/// in automated tests without touching the real SCM.
/// </summary>
public interface IServiceBridge
{
    ServiceState Query();
    void Install();
    void Uninstall();
    void Start();
    void Stop();
}

public sealed class SystemServiceBridge : IServiceBridge
{
    public ServiceState Query() => ServiceInstaller.Query();
    public void Install() => ServiceInstaller.Install();
    public void Uninstall() => ServiceInstaller.Uninstall();
    public void Start() => ServiceInstaller.Start();
    public void Stop() => ServiceInstaller.Stop();
}

public partial class MainViewModel : ViewModelBase
{
    private readonly UserStore _store;
    private readonly IServiceBridge _service;
    private readonly Func<List<PcapDeviceInfo>> _listInterfaces;
    private static readonly TimeSpan PausedProbeInterval = TimeSpan.FromSeconds(30);
    private const long GuiLogMaxBytes = 5 * 1024 * 1024;
    private ServiceLogTailer? _tailer;
    private readonly Action<string>? _openPath;
    private CancellationTokenSource? _cancellation;
    private bool _applyingSettings;
    private Supplicant? _activeSupplicant;
    private GuardianClient? _guardian;
    private int _guardianFailureCount;
    private DateTimeOffset _nextGuardianCheck = DateTimeOffset.MinValue;
    private bool? _lastGuardianDecision;
    private readonly StringBuilder _pendingServiceLines = new();
    private readonly object _logGate = new();

    [ObservableProperty] private UserAccount? _selectedAccount;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _serviceName = "";
    [ObservableProperty] private string _staticId = "";
    [ObservableProperty] private double _timeout = 20;
    [ObservableProperty] private int _retries = 3;
    [ObservableProperty] private int _repeats = 3;
    [ObservableProperty] private double _startTimeout = 3;
    [ObservableProperty] private double _checkInterval = 2;
    [ObservableProperty] private double _retryInterval = 3;
    [ObservableProperty] private string _connectivityUrl = "http://www.baidu.com/";
    [ObservableProperty] private DhcpOption? _selectedDhcpMode;
    [ObservableProperty] private double _dhcpTimeout = 30;
    [ObservableProperty] private bool _dhcpOption82;
    [ObservableProperty] private string _schoolCode = "";
    [ObservableProperty] private bool _serverLock;
    [ObservableProperty] private string _lockedServer = "";
    [ObservableProperty] private ServerRecord? _selectedServer;
    [ObservableProperty] private string _serverStatus = "服务器列表";
    [ObservableProperty] private bool _preempt = true;
    [ObservableProperty] private bool _takeoverEnabled;
    [ObservableProperty] private bool _guardianEnabled;
    [ObservableProperty] private string _guardianUrl = GuardianClient.DefaultUrl;
    [ObservableProperty] private double _guardianInterval = 5;
    [ObservableProperty] private int _guardianFailures = 2;
    [ObservableProperty] private PcapDeviceInfo? _selectedInterface;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private string _profileNameLabel = "配置档案: 未导入";
    [ObservableProperty] private ProfileOption? _selectedProfile;
    [ObservableProperty] private string _profileStatus = "未发现配置档案";
    [ObservableProperty] private string _serviceStatus = "未检测";
    [ObservableProperty] private string _discoveryStatus = "自动探测";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _verboseLog;

    public ObservableCollection<UserAccount> Accounts { get; } = [];
    public ObservableCollection<PcapDeviceInfo> Interfaces { get; } = [];
    public ObservableCollection<ServerRecord> Servers { get; } = [];
    public ObservableCollection<ProfileOption> AvailableProfiles { get; } = [];
    public DebugViewModel Debug { get; } = new();

    public IReadOnlyList<DhcpOption> DhcpModes { get; } =
    [
        new("before", "认证前获取"),
        new("after", "认证后获取"),
        new("none", "不使用"),
    ];

    public MainViewModel() : this(new SystemServiceBridge(), null, null)
    {
    }

    public MainViewModel(
        IServiceBridge serviceBridge,
        Func<List<PcapDeviceInfo>>? listInterfaces,
        Action<string>? openPath = null)
    {
        _service = serviceBridge;
        _listInterfaces = listInterfaces ?? (() => PcapNative.ListDevices());
        _openPath = openPath;
        _store = new UserStore();
        foreach (var device in _listInterfaces())
        {
            Interfaces.Add(device);
        }
        var settings = _store.LoadSettings();
        ApplySettings(settings);
        foreach (var account in _store.LoadAccounts())
        {
            Accounts.Add(account);
        }
        SelectedAccount = Accounts.FirstOrDefault(
            account => account.Id == settings.ActiveAccountId)
            ?? Accounts.FirstOrDefault(account => account.Enabled)
            ?? Accounts.FirstOrDefault();
        if (SelectedAccount is null)
        {
            SelectedInterface = Interfaces.FirstOrDefault();
        }
        AppendLog($"账号池已加载: {Accounts.Count} 个账号");
        RefreshProfiles();
        LoadServerList();
        RefreshService();
        StartServiceLogTail();
    }

    /// <summary>
    /// Streams the service log into the shared log view; the service writes
    /// with AutoFlush, so lines appear the moment the service emits them.
    /// </summary>
    private void StartServiceLogTail()
    {
        try
        {
            var path = Path.Combine(_store.BaseDirectory, "logs", "service.log");
            _tailer = new ServiceLogTailer(path);
            _tailer.LineAppended += OnServiceLogLine;
            _tailer.Start();
        }
        catch (Exception)
        {
            // Log tailing is best-effort; auth must not depend on it.
            _tailer?.Dispose();
            _tailer = null;
        }
    }

    private void OnServiceLogLine(string rawLine)
    {
        var line = FormatServiceLine(rawLine);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        Dispatcher.UIThread.Post(() => AppendServiceLine(line));
    }

    private static string FormatServiceLine(string rawLine)
    {
        // "2026-11-19 21:51:00 [Information] Category: message"
        var marker = rawLine.IndexOf("]: ", StringComparison.Ordinal);
        if (marker > 0 && rawLine.Length > marker + 3)
        {
            var message = rawLine[(marker + 3)..];
            var timeEnd = rawLine.IndexOf(' ');
            var secondSpace = timeEnd >= 0
                ? rawLine.IndexOf(' ', timeEnd + 1)
                : -1;
            var time = secondSpace > 0 ? rawLine[(timeEnd + 1)..secondSpace] : "";
            return string.IsNullOrWhiteSpace(time)
                ? $"[服务] {message}"
                : $"[服务 {time}] {message}";
        }
        return $"[服务] {rawLine}";
    }

    private static bool IsRoutineLine(string line)
    {
        // Hidden unless 详细信息 is on: the guardian heartbeat repeats every
        // few seconds and drowns out everything else.
        return line.Contains("网络管家:");
    }

    partial void OnVerboseLogChanged(bool value) => RebuildLogView();

    private readonly List<string> _logHistory = [];

    private void AppendToHistory(string line)
    {
        lock (_logGate)
        {
            _logHistory.Add(line);
            if (_logHistory.Count > 4000)
            {
                _logHistory.RemoveRange(0, _logHistory.Count - 2000);
            }
        }
    }

    private void RebuildLogView()
    {
        List<string> snapshot;
        lock (_logGate)
        {
            snapshot = [.. _logHistory];
        }
        var visible = VerboseLog
            ? snapshot
            : snapshot.Where(line => !IsRoutineLine(line));
        var text = string.Join(Environment.NewLine, visible);
        if (text.Length > 200_000)
        {
            text = text[^100_000..];
        }
        LogText = text;
    }

    [RelayCommand]
    private void ClearLog()
    {
        lock (_logGate)
        {
            _logHistory.Clear();
        }
        LogText = "";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.Combine(_store.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            if (_openPath is not null)
            {
                _openPath(dir);
            }
            else
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Status = $"打开日志目录失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    [RelayCommand]
    private void RefreshProfiles()
    {
        var settings = _store.LoadSettings();
        AvailableProfiles.Clear();
        foreach (var path in ProfileStore.Discover())
        {
            try
            {
                var profile = ProfileStore.Load(path);
                AvailableProfiles.Add(new ProfileOption(
                    path,
                    profile.Name,
                    profile.AuthMode,
                    profile.ServiceName));
            }
            catch
            {
                // Ignore malformed profile files in the picker.
            }
        }
        SelectedProfile = AvailableProfiles.FirstOrDefault(item =>
                              string.Equals(
                                  item.Path,
                                  settings.ProfilePath,
                                  StringComparison.OrdinalIgnoreCase))
                          ?? AvailableProfiles.FirstOrDefault();
        ProfileStatus = AvailableProfiles.Count == 0
            ? "未发现档案：把 JSON 放到程序目录 profiles/ 或当前目录 profiles/"
            : $"发现 {AvailableProfiles.Count} 个档案；当前: {settings.ProfileName ?? "(未导入)"}";
    }

    [RelayCommand]
    private void ImportProfile()
    {
        if (SelectedProfile is null)
        {
            ProfileStatus = "请先选择档案";
            return;
        }
        try
        {
            var profile = ProfileStore.Load(SelectedProfile.Path);
            var result = ProfileStore.Import(profile, _store, SelectedProfile.Path);
            ApplySettings(_store.LoadSettings());
            ProfileStatus = $"已切换到 {result.ProfileName}；备份: {result.BackupPath ?? "(none)"}";
            AppendLog($"已导入配置档案: {result.ProfileName} ({SelectedProfile.Path})");
            RefreshService();
        }
        catch (Exception ex)
        {
            ProfileStatus = $"导入失败: {ex.Message}";
        }
    }

    partial void OnSelectedAccountChanged(UserAccount? value)
    {
        if (value is null)
        {
            return;
        }
        Username = value.Username;
        Password = value.Password;
        Note = value.Note;
        Status = value.Enabled ? $"当前账号: {value.Username}" : $"账号已停用: {value.Username}";
    }

    partial void OnSelectedInterfaceChanged(PcapDeviceInfo? value)
    {
        if (value is not null)
        {
            AppendLog($"选择网卡: {value.Description}");
        }
    }

    partial void OnTakeoverEnabledChanged(bool value)
    {
        if (_applyingSettings)
        {
            return;
        }
        try
        {
            var settings = BuildSettings();
            _store.SaveSettings(settings);
            if (File.Exists(ServiceConfig.ConfigPath))
            {
                ServiceConfig.SetTakeover(ServiceConfig.ConfigPath, value);
            }
            Status = value ? "接管模式已开启并保存" : "接管模式已关闭并保存";
            AppendLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"保存接管模式失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    private void ApplySettings(UserSettings settings)
    {
        ServiceName = settings.ServiceName;
        StaticId = settings.StaticId;
        Timeout = settings.Timeout;
        Retries = settings.Retries;
        Repeats = settings.Repeats;
        StartTimeout = settings.StartTimeout;
        CheckInterval = settings.CheckInterval;
        RetryInterval = settings.RetryInterval;
        ConnectivityUrl = settings.ConnectivityUrl;
        SelectedDhcpMode = DhcpModes.FirstOrDefault(item =>
            item.Value.Equals(settings.DhcpMode, StringComparison.OrdinalIgnoreCase))
            ?? DhcpModes[0];
        DhcpTimeout = settings.DhcpTimeout;
        DhcpOption82 = settings.DhcpOption82;
        SchoolCode = settings.SchoolCode;
        ProfileNameLabel = string.IsNullOrWhiteSpace(settings.ProfileName)
            ? "配置档案: 未导入"
            : $"配置档案: {settings.ProfileName}";
        ServerLock = settings.ServerLock;
        LockedServer = settings.LockedServer;
        Preempt = settings.Preempt;
        _applyingSettings = true;
        try
        {
            var takeover = settings.TakeoverEnabled;
            try
            {
                if (File.Exists(ServiceConfig.ConfigPath))
                {
                    takeover = ServiceConfig.ReadTakeoverEnabled(ServiceConfig.ConfigPath);
                }
            }
            catch
            {
                // Keep the user setting when service.json is unreadable.
            }
            TakeoverEnabled = takeover;
        }
        finally
        {
            _applyingSettings = false;
        }
        GuardianEnabled = settings.GuardianEnabled;
        GuardianUrl = settings.GuardianUrl;
        GuardianInterval = settings.GuardianInterval;
        GuardianFailures = settings.GuardianFailures;

        var discovery = _store.LoadDiscovery();
        if (!string.IsNullOrWhiteSpace(settings.Interface))
        {
            SelectedInterface = Interfaces.FirstOrDefault(item =>
                item.Name.Equals(settings.Interface, StringComparison.OrdinalIgnoreCase))
                ?? Interfaces.FirstOrDefault(item =>
                    item.Description.Contains(settings.Interface, StringComparison.OrdinalIgnoreCase))
                ?? Interfaces.FirstOrDefault();
            DiscoveryStatus = discovery is null
                ? "指定网卡（不使用自动发现缓存）"
                : $"指定网卡；缓存通道: {discovery.Description} → {discovery.ServerMac}";
        }
        else if (discovery is not null &&
                 Interfaces.FirstOrDefault(item =>
                     item.Name.Equals(discovery.Interface, StringComparison.OrdinalIgnoreCase)) is { } cached)
        {
            SelectedInterface = cached;
            DiscoveryStatus = $"已缓存通道: {discovery.Description} → {discovery.ServerMac}";
        }
        else
        {
            SelectedInterface = Interfaces.FirstOrDefault(item =>
                item.Description.Contains("USB GbE", StringComparison.OrdinalIgnoreCase))
                ?? Interfaces.FirstOrDefault(item =>
                    item.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
                ?? Interfaces.FirstOrDefault();
            DiscoveryStatus = "自动探测";
        }
    }

    [RelayCommand]
    private void NewAccount()
    {
        SelectedAccount = null;
        Username = "";
        Password = "";
        Note = "";
        Status = "已新建空表单，填写后点击 添加";
    }

    [RelayCommand]
    private void AddAccount()
    {
        var account = new UserAccount
        {
            Username = Username.Trim(),
            Password = Password,
            Note = Note.Trim(),
            Enabled = true,
        };
        if (string.IsNullOrWhiteSpace(account.Username))
        {
            Status = "用户名不能为空";
            return;
        }
        if (Accounts.Any(item =>
                item.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"账号已存在: {account.Username}";
            return;
        }
        Accounts.Add(account);
        SelectedAccount = account;
        PersistAccounts(account.Id);
        Status = "账号已添加";
        AppendLog(Status);
    }

    [RelayCommand]
    private void UpdateAccount()
    {
        if (SelectedAccount is null)
        {
            Status = "请先选择账号";
            return;
        }
        SelectedAccount.Username = Username.Trim();
        SelectedAccount.Password = Password;
        SelectedAccount.Note = Note.Trim();
        PersistAccounts(SelectedAccount.Id);
        Status = "账号已更新";
        AppendLog(Status);
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        if (SelectedAccount is null)
        {
            Status = "请先选择账号";
            return;
        }
        var removed = SelectedAccount.Username;
        Accounts.Remove(SelectedAccount);
        SelectedAccount = Accounts.FirstOrDefault();
        PersistAccounts(SelectedAccount?.Id);
        Status = $"账号已删除: {removed}";
        AppendLog(Status);
    }

    [RelayCommand]
    private void UseAccount()
    {
        if (SelectedAccount is null)
        {
            Status = "请先选择账号";
            return;
        }
        PersistAccounts(SelectedAccount.Id);
        Status = $"当前账号: {SelectedAccount.Username}";
        AppendLog(Status);
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedAccount is not null)
        {
            SelectedAccount.Username = Username.Trim();
            SelectedAccount.Password = Password;
            SelectedAccount.Note = Note.Trim();
            _store.SaveAccounts(Accounts);
        }
        var settings = BuildSettings();
        settings.ActiveAccountId = SelectedAccount?.Id;
        _store.SaveSettings(settings);
        try
        {
            if (File.Exists(ServiceConfig.ConfigPath))
            {
                ServiceConfig.SetTakeover(ServiceConfig.ConfigPath, settings.TakeoverEnabled);
            }
        }
        catch
        {
            // The service may be installed with a protected config.
        }
        Status = "配置已保存";
        AppendLog("配置已保存");
    }

    [RelayCommand]
    private void ForceRediscover()
    {
        _store.ClearDiscovery();
        DiscoveryStatus = "已清除发现缓存，下次认证重新探测";
        AppendLog(DiscoveryStatus);
        if (IsBusy)
        {
            Stop();
        }
    }

    private void LoadServerList()
    {
        var records = _store.LoadServers()
            .OrderByDescending(item => item.SuccessCount)
            .ThenByDescending(item => item.LastSuccess)
            .ToList();
        Servers.Clear();
        foreach (var record in records)
        {
            Servers.Add(record);
        }
        if (SelectedServer is not null && !Servers.Contains(SelectedServer))
        {
            SelectedServer = null;
        }
        SelectedServer ??= Servers.FirstOrDefault();
        if (Servers.Count == 0)
        {
            ServerStatus = "服务器列表为空，认证成功后自动记录";
        }
        else
        {
            ServerStatus =
                $"已记录 {Servers.Count} 个认证服务器" +
                (ServerLock && !string.IsNullOrWhiteSpace(LockedServer)
                    ? $"；当前锁定 {LockedServer}"
                    : "");
        }
    }

    [RelayCommand]
    private void RefreshServers()
    {
        LoadServerList();
        AppendLog(ServerStatus);
    }

    [RelayCommand]
    private void LockSelectedServer()
    {
        if (SelectedServer is null)
        {
            Status = "请先选择服务器";
            return;
        }
        ServerLock = true;
        LockedServer = SelectedServer.Mac;
        Save();
        Status = $"已锁定服务器 {SelectedServer.Mac}";
        ServerStatus = Status;
        AppendLog(Status);
    }

    [RelayCommand]
    private void UnlockServer()
    {
        ServerLock = false;
        Save();
        Status = "已取消服务器锁定";
        ServerStatus = Status;
        AppendLog(Status);
    }

    [RelayCommand]
    private void DeleteServer()
    {
        if (SelectedServer is null)
        {
            Status = "请先选择服务器";
            return;
        }
        var mac = SelectedServer.Mac;
        var records = _store.LoadServers();
        records.RemoveAll(item =>
            item.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        _store.SaveServers(records);
        if (LockedServer.Equals(mac, StringComparison.OrdinalIgnoreCase))
        {
            ServerLock = false;
            LockedServer = "";
            Save();
        }
        SelectedServer = null;
        Status = $"已删除服务器记录 {mac}";
        AppendLog(Status);
        LoadServerList();
    }

    [RelayCommand]
    private void ToggleAccountEnabled()
    {
        if (SelectedAccount is null)
        {
            Status = "请先选择账号";
            return;
        }
        SelectedAccount.Enabled = !SelectedAccount.Enabled;
        PersistAccounts(SelectedAccount.Id);
        Status = SelectedAccount.Enabled
            ? $"账号已启用: {SelectedAccount.Username}"
            : $"账号已停用: {SelectedAccount.Username}";
        AppendLog(Status);
    }

    private void PersistAccounts(string? activeId)
    {
        _store.SaveAccounts(Accounts);
        var settings = _store.LoadSettings();
        settings.ActiveAccountId = activeId;
        _store.SaveSettings(settings);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }
        if (_service.Query().Running)
        {
            Status = "系统服务正在托管认证，请先停止服务";
            AppendLog(Status);
            return;
        }
        if (SelectedAccount is null ||
            string.IsNullOrWhiteSpace(SelectedAccount.Username) ||
            string.IsNullOrWhiteSpace(SelectedAccount.Password))
        {
            Status = "请先选择或添加一个带密码的账号";
            AppendLog(Status);
            return;
        }
        if (SelectedInterface is null)
        {
            Status = "请选择网卡";
            AppendLog(Status);
            return;
        }
        Save();
        IsBusy = true;
        Status = "正在认证...";
        _cancellation = new CancellationTokenSource();
        try
        {
            var settings = _store.LoadSettings();
            _guardian = settings.GuardianEnabled
                ? new GuardianClient(settings.GuardianUrl)
                : null;
            _guardianFailureCount = 0;
            _nextGuardianCheck = DateTimeOffset.MinValue;
            _lastGuardianDecision = null;
            while (!_cancellation.IsCancellationRequested)
            {
                settings = _store.LoadSettings();
                using var supplicant = new Supplicant(
                    SelectedInterface.Name,
                    SelectedAccount.Username,
                    SelectedAccount.Password,
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
                bool ok;
                try
                {
                    ok = await Task.Run(
                        supplicant.Authenticate,
                        _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _activeSupplicant = null;
                    Status = $"认证异常: {ex.Message}";
                    AppendLog(Status);
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, settings.RetryInterval)),
                        _cancellation.Token);
                    continue;
                }
                _activeSupplicant = null;
                if (supplicant.Connected)
                {
                    _store.SaveDiscovery(
                        SelectedInterface.Name,
                        SelectedInterface.Description,
                        supplicant.SourceMac,
                        supplicant.ServerMac ?? "");
                    _store.UpsertServer(
                        supplicant.ServerMac ?? "",
                        SelectedInterface.Name,
                        SelectedInterface.Description,
                        ok);
                    LoadServerList();
                }
                else
                {
                    _store.ClearDiscovery();
                }
                if (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                if (!ok)
                {
                    if (!supplicant.Connected &&
                        await OnlineAsync(settings, _cancellation.Token))
                    {
                        await PauseForHomeNetworkAsync(settings, _cancellation.Token);
                        continue;
                    }
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
                    if (!await OnlineAsync(settings, _cancellation.Token))
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
            _guardian?.Dispose();
            _guardian = null;
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
        var state = _service.Query();
        var detail = state.Detail;
        var status = RuijieCampusAuth.Core.ServiceStatus.Read();
        if (status is not null)
        {
            detail += $" | {status.State}: {status.Message}";
        }
        ServiceStatus = detail;
    }

    [RelayCommand]
    private void ApplyServiceConfig()
    {
        try
        {
            Save();
            var settings = _store.LoadSettings();
            var account = SelectedAccount ?? _store.ActiveAccount();
            if (account is null)
            {
                Status = "请先添加账号";
                return;
            }
            ServiceConfig.Save(
                ServiceConfig.ConfigPath,
                account.Username,
                account.Password,
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
    private void InstallService()
    {
        try
        {
            ApplyServiceConfig();
            _service.Install();
            if (OperatingSystem.IsWindows())
            {
                _service.Start();
                AppendLog("系统服务已安装并启动");
            }
            else
            {
                AppendLog(
                    "systemd unit 已写入；请执行 systemctl daemon-reload && systemctl enable --now ruijie-campus-auth.service");
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
    private void UninstallService()
    {
        try
        {
            _service.Uninstall();
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
            _service.Start();
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
            _service.Stop();
            RefreshService();
            AppendLog("系统服务已停止");
        }
        catch (Exception ex)
        {
            Status = $"停止服务失败: {ex.Message}";
            AppendLog(Status);
        }
    }

    private UserSettings BuildSettings()
    {
        var settings = _store.LoadSettings();
        settings.ActiveAccountId = SelectedAccount?.Id;
        settings.Interface = SelectedInterface?.Name ?? "";
        settings.ServiceName = ServiceName.Trim();
        settings.StaticId = StaticId.Trim();
        settings.Timeout = Timeout;
        settings.Retries = Retries;
        settings.Repeats = Repeats;
        settings.StartTimeout = StartTimeout;
        settings.CheckInterval = CheckInterval;
        settings.RetryInterval = RetryInterval;
        settings.ConnectivityUrl = ConnectivityUrl;
        settings.DhcpMode = SelectedDhcpMode?.Value ?? "before";
        settings.DhcpTimeout = DhcpTimeout;
        settings.DhcpOption82 = DhcpOption82;
        settings.SchoolCode = SchoolCode.Trim();
        settings.ServerLock = ServerLock;
        settings.LockedServer = LockedServer.Trim();
        settings.Preempt = Preempt;
        settings.TakeoverEnabled = TakeoverEnabled;
        settings.GuardianEnabled = GuardianEnabled;
        settings.GuardianUrl = GuardianUrl;
        settings.GuardianInterval = GuardianInterval;
        settings.GuardianFailures = GuardianFailures;
        return settings;
    }

    private static ServiceSettings ToServiceSettings(UserSettings settings)
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
            DhcpMode = settings.DhcpMode,
            DhcpTimeout = settings.DhcpTimeout,
            DhcpOption82 = settings.DhcpOption82,
            SchoolCode = settings.SchoolCode,
            ProfileName = settings.ProfileName,
            AuthMode = settings.AuthMode,
            SelfServiceUrl = settings.SelfServiceUrl,
            UpdateUrl = settings.UpdateUrl,
            PortalUrl = settings.PortalUrl,
            SamHost = settings.SamHost,
            SamPort = settings.SamPort,
            AdDomain = settings.AdDomain,
            AdTimeout = settings.AdTimeout,
            EngineVersion = settings.EngineVersion,
            UseServerReauth = settings.UseServerReauth,
            GuardianEnabled = settings.GuardianEnabled,
            GuardianUrl = settings.GuardianUrl,
            GuardianInterval = settings.GuardianInterval,
            GuardianFailures = settings.GuardianFailures,
        };
    }

    private async Task PauseForHomeNetworkAsync(
        UserSettings settings,
        CancellationToken token)
    {
        Status = "未检测到认证服务器，当前网络在线，暂停认证";
        AppendLog(Status);
        _guardianFailureCount = 0;
        _nextGuardianCheck = DateTimeOffset.MinValue;
        _lastGuardianDecision = null;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(PausedProbeInterval, token);
            if (!await OnlineAsync(settings, token))
            {
                Status = "网络已离线，恢复认证";
                AppendLog(Status);
                return;
            }
        }
    }

    private async Task<bool> OnlineAsync(UserSettings settings, CancellationToken token)
    {
        if (_guardian is not null)
        {
            var now = DateTimeOffset.Now;
            if (now >= _nextGuardianCheck)
            {
                _nextGuardianCheck = now.AddSeconds(Math.Max(1, settings.GuardianInterval));
                try
                {
                    var probe = await _guardian.ProbeAsync(SelectedInterface?.Name, token);
                    if (probe.Online is bool online)
                    {
                        if (online && !probe.CaptivePortal)
                        {
                            _guardianFailureCount = 0;
                            _lastGuardianDecision = true;
                            return true;
                        }
                        _guardianFailureCount++;
                        AppendLog(
                            $"网络管家: {probe.Short()} | 连续异常 {_guardianFailureCount}/{Math.Max(1, settings.GuardianFailures)}");
                        if (_guardianFailureCount >= Math.Max(1, settings.GuardianFailures))
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
                    AppendLog($"网络管家不可用，回退本机探测: {ex.Message}");
                }
            }
            else if (_lastGuardianDecision is { } cached)
            {
                return cached;
            }
        }
        return await TcpOnlineAsync(settings.ConnectivityUrl, token);
    }

    private static async Task<bool> TcpOnlineAsync(string url, CancellationToken token)
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
        AppendToHistory(line);
        try
        {
            var logDir = Path.Combine(_store.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "csharp-gui.log");
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > GuiLogMaxBytes)
            {
                var backup = logPath + ".1";
                File.Delete(backup);
                File.Move(logPath, backup);
            }
            File.AppendAllText(
                logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // File logging is diagnostic only.
        }
        var snapshot = VerboseLog || !IsRoutineLine(line);
        if (!snapshot)
        {
            Dispatcher.UIThread.Post(RebuildLogView);
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            // Commands run on the UI thread: update immediately so the log
            // reflects each action with zero delay.
            LogText += line + Environment.NewLine;
            if (LogText.Length > 200_000)
            {
                LogText = LogText[^100_000..];
            }
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            LogText += line + Environment.NewLine;
            if (LogText.Length > 200_000)
            {
                LogText = LogText[^100_000..];
            }
        });
    }

    private void AppendServiceLine(string line)
    {
        AppendToHistory(line);
        if (!VerboseLog && IsRoutineLine(line))
        {
            return;
        }
        LogText += line + Environment.NewLine;
        if (LogText.Length > 200_000)
        {
            LogText = LogText[^100_000..];
        }
    }
}
