using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Debug;

Console.OutputEncoding = Encoding.UTF8;

if (args.Any(arg => arg is "--help" or "-h" or "help"))
{
    ConfigStore.PrintUsage();
    return 0;
}

UserStore.MigrateLegacy(ServiceConfig.ServiceRoot);
new UserStore(ServiceConfig.ServiceRoot).UpgradePasswordProtection();

var options = Parse(args);
if (options.ContainsKey("--list-devices"))
{
    foreach (var device in PcapNative.ListDevices())
    {
        Console.WriteLine($"{device.Name} | {device.Description}");
    }
    return 0;
}

var command = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
    ?? "auth";
if (command == "status")
{
    PrintStatus();
    return 0;
}
if (command == "debug")
{
    var action = Subcommand(args, "debug") ?? "list";
    var store = new UserStore(ServiceConfig.ServiceRoot);
    var gateway = new DebugGateway(store);
    switch (action)
    {
        case "list":
        {
            var result = await gateway.ListAsync();
            foreach (var item in result.Features)
            {
                Console.WriteLine(
                    $"{item.Name,-22} {item.Phase,-3} {(item.Enabled ? "on " : "off")} " +
                    $"{item.State,-12} {item.DisplayName}");
            }
            Console.WriteLine($"source: {result.Source}");
            return 0;
        }
        case "status":
        {
            var name = Subcommand(args, "status") ?? "system-info";
            var result = await gateway.ListAsync();
            var item = result.Features.FirstOrDefault(
                feature => feature.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                Console.Error.WriteLine($"未知功能: {name}");
                return 2;
            }
            Console.WriteLine($"name: {item.Name}");
            Console.WriteLine($"display: {item.DisplayName}");
            Console.WriteLine($"phase: {item.Phase}");
            Console.WriteLine($"enabled: {item.Enabled}");
            Console.WriteLine($"state: {item.State}");
            Console.WriteLine($"requires_service: {item.RequiresService}");
            Console.WriteLine($"description: {item.Description}");
            return 0;
        }
        case "run":
        {
            var name = Subcommand(args, "run");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("用法: debug run <feature> [--args '<json>']");
                return 2;
            }
            var result = await gateway.RunAsync(
                name,
                options.GetValueOrDefault("--args"));
            Console.WriteLine(result.Ok ? "OK" : "FAILED");
            Console.WriteLine(result.Message);
            if (!string.IsNullOrWhiteSpace(result.Data))
            {
                Console.WriteLine(result.Data);
            }
            return result.Ok ? 0 : 1;
        }
        case "stop":
        {
            var name = Subcommand(args, "stop");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("用法: debug stop <feature>");
                return 2;
            }
            var result = await gateway.StopAsync(name);
            Console.WriteLine(result.Ok ? "OK" : "FAILED");
            Console.WriteLine(result.Message);
            return result.Ok ? 0 : 1;
        }
        case "enable":
        case "disable":
        {
            var name = Subcommand(args, action);
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine($"用法: debug {action} <feature|all>");
                return 2;
            }
            var settings = store.LoadSettings();
            var enabled = action == "enable";
            if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                settings.Debug.Enabled = enabled;
            }
            else
            {
                settings.Debug.Enabled = true;
                settings.Debug.SetFeature(name, enabled);
            }
            store.SaveSettings(settings);
            Console.WriteLine($"{name}: {(enabled ? "enabled" : "disabled")}");
            return 0;
        }
        case "settings":
        {
            var settings = store.LoadDebugSettings();
            Console.WriteLine(JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }));
            return 0;
        }
        default:
            Console.Error.WriteLine(
                "用法: debug list|status|run|stop|enable|disable|settings");
            return 2;
    }
}
if (command == "profile")
{
    var action = Subcommand(args, "profile") ?? "list";
    var root = options.GetValueOrDefault("--root") ?? ServiceConfig.ServiceRoot;
    var store = new UserStore(root);
    switch (action)
    {
        case "list":
        {
            var profiles = options.GetValueOrDefault("--dir") is { Length: > 0 } directory
                ? ProfileStore.ListProfiles(directory)
                : ProfileStore.Discover();
            if (profiles.Count == 0)
            {
                Console.WriteLine("没有可用档案（检查程序目录/当前目录的 profiles/）");
                return 1;
            }
            foreach (var path in profiles)
            {
                Console.WriteLine(path);
            }
            return 0;
        }
        case "show":
        {
            var path = Subcommand(args, "show") ?? options.GetValueOrDefault("--path");
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.Error.WriteLine("用法: profile show <profile.json>");
                return 2;
            }
            var profile = ProfileStore.Load(path);
            Console.WriteLine(JsonSerializer.Serialize(profile, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }));
            return 0;
        }
        case "import":
        {
            var path = Subcommand(args, "import") ?? options.GetValueOrDefault("--path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.Error.WriteLine("用法: profile import <profile.json> [--root <install-dir>]");
                return 2;
            }
            var profile = ProfileStore.Load(path);
            var result = ProfileStore.Import(profile, store, path);
            Console.WriteLine($"profile: {result.ProfileName}");
            Console.WriteLine($"settings: {result.SettingsPath}");
            Console.WriteLine($"backup: {result.BackupPath ?? "(none)"}");
            Console.WriteLine($"applied: {string.Join(", ", result.Applied)}");
            return 0;
        }
        default:
            Console.Error.WriteLine("用法: profile list|show|import");
            return 2;
    }
}
if (command == "service")
{
    var action = Subcommand(args, "service") ?? "status";
    switch (action)
    {
        case "install":
            if (!File.Exists(ServiceConfig.ConfigPath))
            {
                Console.Error.WriteLine("缺少 service.json，请先执行 config init");
                return 2;
            }
            ServiceInstaller.Install();
            if (OperatingSystem.IsWindows())
            {
                ServiceInstaller.Start();
                Console.WriteLine("服务已安装并启动");
            }
            else
            {
                Console.WriteLine(
                    "systemd unit 已写入；请执行: systemctl daemon-reload && systemctl enable --now ruijie-campus-auth.service");
            }
            PrintStatus();
            return 0;
        case "uninstall":
            ServiceInstaller.Uninstall();
            Console.WriteLine("服务已卸载");
            return 0;
        case "start":
            ServiceInstaller.Start();
            Console.WriteLine("服务已启动");
            return 0;
        case "stop":
            ServiceInstaller.Stop();
            Console.WriteLine("服务已停止");
            return 0;
        case "status":
            PrintStatus();
            return 0;
        default:
            Console.Error.WriteLine("用法: service install|uninstall|start|stop|status");
            return 2;
    }
}
if (command == "config")
{
    var action = Subcommand(args, "config") ?? "show";
    if (action == "path")
    {
        Console.WriteLine(ServiceConfig.ConfigPath);
        return 0;
    }
    if (action == "init")
    {
        var username = options.GetValueOrDefault("--username");
        var password = options.GetValueOrDefault("--password");
        var interfaceName = options.GetValueOrDefault("--interface");
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(interfaceName))
        {
            Console.Error.WriteLine("config init 需要 --username --password --interface");
            return 2;
        }
        ServiceConfig.Save(
            ServiceConfig.ConfigPath,
            username,
            password,
            BuildSettings(options));
        Console.WriteLine($"配置已写入: {ServiceConfig.ConfigPath}");
        return 0;
    }
    if (action == "takeover")
    {
        if (!File.Exists(ServiceConfig.ConfigPath))
        {
            Console.Error.WriteLine("service.json 不存在");
            return 2;
        }
        var value = Subcommand(args, "takeover") ?? "";
        if (value is not ("on" or "off"))
        {
            Console.Error.WriteLine("用法: config takeover on|off");
            return 2;
        }
        ServiceConfig.SetTakeover(ServiceConfig.ConfigPath, value == "on");
        Console.WriteLine($"接管模式: {(value == "on" ? "开启" : "关闭")}");
        return 0;
    }
    if (action == "show")
    {
        if (!File.Exists(ServiceConfig.ConfigPath))
        {
            Console.Error.WriteLine("service.json 不存在");
            return 2;
        }
        var config = ServiceConfig.Load(ServiceConfig.ConfigPath);
        Console.WriteLine($"username: {config.Username}");
        Console.WriteLine($"interface: {config.Settings.Interface}");
        Console.WriteLine($"service: {config.Settings.ServiceName}");
        Console.WriteLine($"static id: {config.Settings.StaticId}");
        return 0;
    }
    Console.Error.WriteLine("用法: config init|show|path");
    return 2;
}

