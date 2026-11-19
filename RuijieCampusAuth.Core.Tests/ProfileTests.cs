using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Core.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ruijie-profile-" + Guid.NewGuid().ToString("N"));

    public ProfileTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Import_maps_profile_without_touching_accounts()
    {
        var store = new UserStore(_root);
        var settings = store.LoadSettings();
        settings.Interface = "test-nic";
        settings.TakeoverEnabled = true;
        store.SaveSettings(settings);
        store.SaveAccounts(
        [
            new UserAccount
            {
                Username = "user",
                Password = "pass",
                Note = "keep",
            },
        ]);

        var profile = new SchoolProfile
        {
            Name = "Example University",
            SchoolCode = "EXAM",
            AuthMode = "PEAP",
            ServiceName = "校园服务",
            StaticId = "Static:DEADBEEF",
            SelfServiceUrl = "http://self.example/",
            UpdateUrl = "http://update.example/",
            DhcpMode = "after",
            DhcpTimeout = 45,
            DhcpOption82 = true,
            AdDomain = "ad.example",
            AdTimeout = 90,
            EngineVersion = "5.0.2014.108",
        };

        var result = ProfileStore.Import(profile, store, "profiles/example.json");

        Assert.Equal("Example University", result.ProfileName);
        Assert.True(File.Exists(result.SettingsPath));
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        var loaded = store.LoadSettings();
        Assert.Equal("Example University", loaded.ProfileName);
        Assert.Equal("EXAM", loaded.SchoolCode);
        Assert.Equal("PEAP", loaded.AuthMode);
        Assert.Equal("校园服务", loaded.ServiceName);
        Assert.Equal("Static:DEADBEEF", loaded.StaticId);
        Assert.Equal("http://self.example/", loaded.SelfServiceUrl);
        Assert.Equal("http://update.example/", loaded.UpdateUrl);
        Assert.Equal("after", loaded.DhcpMode);
        Assert.Equal(45, loaded.DhcpTimeout);
        Assert.True(loaded.DhcpOption82);
        Assert.Equal("ad.example", loaded.AdDomain);
        Assert.Equal(90, loaded.AdTimeout);
        Assert.Equal("5.0.2014.108", loaded.EngineVersion);
        Assert.Equal("test-nic", loaded.Interface);
        Assert.True(loaded.TakeoverEnabled);
        Assert.Equal("http://self.example/", loaded.Debug.Endpoints.SelfService);

        var accounts = store.LoadAccounts();
        Assert.Single(accounts);
        Assert.Equal("user", accounts[0].Username);
        Assert.Equal("keep", accounts[0].Note);
    }

    [Fact]
    public void Load_rejects_empty_profile()
    {
        var path = Path.Combine(_root, "empty.json");
        File.WriteAllText(path, "{}");
        var profile = ProfileStore.Load(path);
        Assert.Equal("empty", profile.Name);
        Assert.Equal("EAPMD5", profile.AuthMode);
    }
}
