using System.Text.Json;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.App;

public sealed class GuiSettings
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Interface { get; set; } = "";
    public string ServiceName { get; set; } = "电信宽带服务";
    public string StaticId { get; set; } = "Static:AB45A862";
    public double Timeout { get; set; } = 20;
    public int Retries { get; set; } = 4;
    public int Repeats { get; set; } = 3;
    public double StartTimeout { get; set; } = 3;
    public string ConnectivityUrl { get; set; } = "http://www.baidu.com/";
    public bool ServerLock { get; set; }
    public string LockedServer { get; set; } = "";
    public bool Preempt { get; set; } = true;
    public bool TakeoverEnabled { get; set; }
    public double CheckInterval { get; set; } = 2;
    public double RetryInterval { get; set; } = 3;
}

public static class GuiSettingsStore
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RuijieCampusAuth",
        "csharp-gui.json");

    public static GuiSettings Load()
    {
        if (File.Exists(Path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path));
                var root = document.RootElement;
                return new GuiSettings
                {
                    Username = GetString(root, "username"),
                    Password = ServiceConfig.Unprotect(GetString(root, "password_enc")),
                    Interface = GetString(root, "interface"),
                    ServiceName = GetStringOr(root, "service_name", "电信宽带服务"),
                    StaticId = GetStringOr(root, "static_id", "Static:AB45A862"),
                    Timeout = GetDouble(root, "timeout") ?? 20,
                    Retries = (int)(GetDouble(root, "retries") ?? 4),
                    Repeats = (int)(GetDouble(root, "repeats") ?? 3),
                    StartTimeout = GetDouble(root, "start_timeout") ?? 3,
                    ConnectivityUrl = GetStringOr(root, "connectivity_url", "http://www.baidu.com/"),
                    ServerLock = GetBool(root, "server_lock"),
                    LockedServer = GetString(root, "locked_server"),
                    Preempt = GetBool(root, "preempt", true),
                    TakeoverEnabled = GetBool(root, "takeover_enabled"),
                    CheckInterval = GetDouble(root, "check_interval") ?? 2,
                    RetryInterval = GetDouble(root, "retry_interval") ?? 3,
                };
            }
            catch
            {
                // Fall through to migration/defaults.
            }
        }
        if (File.Exists(ServiceConfig.ConfigPath))
        {
            try
            {
                var service = ServiceConfig.Load(ServiceConfig.ConfigPath);
                return FromService(service);
            }
            catch
            {
                // Ignore an unreadable legacy service config.
            }
        }
        return new GuiSettings();
    }

    public static void Save(GuiSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var payload = new
        {
            version = 1,
            username = settings.Username,
            password_enc = ServiceConfig.Protect(settings.Password),
            @interface = settings.Interface,
            service_name = settings.ServiceName,
            static_id = settings.StaticId,
            timeout = settings.Timeout,
            retries = settings.Retries,
            repeats = settings.Repeats,
            start_timeout = settings.StartTimeout,
            connectivity_url = settings.ConnectivityUrl,
            server_lock = settings.ServerLock,
            locked_server = settings.LockedServer,
            takeover_enabled = settings.TakeoverEnabled,
            preempt = settings.Preempt,
            check_interval = settings.CheckInterval,
            retry_interval = settings.RetryInterval,
        };
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(temp, Path, true);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static GuiSettings FromService(ServiceConfig service)
    {
        return new GuiSettings
        {
            Username = service.Username,
            Password = service.Password,
            Interface = service.Settings.Interface,
            ServiceName = service.Settings.ServiceName,
            StaticId = service.Settings.StaticId,
            Timeout = service.Settings.Timeout,
            Retries = service.Settings.Retries,
            Repeats = service.Settings.Repeats,
            StartTimeout = service.Settings.StartTimeout,
            ConnectivityUrl = service.Settings.ConnectivityUrl,
            ServerLock = service.Settings.ServerLock,
            LockedServer = service.Settings.LockedServer,
            TakeoverEnabled = service.Settings.TakeoverEnabled,
            Preempt = service.Settings.Preempt,
            CheckInterval = service.Settings.CheckInterval,
            RetryInterval = service.Settings.RetryInterval,
        };
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string GetStringOr(JsonElement element, string name, string fallback)
    {
        var value = GetString(element, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback = false)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }
}