var context = ConfigStore.Load(
    options.GetValueOrDefault("--interface"),
    options.GetValueOrDefault("--username"),
    options.GetValueOrDefault("--password"),
    options.GetValueOrDefault("--service-name"),
    options.GetValueOrDefault("--static-id"),
    options.GetValueOrDefault("--server"),
    options.TryGetValue("--start-timeout", out var startText) ? double.Parse(startText) : null,
    options.TryGetValue("--timeout", out var timeoutText) ? double.Parse(timeoutText) : null,
    options.TryGetValue("--retries", out var retriesText) ? int.Parse(retriesText) : null,
    options.TryGetValue("--repeats", out var repeatsText) ? int.Parse(repeatsText) : null);

Console.WriteLine($"interface: {context.Interface}");
Console.WriteLine($"username: {context.Username}");
Console.WriteLine($"service: {context.ServiceName}");
Console.WriteLine($"static id: {context.StaticId}");
Console.WriteLine($"locked server: {context.LockedServer ?? "(auto)"}");

using var supplicant = new Supplicant(
    context.Interface,
    context.Username,
    context.Password,
    timeout: context.Timeout,
    retries: context.Retries,
    repeats: context.Repeats,
    startTimeout: context.StartTimeout,
    lockedServer: context.LockedServer,
    serviceName: context.ServiceName,
    staticId: context.StaticId,
    authMode: context.AuthMode,
    log: Console.WriteLine,
    error: message => Console.Error.WriteLine(message));
