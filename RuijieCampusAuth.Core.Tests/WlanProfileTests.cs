using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public sealed class WlanProfileTests
{
    [Fact]
    public void Profile_xml_contains_peap_mschapv2_and_escapes_ssid()
    {
        var xml = WlanProfileManager.BuildProfileXml(
            "Campus & Lab",
            "WPA2",
            "AES",
            true,
            "radius.example",
            "ca-thumb");
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<name>Campus &amp; Lab</name>", xml);
        Assert.Contains("<authentication>WPA2</authentication>", xml);
        Assert.Contains("<encryption>AES</encryption>", xml);
        Assert.Contains("<useOneX>true</useOneX>", xml);
        Assert.Contains("<Type>25</Type>", xml);
        Assert.Contains("<Type>26</Type>", xml);
        Assert.Contains("<ServerName>radius.example</ServerName>", xml);
        Assert.Contains("<connectionMode>auto</connectionMode>", xml);
    }

    [Fact]
    public void User_credential_xml_escapes_identity_and_password()
    {
        var xml = WlanProfileManager.BuildUserCredentialXml(
            "user&1",
            "p<ass>");
        Assert.StartsWith("<EapHostUserCredentials", xml);
        Assert.Contains("<eapCommon:Type>25</eapCommon:Type>", xml);
        Assert.Contains("<Identity>user&amp;1</Identity>", xml);
        Assert.Contains("<Password>p&lt;ass&gt;</Password>", xml);
    }
}
