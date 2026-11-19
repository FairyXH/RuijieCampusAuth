using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core;

public sealed class ServiceSettings
{
    public string Interface { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string StaticId { get; init; } = "";
    public double Timeout { get; init; } = 20;
    public int Retries { get; init; } = 4;
    public int Repeats { get; init; } = 3;
    public double StartTimeout { get; init; } = 3;
    public bool Preempt { get; init; } = true;
    public double CheckInterval { get; init; } = 2;
    public double RetryInterval { get; init; } = 3;
    public string ConnectivityUrl { get; init; } = "http://www.baidu.com/";
    public bool ServerLock { get; init; }
    public string LockedServer { get; init; } = "";
    public bool TakeoverEnabled { get; init; }
    public string DhcpMode { get; init; } = "before";
    public double DhcpTimeout { get; init; } = 30;
    public bool DhcpOption82 { get; init; }
    public string SchoolCode { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string AuthMode { get; init; } = "EAPMD5";
    public string SelfServiceUrl { get; init; } = "";
    public string UpdateUrl { get; init; } = "";
    public string PortalUrl { get; init; } = "";
    public string SamHost { get; init; } = "";
    public int SamPort { get; init; }
    public string AdDomain { get; init; } = "";
    public double AdTimeout { get; init; } = 120;
    public string EngineVersion { get; init; } = "";
    public bool UseServerReauth { get; init; } = true;
    public bool GuardianEnabled { get; init; }
    public string GuardianUrl { get; init; } = "http://127.0.0.1:18765/mcp";
    public double GuardianInterval { get; init; } = 5;
    public int GuardianFailures { get; init; } = 2;

    public static ServiceSettings FromUser(UserSettings settings)
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
}

public sealed class ServiceConfig
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public ServiceSettings Settings { get; init; } = new();

    public static string ServiceRoot => AppContext.BaseDirectory;
    public static string ConfigPath => Path.Combine(ServiceRoot, "service.json");

    public static ServiceConfig Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var account = root.TryGetProperty("account", out var accountElement)
            ? accountElement
            : default;
        var settingsElement = root.TryGetProperty("settings", out var settingsValue)
            ? settingsValue
            : default;
        var username = GetString(account, "username");
        var password = Unprotect(GetString(account, "password_enc"));
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("service.json 缺少账号或密码");
        }
        var settings = new ServiceSettings
        {
            Interface = GetString(settingsElement, "interface"),
            ServiceName = GetStringOr(settingsElement, "service_name", ""),
            StaticId = GetStringOr(settingsElement, "static_id", ""),
            Timeout = GetDouble(settingsElement, "timeout") ?? 20,
            Retries = (int)(GetDouble(settingsElement, "retries") ?? 4),
            Repeats = (int)(GetDouble(settingsElement, "repeats") ?? 3),
            StartTimeout = GetDouble(settingsElement, "start_timeout") ?? 3,
            Preempt = GetBool(settingsElement, "preempt") ?? true,
            CheckInterval = GetDouble(settingsElement, "check_interval") ?? 2,
            RetryInterval = GetDouble(settingsElement, "retry_interval") ?? 3,
            ConnectivityUrl = GetStringOr(settingsElement, "connectivity_url", "http://www.baidu.com/"),
            ServerLock = GetBool(settingsElement, "server_lock") ?? false,
            LockedServer = GetString(settingsElement, "locked_server"),
            TakeoverEnabled = GetBool(settingsElement, "takeover_enabled") ?? false,
            DhcpMode = GetStringOr(settingsElement, "dhcp_mode", "before"),
            DhcpTimeout = GetDouble(settingsElement, "dhcp_timeout") ?? 30,
            DhcpOption82 = GetBool(settingsElement, "dhcp_option82") ?? false,
            SchoolCode = GetString(settingsElement, "school_code"),
            ProfileName = GetString(settingsElement, "profile_name"),
            AuthMode = GetStringOr(settingsElement, "auth_mode", "EAPMD5"),
            SelfServiceUrl = GetString(settingsElement, "self_service_url"),
            UpdateUrl = GetString(settingsElement, "update_url"),
            PortalUrl = GetString(settingsElement, "portal_url"),
            SamHost = GetString(settingsElement, "sam_host"),
            SamPort = (int)(GetDouble(settingsElement, "sam_port") ?? 0),
            AdDomain = GetString(settingsElement, "ad_domain"),
            AdTimeout = GetDouble(settingsElement, "ad_timeout") ?? 120,
            EngineVersion = GetString(settingsElement, "engine_version"),
            UseServerReauth = GetBool(settingsElement, "use_server_reauth") ?? true,
            GuardianEnabled = GetBool(settingsElement, "guardian_enabled") ?? false,
            GuardianUrl = GetStringOr(settingsElement, "guardian_url", "http://127.0.0.1:18765/mcp"),
            GuardianInterval = GetDouble(settingsElement, "guardian_interval") ?? 5,
            GuardianFailures = (int)(GetDouble(settingsElement, "guardian_failures") ?? 2),
        };
        return new ServiceConfig
        {
            Username = username,
            Password = password,
            Settings = settings,
        };
    }

    public static bool ReadTakeoverEnabled(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("settings", out var settings))
        {
            return false;
        }
        return GetBool(settings, "takeover_enabled") ?? false;
    }

    public static void SetTakeover(string path, bool enabled)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("service.json 格式无效");
        if (root["settings"] is not JsonObject settings)
        {
            settings = new JsonObject();
            root["settings"] = settings;
        }
        settings["takeover_enabled"] = enabled;
        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    public static void Save(string path, string username, string password, ServiceSettings settings)
    {
        var payload = new
        {
            version = 1,
            updated_at = DateTimeOffset.Now.ToString("O"),
            account = new
            {
                username,
                password_enc = Protect(password),
            },
            settings = new
            {
                @interface = settings.Interface,
                service_name = settings.ServiceName,
                static_id = settings.StaticId,
                timeout = settings.Timeout,
                retries = settings.Retries,
                repeats = settings.Repeats,
                start_timeout = settings.StartTimeout,
                preempt = settings.Preempt,
                check_interval = settings.CheckInterval,
                retry_interval = settings.RetryInterval,
                connectivity_url = settings.ConnectivityUrl,
                server_lock = settings.ServerLock,
                locked_server = settings.LockedServer,
                takeover_enabled = settings.TakeoverEnabled,
                dhcp_mode = settings.DhcpMode,
                dhcp_timeout = settings.DhcpTimeout,
                dhcp_option82 = settings.DhcpOption82,
                school_code = settings.SchoolCode,
                profile_name = settings.ProfileName,
                auth_mode = settings.AuthMode,
                self_service_url = settings.SelfServiceUrl,
                update_url = settings.UpdateUrl,
                portal_url = settings.PortalUrl,
                sam_host = settings.SamHost,
                sam_port = settings.SamPort,
                ad_domain = settings.AdDomain,
                ad_timeout = settings.AdTimeout,
                engine_version = settings.EngineVersion,
                use_server_reauth = settings.UseServerReauth,
                guardian_enabled = settings.GuardianEnabled,
                guardian_url = settings.GuardianUrl,
                guardian_interval = settings.GuardianInterval,
                guardian_failures = settings.GuardianFailures,
            },
        };
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(temp, path, true);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort; systemd deployments normally run as root.
            }
        }
    }

    public static string Protect(string password)
    {
        var raw = Encoding.UTF8.GetBytes(password);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.LocalMachine);
            return "machine-dpapi:" + Convert.ToBase64String(encrypted);
        }
        return "plain:" + Convert.ToBase64String(raw);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        if (value.StartsWith("machine-dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("machine DPAPI 仅支持 Windows");
            }
            var raw = Convert.FromBase64String(value["machine-dpapi:".Length..]);
            var plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        if (value.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("DPAPI 仅支持 Windows");
            }
            var raw = Convert.FromBase64String(value[6..]);
            var plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        if (value.StartsWith("plain:", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value[6..]));
        }
        return value;
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
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
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