var ok = supplicant.Authenticate();
Console.WriteLine(ok ? "RESULT SUCCESS" : "RESULT FAILURE");
return ok ? 0 : 1;

static Dictionary<string, string> Parse(string[] argv)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Length; i++)
    {
        var item = argv[i];
        if (!item.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }
        if (item is "--list-devices" or "--verbose")
        {
            result[item] = "1";
            continue;
        }
        if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[item] = argv[++i];
        }
        else
        {
            result[item] = "1";
        }
    }
    return result;
}

static string? Subcommand(string[] argv, string name)
{
    var index = Array.IndexOf(argv, name);
    return index >= 0 && index + 1 < argv.Length &&
           !argv[index + 1].StartsWith("--", StringComparison.Ordinal)
        ? argv[index + 1]
        : null;
}

static void PrintStatus()
{
    var state = ServiceInstaller.Query();
    Console.WriteLine(
        $"service: installed={state.Installed} running={state.Running} {state.Detail}");
    var status = ServiceStatus.Read();
    if (status is not null)
    {
        Console.WriteLine($"status: {status.State} | {status.Message} | {status.Time}");
    }
}

static ServiceSettings BuildSettings(Dictionary<string, string> options)
{
    return new ServiceSettings
    {
        Interface = options.GetValueOrDefault("--interface", ""),
        ServiceName = options.GetValueOrDefault("--service-name", ""),
        StaticId = options.GetValueOrDefault("--static-id", ""),
        Timeout = options.TryGetValue("--timeout", out var timeout) ? double.Parse(timeout) : 20,
        Retries = options.TryGetValue("--retries", out var retries) ? int.Parse(retries) : 4,
        Repeats = options.TryGetValue("--repeats", out var repeats) ? int.Parse(repeats) : 3,
        StartTimeout = options.TryGetValue("--start-timeout", out var start) ? double.Parse(start) : 3,
        Preempt = !options.ContainsKey("--no-preempt"),
        CheckInterval = options.TryGetValue("--check-interval", out var check) ? double.Parse(check) : 2,
        RetryInterval = options.TryGetValue("--retry-interval", out var retry) ? double.Parse(retry) : 3,
        ConnectivityUrl = options.GetValueOrDefault("--url", "http://www.baidu.com/"),
        ServerLock = options.ContainsKey("--server"),
        LockedServer = options.GetValueOrDefault("--server", ""),
    };
}

internal sealed record LegacyContext(
    string Username,
    string Password,
    string Interface,
    string ServiceName,
    string StaticId,
    double Timeout,
    int Retries,
    int Repeats,
    double StartTimeout,
    string? LockedServer,
    string AuthMode = "EAPMD5");

