using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuijieCampusAuth.Core;

Console.OutputEncoding = Encoding.UTF8;
UserStore.MigrateLegacy(ServiceConfig.ServiceRoot);

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
                    "systemd unit 已写入；请执行: systemctl daemon-reload && systemctl enable --now hxxy-campus-auth.service");
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
        ServiceName = options.GetValueOrDefault("--service-name", "电信宽带服务"),
        StaticId = options.GetValueOrDefault("--static-id", "Static:AB45A862"),
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
    string? LockedServer);

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
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = "电信宽带服务";
        }
        var staticId = staticIdOverride
            ?? (settingsRoot is null ? "" : GetString(settings, "static_id"));
        if (string.IsNullOrWhiteSpace(staticId))
        {
            staticId = "Static:AB45A862";
        }
        var locked = lockedServerOverride;
        if (locked is null && GetBool(settings, "server_lock"))
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
            string.IsNullOrWhiteSpace(locked) ? null : locked);
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
        if (encrypted.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            var raw = Convert.FromBase64String(encrypted[6..]);
            var plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        if (encrypted.StartsWith("plain:", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encrypted[6..]));
        }
        return encrypted;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               value.GetBoolean();
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }
}
