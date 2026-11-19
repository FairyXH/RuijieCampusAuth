using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuijieCampusAuth.Core;

public sealed class SchoolProfile
{
    public string Name { get; set; } = "";
    public string SchoolCode { get; set; } = "";
    public string AuthMode { get; set; } = "EAPMD5";
    public string ServiceName { get; set; } = "";
    public string StaticId { get; set; } = "";
    public string SelfServiceUrl { get; set; } = "";
    public string UpdateUrl { get; set; } = "";
    public string PortalUrl { get; set; } = "";
    public string SamHost { get; set; } = "";
    public int SamPort { get; set; }
    public string DhcpMode { get; set; } = "none";
    public double DhcpTimeout { get; set; } = 30;
    public bool DhcpOption82 { get; set; }
    public string AdDomain { get; set; } = "";
    public double AdTimeout { get; set; } = 120;
    public string EngineVersion { get; set; } = "";
    public string Notes { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record ProfileImportResult(
    string ProfileName,
    string SettingsPath,
    string? BackupPath,
    IReadOnlyList<string> Applied);

public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static SchoolProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<SchoolProfile>(
                          File.ReadAllText(path), JsonOptions)
                      ?? throw new InvalidDataException($"profile is empty: {path}");
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = Path.GetFileNameWithoutExtension(path);
        }
        return profile;
    }

    public static IReadOnlyList<string> ListProfiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> Discover(string? extraDirectory = null)
    {
        var directories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "profiles"),
            Path.Combine(Environment.CurrentDirectory, "profiles"),
            extraDirectory,
        };
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            foreach (var path in ListProfiles(directory))
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full))
                {
                    result.Add(full);
                }
            }
        }
        return result;
    }

    public static ProfileImportResult Import(
        SchoolProfile profile,
        UserStore store,
        string? profilePath = null)
    {
        var settingsPath = store.SettingsPath;
        string? backup = null;
        if (File.Exists(settingsPath))
        {
            backup = $"{settingsPath}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(settingsPath, backup, overwrite: false);
        }

        var settings = store.LoadSettings();
        var applied = new List<string>();
        void Apply(string name, string? value, Action<string> setter)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            setter(value.Trim());
            applied.Add(name);
        }

        settings.ProfileName = profile.Name;
        applied.Add("profile_name");
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            settings.ProfilePath = Path.GetFullPath(profilePath);
            applied.Add("profile_path");
        }
        settings.AuthMode = profile.AuthMode;
        applied.Add("auth_mode");
        Apply("school_code", profile.SchoolCode, value => settings.SchoolCode = value);
        Apply("service_name", profile.ServiceName, value => settings.ServiceName = value);
        Apply("static_id", profile.StaticId, value => settings.StaticId = value);
        Apply("self_service_url", profile.SelfServiceUrl, value =>
        {
            settings.SelfServiceUrl = value;
            settings.Debug.Endpoints.SelfService = value;
        });
        Apply("update_url", profile.UpdateUrl, value =>
        {
            settings.UpdateUrl = value;
            settings.Debug.Endpoints.Upgrade = value;
        });
        Apply("portal_url", profile.PortalUrl, value =>
        {
            settings.PortalUrl = value;
            settings.Debug.Endpoints.Portal = value;
        });
        Apply("sam_host", profile.SamHost, value => settings.SamHost = value);
        if (profile.SamPort > 0)
        {
            settings.SamPort = profile.SamPort;
            applied.Add("sam_port");
        }
        if (profile.DhcpMode is "before" or "after" or "none")
        {
            settings.DhcpMode = profile.DhcpMode;
            applied.Add("dhcp_mode");
        }
        if (profile.DhcpTimeout > 0)
        {
            settings.DhcpTimeout = profile.DhcpTimeout;
            applied.Add("dhcp_timeout");
        }
        settings.DhcpOption82 = profile.DhcpOption82;
        applied.Add("dhcp_option82");
        Apply("ad_domain", profile.AdDomain, value => settings.AdDomain = value);
        if (profile.AdTimeout > 0)
        {
            settings.AdTimeout = profile.AdTimeout;
            applied.Add("ad_timeout");
        }
        Apply("engine_version", profile.EngineVersion, value =>
        {
            settings.EngineVersion = value;
        });

        store.SaveSettings(settings);

        // Propagate to service.json so a running service picks the switch up
        // on its next retry cycle without pressing 应用服务配置.
        string? serviceNote = null;
        try
        {
            if (File.Exists(ServiceConfig.ConfigPath))
            {
                var account = store.ActiveAccount();
                if (account is not null)
                {
                    ServiceConfig.Save(
                        ServiceConfig.ConfigPath,
                        account.Username,
                        account.Password,
                        ServiceSettings.FromUser(settings));
                    serviceNote = "service.json";
                }
            }
        }
        catch
        {
            // service.json may be protected (LocalSystem); the GUI's
            // 应用服务配置 button can refresh it later.
        }
        if (serviceNote is not null)
        {
            applied.Add("service_config");
        }

        return new ProfileImportResult(
            profile.Name,
            settingsPath,
            backup,
            applied);
    }
}
