using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using RuijieCampusAuth.App.Services;
using RuijieCampusAuth.App.ViewModels;
using RuijieCampusAuth.Core;
using Xunit;

namespace RuijieCampusAuth.App.Tests;

/// <summary>
/// Exercises every GUI button command end to end against a fake service
/// bridge. The real ServiceInstaller/CLI contract is untouched; this only
/// proves the view-model wiring behind each button.
/// </summary>
public sealed class MainViewModelTests : IDisposable
{
    private readonly FakeServiceBridge _bridge = new();
    private readonly List<string> _opened = [];
    private string _workDir = AppContext.BaseDirectory;

    public MainViewModelTests()
    {
        foreach (var name in new[] { "accounts.json", "settings.json", "discovery.json", "servers.json" })
        {
            var path = Path.Combine(_workDir, name);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(150);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(150);
                }
            }
        }
    }

    private MainViewModel CreateVm(bool serviceRunning = false)
    {
        _bridge.Running = serviceRunning;
        _workDir = Path.Combine(
            Path.GetTempPath(), "rj-guitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        return new MainViewModel(
            _bridge,
            () =>
            [
                new PcapDeviceInfo(
                    @"\Device\NPF_{TEST-GUID}",
                    "Test Adapter"),
            ],
            path => _opened.Add(path),
            new UserStore(_workDir));
    }

    private void SeedServer(string mac = "00:1a:a9:17:ff:ff")
    {
        new UserStore(_workDir).SaveServers(
        [
            new ServerRecord
            {
                Mac = mac,
                Interface = @"\Device\NPF_{TEST-GUID}",
                Description = "Test Adapter",
                SuccessCount = 1,
                LastSuccess = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            },
        ]);
    }

    // ---------- 账户页 ----------

    [AvaloniaFact]
    public void NewAccount_clears_form()
    {
        var vm = CreateVm();
        vm.Username = "a";
        vm.NewAccountCommand.Execute(null);
        Assert.Equal("", vm.Username);
        Assert.Equal("", vm.Password);
        Assert.Null(vm.SelectedAccount);
        Assert.Contains("已新建空表单", vm.Status);
    }

    [AvaloniaFact]
    public void AddAccount_adds_and_persists()
    {
        var vm = CreateVm();
        vm.Username = "student01";
        vm.Password = "pw";
        vm.AddAccountCommand.Execute(null);
        Assert.Single(vm.Accounts);
        Assert.Equal("student01", vm.Accounts[0].Username);
        Assert.Equal("账号已添加", vm.Status);
        Assert.NotEmpty(new UserStore(_workDir).LoadAccounts());
        Assert.Contains("账号已添加", vm.LogText);
    }

    [AvaloniaFact]
    public void AddAccount_rejects_empty_and_duplicate()
    {
        var vm = CreateVm();
        vm.Username = "";
        vm.AddAccountCommand.Execute(null);
        Assert.Equal("用户名不能为空", vm.Status);

        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        vm.NewAccountCommand.Execute(null);
        vm.Username = "user";
        vm.Password = "p2";
        vm.AddAccountCommand.Execute(null);
        Assert.Equal("账号已存在: user", vm.Status);
        Assert.Single(vm.Accounts);
    }

    [AvaloniaFact]
    public void UpdateAccount_writes_form_back_to_selection()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        vm.Username = "user2";
        vm.Note = "updated";
        vm.UpdateAccountCommand.Execute(null);
        Assert.Equal("user2", vm.Accounts[0].Username);
        Assert.Equal("user2", new UserStore(_workDir).LoadAccounts()[0].Username);
    }

    [AvaloniaFact]
    public void ToggleAccountEnabled_flips_state()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        Assert.True(vm.Accounts[0].Enabled);
        vm.ToggleAccountEnabledCommand.Execute(null);
        Assert.False(vm.Accounts[0].Enabled);
        Assert.Equal("账号已停用: user", vm.Status);
        vm.ToggleAccountEnabledCommand.Execute(null);
        Assert.True(vm.Accounts[0].Enabled);
    }

    [AvaloniaFact]
    public void UseAccount_sets_active_account()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        vm.UseAccountCommand.Execute(null);
        var settings = new UserStore(_workDir).LoadSettings();
        Assert.Equal(vm.Accounts[0].Id, settings.ActiveAccountId);
    }

    [AvaloniaFact]
    public void DeleteAccount_removes_selection_and_persists()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        vm.DeleteAccountCommand.Execute(null);
        Assert.Empty(vm.Accounts);
        Assert.Empty(new UserStore(_workDir).LoadAccounts());
        Assert.Contains("账号已删除", vm.Status);
    }

    [AvaloniaFact]
    public void Save_persists_settings()
    {
        var vm = CreateVm();
        vm.ServiceName = "电信宽带服务";
        vm.StaticId = "Static:AB45A862";
        vm.SaveCommand.Execute(null);
        var settings = new UserStore(_workDir).LoadSettings();
        Assert.Equal("电信宽带服务", settings.ServiceName);
        Assert.Equal("Static:AB45A862", settings.StaticId);
        Assert.Equal("配置已保存", vm.Status);
    }

    // ---------- 认证页 ----------

    [AvaloniaFact]
    public async Task Start_blocked_when_service_running()
    {
        var vm = CreateVm(serviceRunning: true);
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("系统服务正在托管认证，请先停止服务", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Contains("系统服务正在托管认证", vm.LogText);
    }

    [AvaloniaFact]
    public async Task Start_blocked_without_account()
    {
        var vm = CreateVm();
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("请先选择或添加一个带密码的账号", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Start_blocked_without_interface()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        vm.SelectedInterface = null;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("请选择网卡", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public void Stop_signals_cancel()
    {
        var vm = CreateVm();
        vm.StopCommand.Execute(null);
        Assert.Equal("正在停止...", vm.Status);
    }

    [AvaloniaFact]
    public void ClearLog_empties_view()
    {
        var vm = CreateVm();
        vm.Username = "u";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        Assert.NotEmpty(vm.LogText);
        vm.ClearLogCommand.Execute(null);
        Assert.Equal("", vm.LogText);
    }

    [AvaloniaFact]
    public async Task ServiceLog_lines_appear_live_and_filter_by_verbose()
    {
        var vm = CreateVm();
        var logDir = Path.Combine(_workDir, "logs");
        Directory.CreateDirectory(logDir);
        var serviceLog = Path.Combine(logDir, "service.log");
        await File.AppendAllTextAsync(
            serviceLog,
            $"2026-11-19 21:51:00 [Information] RuijieCampusAuth.Service.AuthWorker: 测试普通行{Environment.NewLine}");
        await File.AppendAllTextAsync(
            serviceLog,
            $"2026-11-19 21:51:01 [Information] RuijieCampusAuth.Service.AuthWorker: 网络管家: NG-TMP-0000 在线{Environment.NewLine}");
        for (var i = 0; i < 40 && !vm.LogText.Contains("[服务"); i++)
        {
            await Task.Delay(100);
        }
        Assert.Contains("[服务", vm.LogText);
        Assert.Contains("测试普通行", vm.LogText);
        Assert.DoesNotContain("网络管家:", vm.LogText); // hidden without 详细信息

        vm.VerboseLog = true;
        Assert.Contains("网络管家:", vm.LogText);
        vm.VerboseLog = false;
        Assert.DoesNotContain("网络管家:", vm.LogText);
    }

    [AvaloniaFact]
    public void OpenLogFolder_uses_injected_opener()
    {
        var vm = CreateVm();
        vm.OpenLogFolderCommand.Execute(null);
        Assert.Single(_opened);
        Assert.EndsWith("logs", _opened[0], StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 网络页 ----------

    [AvaloniaFact]
    public void Server_lock_unlock_delete_flow()
    {
        var vm = CreateVm();
        SeedServer(); // after CreateVm: _workDir is assigned there
        vm.RefreshServersCommand.Execute(null);
        Assert.Single(vm.Servers);
        vm.SelectedServer = vm.Servers[0];

        vm.LockSelectedServerCommand.Execute(null);
        Assert.True(vm.ServerLock);
        Assert.Equal("00:1a:a9:17:ff:ff", vm.LockedServer);
        Assert.True(new UserStore(_workDir).LoadSettings().ServerLock);

        vm.UnlockServerCommand.Execute(null);
        Assert.False(vm.ServerLock);
        Assert.False(new UserStore(_workDir).LoadSettings().ServerLock);

        vm.DeleteServerCommand.Execute(null);
        Assert.Empty(vm.Servers);
        Assert.Empty(new UserStore(_workDir).LoadServers());
    }

    [AvaloniaFact]
    public void Server_commands_guard_without_selection()
    {
        var vm = CreateVm();
        vm.LockSelectedServerCommand.Execute(null);
        Assert.Equal("请先选择服务器", vm.Status);
        vm.DeleteServerCommand.Execute(null);
        Assert.Equal("请先选择服务器", vm.Status);
    }

    [AvaloniaFact]
    public void ForceRediscover_clears_cache()
    {
        new UserStore(_workDir).SaveDiscovery(
            @"\Device\NPF_{TEST-GUID}", "Test Adapter", "00:11:22:33:44:55", "66:77:88:99:aa:bb");
        var vm = CreateVm();
        vm.ForceRediscoverCommand.Execute(null);
        Assert.Contains("已清除发现缓存", vm.DiscoveryStatus);
        Assert.Null(new UserStore(_workDir).LoadDiscovery());
    }

    // ---------- 服务页 ----------

    [AvaloniaFact]
    public void RefreshService_reports_state()
    {
        var vm = CreateVm();
        vm.RefreshServiceCommand.Execute(null);
        Assert.Contains("fake", vm.ServiceStatus);
    }

    [AvaloniaFact]
    public async Task ApplyServiceConfig_writes_service_json()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        await Task.Run(() => vm.ApplyServiceConfigCommand.Execute(null));
        Assert.True(File.Exists(ServiceConfig.ConfigPath));
        Assert.Contains("服务配置已更新", vm.Status);
    }

    [AvaloniaFact]
    public async Task InstallService_calls_bridge_in_order()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        await Task.Run(() => vm.InstallServiceCommand.Execute(null));
        Assert.Equal(
            ["install", "start"],
            _bridge.Calls.Where(c => c != "query").ToArray());
        Assert.Contains("系统服务已安装并启动", vm.LogText);
    }

    [AvaloniaFact]
    public void StartStopUninstall_calls_bridge()
    {
        var vm = CreateVm();
        vm.StartServiceCommand.Execute(null);
        vm.StopServiceCommand.Execute(null);
        vm.UninstallServiceCommand.Execute(null);
        Assert.Contains("start", _bridge.Calls);
        Assert.Contains("stop", _bridge.Calls);
        Assert.Contains("uninstall", _bridge.Calls);
    }

    // ---------- 配置页（学校档案） ----------

    [AvaloniaFact]
    public void Profile_discover_import_switch()
    {
        var profileDir = Path.Combine(_workDir, "profiles");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(
            Path.Combine(profileDir, "test-school.json"),
            """
            {
              "name": "测试大学",
              "school_code": "TEST",
              "auth_mode": "EAPMD5",
              "service_name": "电信宽带服务",
              "static_id": "Static:TEST01"
            }
            """);
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null); // service.json propagation needs credentials.
        vm.SaveCommand.Execute(null); // Ensure settings.json exists so Import takes a backup.
        vm.ApplyServiceConfigCommand.Execute(null); // Create service.json: import then propagates into it.
        vm.RefreshProfilesCommand.Execute(null);
        Assert.Contains(vm.AvailableProfiles, p => p.Name == "测试大学");

        vm.SelectedProfile = vm.AvailableProfiles.First(p => p.Name == "测试大学");
        vm.ImportProfileCommand.Execute(null);
        Assert.Contains("已切换到 测试大学", vm.ProfileStatus);
        var settings = new UserStore(_workDir).LoadSettings();
        Assert.Equal("测试大学", settings.ProfileName);
        Assert.Equal("电信宽带服务", settings.ServiceName);
        Assert.Equal("Static:TEST01", settings.StaticId);
        Assert.Equal("配置档案: 测试大学", vm.ProfileNameLabel);
        // Backup created next to settings.json.
        var bin = new DirectoryInfo(_workDir);
        Assert.NotEmpty(bin.GetFiles("settings.json.bak-*"));

        // 切换学校 → service.json 立即同步（服务下一轮生效，无需手动应用）。
        Assert.True(File.Exists(Path.Combine(_workDir, "service.json")), "service.json should exist");
        var svc = ServiceConfig.Load(Path.Combine(_workDir, "service.json"));
        Assert.Equal("测试大学", svc.Settings.ProfileName);
        Assert.Equal("电信宽带服务", svc.Settings.ServiceName);
        Assert.Equal("Static:TEST01", svc.Settings.StaticId);
    }

    [AvaloniaFact]
    public void AuthMode_switches_without_reimport()
    {
        var vm = CreateVm();
        vm.Username = "user";
        vm.Password = "p";
        vm.AddAccountCommand.Execute(null);
        Assert.Equal("EAPMD5", vm.AuthModes[0].Value);
        vm.SelectedAuthMode = vm.AuthModes.First(m => m.Value == "PEAP");
        Assert.Equal("PEAP", vm.SelectedAuthMode.Value);
        vm.SaveCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(_workDir, "settings.json")));
        var settings = new UserStore(_workDir).LoadSettings();
        Assert.Equal("PEAP", settings.AuthMode);
        vm.ApplyServiceConfigCommand.Execute(null);
        Assert.Contains("服务配置已更新", vm.Status);
        var svc = ServiceConfig.Load(Path.Combine(_workDir, "service.json"));
        Assert.Equal("PEAP", svc.Settings.AuthMode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir) &&
                _workDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private sealed class FakeServiceBridge : IServiceBridge
    {
        public List<string> Calls { get; } = [];
        public bool Running { get; set; }

        public ServiceState Query()
        {
            Calls.Add("query");
            return new ServiceState(true, Running, "fake service state");
        }

        public void Install() => Calls.Add("install");
        public void Uninstall() => Calls.Add("uninstall");
        public void Start() => Calls.Add("start");
        public void Stop() => Calls.Add("stop");
    }
}
