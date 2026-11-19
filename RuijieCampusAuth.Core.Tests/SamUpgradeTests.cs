using RuijieCampusAuth.Core.Debug;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public sealed class SamUpgradeTests
{
    [Fact]
    public void Des_cbc_roundtrip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var key = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        var iv = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var encrypted = SamDirectClient.DesTransform(
            data,
            key,
            iv,
            DesMode.Cbc,
            encrypt: true);
        Assert.NotEqual(data, encrypted);
        var decrypted = SamDirectClient.DesTransform(
            encrypted,
            key,
            iv,
            DesMode.Cbc,
            encrypt: false);
        Assert.Equal(data, decrypted);
    }

    [Fact]
    public async Task Sam_feature_requires_host_and_port()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ruijie-sam-" + Guid.NewGuid().ToString("N"));
        var store = new UserStore(root);
        var settings = new DebugSettings { Enabled = true };
        settings.SetFeature("sam-direct", true);
        var registry = DebugFeatureFactory.Create(store, settings);
        var result = await registry.RunAsync(
            "sam-direct",
            "{\"payload_hex\":\"00001311\"}",
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("host/port", result.Message);
    }
}
