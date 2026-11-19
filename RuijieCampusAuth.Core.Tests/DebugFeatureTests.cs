using System.Text.Json.Nodes;
using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Debug;

namespace RuijieCampusAuth.Core.Tests;

public sealed class DebugFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ruijie-debug-" + Guid.NewGuid().ToString("N"));

    public DebugFeatureTests()
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
    public void Settings_are_disabled_by_default()
    {
        var settings = new DebugSettings();
        Assert.False(settings.Enabled);
        Assert.False(settings.IsFeatureEnabled("system-info"));
    }

    [Fact]
    public void Feature_requires_both_switches()
    {
        var settings = new DebugSettings { Enabled = true };
        Assert.False(settings.IsFeatureEnabled("system-info"));
        settings.SetFeature("system-info", true);
        Assert.True(settings.IsFeatureEnabled("system-info"));
        settings.Enabled = false;
        Assert.False(settings.IsFeatureEnabled("system-info"));
    }

    [Fact]
    public async Task Registry_blocks_when_disabled()
    {
        var store = new UserStore(_root);
        var registry = DebugFeatureFactory.Create(store, new DebugSettings());
        var result = await registry.RunAsync(
            "system-info",
            null,
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("总开关", result.Message);
    }

    [Fact]
    public async Task Registry_runs_system_info_when_enabled()
    {
        var store = new UserStore(_root);
        var settings = new DebugSettings { Enabled = true };
        settings.SetFeature("system-info", true);
        var registry = DebugFeatureFactory.Create(store, settings);
        var result = await registry.RunAsync(
            "system-info",
            null,
            CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Data);
        var data = JsonNode.Parse(result.Data!);
        Assert.NotNull(data!["runtime"]);
        Assert.Equal(
            Path.Combine(_root, "settings.json"),
            data["application"]!["settings_path"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_root, "logs", "debug.log")));
    }

    [Fact]
    public async Task Describe_marks_planned_features()
    {
        var store = new UserStore(_root);
        var settings = new DebugSettings { Enabled = true };
        settings.SetFeature("system-info", true);
        var registry = DebugFeatureFactory.Create(store, settings);
        var descriptors = await registry.DescribeAsync(CancellationToken.None);

        var system = descriptors.Single(item => item.Name == "system-info");
        Assert.Equal("available", system.State);
        Assert.True(system.Enabled);

        var peap = descriptors.Single(item => item.Name == "peap");
        Assert.Equal("feature-off", peap.State);
        Assert.Equal("P6", peap.Phase);
        Assert.False(peap.Enabled);

        var wireless = descriptors.Single(item => item.Name == "xiaorui-share");
        Assert.Equal("planned", wireless.State);
        Assert.Equal("P8", wireless.Phase);
        Assert.False(wireless.Enabled);
    }

    [Fact]
    public void Redaction_hides_secret_fields()
    {
        const string json =
            "{\"username\":\"u\",\"password\":\"p\",\"Key\":\"k\",\"iv\":\"i\"," +
            "\"note\":\"n\",\"nested\":{\"token\":\"t\",\"value\":1}}";
        var redacted = DebugRedaction.RedactJson(json);
        Assert.NotNull(redacted);
        var node = JsonNode.Parse(redacted!);
        Assert.Equal("u", node!["username"]!.GetValue<string>());
        Assert.Equal("n", node["note"]!.GetValue<string>());
        Assert.Equal("***", node["password"]!.GetValue<string>());
        Assert.Equal("***", node["Key"]!.GetValue<string>());
        Assert.Equal("***", node["iv"]!.GetValue<string>());
        Assert.Equal("***", node["nested"]!["token"]!.GetValue<string>());
        Assert.Equal(1, node["nested"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task Dhcp_feature_rejects_unknown_interface()
    {
        var store = new UserStore(_root);
        var settings = new DebugSettings { Enabled = true };
        settings.SetFeature("dhcp-client", true);
        var registry = DebugFeatureFactory.Create(store, settings);
        var result = await registry.RunAsync(
            "dhcp-client",
            "{\"interface\":\"definitely-not-a-nic\",\"operation\":\"renew\"}",
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("interface not found", result.Message);
    }

    [Fact]
    public void Settings_roundtrip_preserves_debug_block()
    {
        var store = new UserStore(_root);
        var settings = store.LoadSettings();
        settings.Debug.Enabled = true;
        settings.Debug.SetFeature("peap", true);
        settings.Debug.Endpoints.Portal = "http://127.0.0.1:8080";
        store.SaveSettings(settings);

        var loaded = store.LoadSettings();
        Assert.True(loaded.Debug.Enabled);
        Assert.True(loaded.Debug.IsFeatureEnabled("peap"));
        Assert.Equal("http://127.0.0.1:8080", loaded.Debug.Endpoints.Portal);
    }
}
