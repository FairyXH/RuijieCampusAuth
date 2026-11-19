using RuijieCampusAuth.Core.Diagnostics;

namespace RuijieCampusAuth.Core.Tests;

public sealed class DiagnosticsTests
{
    [Theory]
    [InlineData("00:1a:a9:17:ff:ff", "Ruijie Networks")]
    [InlineData("00-74-9C-11-22-33", "Ruijie Networks")]
    [InlineData("aa:bb:cc:dd:ee:ff", "Unknown")]
    public void Oui_detects_ruijie_prefixes(string mac, string expected)
    {
        Assert.Equal(expected, OuiDetect.VendorOf(mac));
    }

    [Fact]
    public void Oui_rejects_invalid_mac()
    {
        Assert.Equal("Unknown", OuiDetect.VendorOf("not-a-mac"));
    }

    [Fact]
    public void Nic_inventory_lists_at_least_one_interface()
    {
        var nics = NicInventory.List();
        Assert.NotEmpty(nics);
        Assert.Contains(nics, nic => nic.Id.Length > 0);
    }

    [Fact]
    public void Dialup_detection_returns_a_list()
    {
        var detected = ProxyDetect.DetectDialup();
        Assert.NotNull(detected);
    }
}
