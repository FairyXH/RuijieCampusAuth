using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuijieCampusAuth.Core.Debug;

namespace RuijieCampusAuth.Core;

public sealed class UserAccount : INotifyPropertyChanged
{
    private string _username = "";
    private string _note = "";
    private bool _enabled = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username
    {
        get => _username;
        set
        {
            if (_username == value)
            {
                return;
            }
            _username = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public string Password { get; set; } = "";
    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
            {
                return;
            }
            _note = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }
            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public string Display =>
        $"{(Enabled ? "" : "[停用] ")}{(string.IsNullOrWhiteSpace(Note) ? Username : $"{Username}  ({Note})")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class UserSettings
{
    public int Version { get; set; } = 1;
    public string? ActiveAccountId { get; set; }
    public string Interface { get; set; } = "";
    public bool Preempt { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public double CheckInterval { get; set; } = 2;
    public double RetryInterval { get; set; } = 3;
    public double Timeout { get; set; } = 20;
    public int Retries { get; set; } = 3;
    public int Repeats { get; set; } = 3;
    public string ConnectivityUrl { get; set; } = "http://www.baidu.com/";
    public bool Verbose { get; set; }
    public bool LogoffOnStop { get; set; }
    public bool BlockOfficialClient { get; set; }
    public bool ServerLock { get; set; }
    public string LockedServer { get; set; } = "";
    public bool GuardianEnabled { get; set; }
    public string GuardianUrl { get; set; } = "http://127.0.0.1:18765/mcp";
    public double GuardianInterval { get; set; } = 5;
    public int GuardianFailures { get; set; } = 2;
    public string ServiceName { get; set; } = "电信宽带服务";
    public string StaticId { get; set; } = "Static:AB45A862";
    public double StartTimeout { get; set; } = 3;
    public string DhcpMode { get; set; } = "before";
    public double DhcpTimeout { get; set; } = 30;
    public bool DhcpOption82 { get; set; }
    public string SchoolCode { get; set; } = "";
    public bool TakeoverEnabled { get; set; }
    public DebugSettings Debug { get; set; } = new();
}

public sealed class DiscoveryInfo
{
    public string Interface { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mac { get; set; } = "";
    public string ServerMac { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class ServerRecord
{
    public string Mac { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public string Interface { get; set; } = "";
    public string Description { get; set; } = "";
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
    public string LastSuccess { get; set; } = "";
    public int SuccessCount { get; set; }

    [JsonIgnore]
    public string Display =>
        string.IsNullOrWhiteSpace(Name) ? Mac : $"{Mac}  ({Name})";

    [JsonIgnore]
    public string Detail
    {
        get
        {
            var success = string.IsNullOrWhiteSpace(LastSuccess)
                ? "尚未成功"
                : $"最近成功 {LastSuccess}";
            return $"成功 {SuccessCount} 次 | {success} | {Description}";
        }
    }
}

public sealed class UserStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public UserStore(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        Directory.CreateDirectory(BaseDirectory);
    }

    public static void MigrateLegacy(string targetDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RuijieCampusAuth");
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }
        if (Path.GetFullPath(legacyDirectory).Equals(
                Path.GetFullPath(targetDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (var name in new[]
                     {
                         "accounts.json",
                         "settings.json",
                         "discovery.json",
                         "servers.json",
                     })
            {
                var source = Path.Combine(legacyDirectory, name);
                var destination = Path.Combine(targetDirectory, name);
                if (File.Exists(source) && !File.Exists(destination))
                {
                    File.Copy(source, destination);
                }
            }
        }
        catch
        {
            // A failed migration must not prevent startup with a fresh store.
        }
    }

    public string BaseDirectory { get; }
    public string AccountsPath => Path.Combine(BaseDirectory, "accounts.json");
    public string SettingsPath => Path.Combine(BaseDirectory, "settings.json");
    public string DiscoveryPath => Path.Combine(BaseDirectory, "discovery.json");
    public string ServersPath => Path.Combine(BaseDirectory, "servers.json");

    public List<UserAccount> LoadAccounts()
    {
        if (!File.Exists(AccountsPath))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(AccountsPath));
            if (!document.RootElement.TryGetProperty("accounts", out var accounts) ||
                accounts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var result = new List<UserAccount>();
            foreach (var item in accounts.EnumerateArray())
            {
                try
                {
                    var username = GetString(item, "username");
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        continue;
                    }
                    result.Add(new UserAccount
                    {
                        Id = GetStringOr(item, "id", Guid.NewGuid().ToString("N")),
                        Username = username,
                        Password = UnprotectUser(GetString(item, "password_enc")),
                        Note = GetString(item, "note"),
                        Enabled = GetBool(item, "enabled", true),
                    });
                }
                catch
                {
                    // Skip accounts that cannot be decrypted for the current user.
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    public void SaveAccounts(IEnumerable<UserAccount> accounts)
    {
        var payload = new
        {
            version = 1,
            accounts = accounts.Select(account => new
            {
                id = account.Id,
                username = account.Username,
                password_enc = ProtectUser(account.Password),
                note = account.Note,
                enabled = account.Enabled,
            }).ToList(),
        };
        WriteFile(AccountsPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public bool UpgradePasswordProtection()
    {
        if (!File.Exists(AccountsPath))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(AccountsPath));
            if (!document.RootElement.TryGetProperty("accounts", out var accounts) ||
                accounts.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            var needsUpgrade = false;
            var count = 0;
            foreach (var item in accounts.EnumerateArray())
            {
                count++;
                var encrypted = GetString(item, "password_enc");
                if (encrypted.StartsWith("dpapi:", StringComparison.Ordinal))
                {
                    needsUpgrade = true;
                }
            }
            if (!needsUpgrade)
            {
                return false;
            }
            var loaded = LoadAccounts();
            if (loaded.Count != count)
            {
                return false;
            }
            SaveAccounts(loaded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public UserAccount? ActiveAccount(IEnumerable<UserAccount>? accounts = null)
    {
        var settings = LoadSettings();
        var list = accounts?.ToList() ?? LoadAccounts();
        return list.FirstOrDefault(account => account.Id == settings.ActiveAccountId) ??
               list.FirstOrDefault(account => account.Enabled) ??
               list.FirstOrDefault();
    }

    public UserSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return new UserSettings();
        }
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(
                               File.ReadAllText(SettingsPath), JsonOptions)
                           ?? new UserSettings();
            settings.Debug ??= new DebugSettings();
            return settings;
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void SaveSettings(UserSettings settings)
    {
        settings.Debug ??= new DebugSettings();
        WriteFile(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public DebugSettings LoadDebugSettings()
    {
        return LoadSettings().Debug ?? new DebugSettings();
    }

    public void SaveDebugSettings(DebugSettings debugSettings)
    {
        var settings = LoadSettings();
        settings.Debug = debugSettings;
        SaveSettings(settings);
    }

    public DiscoveryInfo? LoadDiscovery()
    {
        if (!File.Exists(DiscoveryPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<DiscoveryInfo>(
                File.ReadAllText(DiscoveryPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDiscovery(
        string interfaceName,
        string description,
        string mac,
        string serverMac)
    {
        var info = new DiscoveryInfo
        {
            Interface = interfaceName,
            Description = description,
            Mac = mac,
            ServerMac = serverMac,
            UpdatedAt = DateTimeOffset.Now.ToString("O"),
        };
        WriteFile(DiscoveryPath, JsonSerializer.Serialize(info, JsonOptions));
    }

    public void ClearDiscovery()
    {
        try
        {
            File.Delete(DiscoveryPath);
        }
        catch
        {
            // Ignore.
        }
    }

    public List<ServerRecord> LoadServers()
    {
        if (!File.Exists(ServersPath))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ServersPath));
            if (!document.RootElement.TryGetProperty("servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var result = new List<ServerRecord>();
            foreach (var item in servers.EnumerateArray())
            {
                var mac = PrivateTail.NormalizeMac(GetString(item, "mac"));
                if (mac.Length == 0)
                {
                    continue;
                }
                result.Add(new ServerRecord
                {
                    Mac = mac,
                    Name = GetString(item, "name"),
                    Note = GetString(item, "note"),
                    Interface = GetString(item, "interface"),
                    Description = GetString(item, "description"),
                    FirstSeen = GetString(item, "first_seen"),
                    LastSeen = GetString(item, "last_seen"),
                    LastSuccess = GetString(item, "last_success"),
                    SuccessCount = (int)(GetDouble(item, "success_count") ?? 0),
                });
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    public void SaveServers(IEnumerable<ServerRecord> servers)
    {
        var payload = new
        {
            version = 1,
            servers = servers.ToList(),
        };
        WriteFile(ServersPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public void UpsertServer(
        string mac,
        string interfaceName,
        string description,
        bool success)
    {
        var normalized = PrivateTail.NormalizeMac(mac);
        if (normalized.Length == 0)
        {
            return;
        }
        var stamp = DateTimeOffset.Now.ToString("O");
        var records = LoadServers();
        var record = records.FirstOrDefault(item => item.Mac == normalized);
        if (record is null)
        {
            record = new ServerRecord
            {
                Mac = normalized,
                FirstSeen = stamp,
            };
            records.Add(record);
        }
        record.LastSeen = stamp;
        if (!string.IsNullOrWhiteSpace(interfaceName))
        {
            record.Interface = interfaceName;
        }
        if (!string.IsNullOrWhiteSpace(description))
        {
            record.Description = description;
        }
        if (success)
        {
            record.SuccessCount++;
            record.LastSuccess = stamp;
        }
        SaveServers(records);
    }

    public static string ProtectUser(string password)
    {
        var raw = Encoding.UTF8.GetBytes(password);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(
                raw,
                null,
                DataProtectionScope.LocalMachine);
            return "machine-dpapi:" + Convert.ToBase64String(encrypted);
        }
        return "plain:" + Convert.ToBase64String(raw);
    }

    public static string UnprotectUser(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        if (value.StartsWith("machine-dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                return "";
            }
            var raw = Convert.FromBase64String(value["machine-dpapi:".Length..]);
            var plain = ProtectedData.Unprotect(
                raw,
                null,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        if (value.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                return "";
            }
            var raw = Convert.FromBase64String(value[6..]);
            var plain = ProtectedData.Unprotect(
                raw,
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        if (value.StartsWith("plain:", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value[6..]));
        }
        return value;
    }

    private void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort.
            }
        }
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

    private static bool GetBool(JsonElement element, string name, bool fallback)
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

    private static double? GetDouble(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }
}