internal static class ConfigStore
{
    public static LegacyContext Load(
        string? interfaceOverride,
        string? usernameOverride,
        string? passwordOverride,
        string? serviceNameOverride,
        string? staticIdOverride,
        string? lockedServerOverride,
        double? startTimeoutOverride,
        double? timeoutOverride,
        int? retriesOverride,
        int? repeatsOverride)
    {
        var root = ServiceConfig.ServiceRoot;
        var settingsRoot = LoadRoot(Path.Combine(root, "settings.json"));
        var accountsRoot = LoadRoot(Path.Combine(root, "accounts.json"));
        var settings = settingsRoot ?? default;
        var activeId = settingsRoot is null ? null : GetString(settingsRoot.Value, "active_account_id");
        JsonElement? selected = null;
        if (accountsRoot is { } accounts &&
            accounts.TryGetProperty("accounts", out var accountsArray) &&
            accountsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in accountsArray.EnumerateArray())
            {
                if (GetString(account, "id") == activeId)
                {
                    selected = account.Clone();
                    break;
                }
            }
        }
        var username = usernameOverride
            ?? (selected is { } selectedAccount
                ? GetString(selectedAccount, "username")
                : "");
        var password = passwordOverride
            ?? (selected is { } passwordAccount
                ? Decrypt(GetString(passwordAccount, "password_enc"))
                : "");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("没有可用账号；请用 --username --password 或先写入安装目录账号库");
        }
        var interfaceName = interfaceOverride
            ?? (settingsRoot is null ? "" : GetString(settings, "interface"));
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            throw new InvalidOperationException("settings.interface is empty; use --interface");
        }
        var serviceName = serviceNameOverride
            ?? (settingsRoot is null ? "" : GetString(settings, "service_name"));
        var staticId = staticIdOverride
            ?? (settingsRoot is null ? "" : GetString(settings, "static_id"));
        var locked = lockedServerOverride;
        if (locked is null && settingsRoot is not null && GetBool(settings, "server_lock"))
        {
            locked = GetString(settings, "locked_server");
        }
        var timeout = timeoutOverride ?? GetDouble(settings, "timeout") ?? 20;
        var retries = retriesOverride ?? (int)(GetDouble(settings, "retries") ?? 4);
        var repeats = repeatsOverride ?? (int)(GetDouble(settings, "repeats") ?? 3);
        var startTimeout = startTimeoutOverride
            ?? GetDouble(settings, "start_timeout")
            ?? 3.0;
        return new LegacyContext(
            username,
            password,
            interfaceName,
            serviceName,
            staticId,
            timeout,
            retries,
            repeats,
            startTimeout,
            string.IsNullOrWhiteSpace(locked) ? null : locked,
            settingsRoot is null ? "EAPMD5" : GetString(settings, "auth_mode") ?? "EAPMD5");
    }

    private static JsonElement? LoadRoot(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string Decrypt(string encrypted)
    {
        try
        {
            if (encrypted.StartsWith("machine-dpapi:", StringComparison.Ordinal))
            {
                var raw = Convert.FromBase64String(
                    encrypted["machine-dpapi:".Length..]);
                var plain = ProtectedData.Unprotect(
                    raw, null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(plain);
            }
            if (encrypted.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                var raw = Convert.FromBase64String(encrypted[6..]);
                var plain = ProtectedData.Unprotect(
                    raw, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            if (encrypted.StartsWith("plain:", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encrypted[6..]));
            }
            return encrypted;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "无法解密账号密码；请在 GUI 中重新保存该账号，" +
                "或运行时显式传入 --username/--password", ex);
        }
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            用法: RuijieCampusAuth.Cli <command> [options]

            命令:
              status                         查看服务状态
              auth                           用当前设置执行一次认证
              service install|uninstall|start|stop|status
                                             管理 Windows 服务
              config init|show|path|takeover on|off
                                             管理 service.json
              profile list|show|import       管理学校配置档案
              debug list|status|run|...      调试功能
              --list-devices                 列出 Npcap 网卡

            常用选项:
              --interface <name> --username <user> --password <pass>
              --service-name <name> --static-id <id> --server <mac>
              --timeout <s> --retries <n> --repeats <n>
            """);
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               value.GetBoolean();
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }
}
