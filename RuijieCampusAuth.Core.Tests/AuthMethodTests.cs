using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Protocols;
using Xunit;

namespace RuijieCampusAuth.Core.Tests;

public sealed class AuthMethodTests
{
    [Theory]
    [InlineData(null, AuthMethodKind.EapMd5)]
    [InlineData("", AuthMethodKind.EapMd5)]
    [InlineData("EAPMD5", AuthMethodKind.EapMd5)]
    [InlineData("eap-md5", AuthMethodKind.EapMd5)]
    [InlineData("MD5", AuthMethodKind.EapMd5)]
    [InlineData("PEAP", AuthMethodKind.PeapV0)]
    [InlineData("peapv0", AuthMethodKind.PeapV0)]
    [InlineData("PEAP-MSCHAPV2", AuthMethodKind.PeapV0)]
    [InlineData("peapv1", AuthMethodKind.PeapV1)]
    [InlineData("EAPTLS", AuthMethodKind.EapTls)]
    [InlineData("tls", AuthMethodKind.EapTls)]
    [InlineData("unknown-mode", AuthMethodKind.EapMd5)]
    public void Parse_maps_configured_auth_modes(string? value, AuthMethodKind expected)
    {
        Assert.Equal(expected, AuthMethodOptions.Parse(value));
    }

    [Fact]
    public void Unknown_mode_falls_back_to_md5_not_throws()
    {
        Assert.Equal(AuthMethodKind.EapMd5, AuthMethodOptions.Parse("SAML2"));
    }

    [Theory]
    [InlineData("before", true, false)]
    [InlineData("after", false, true)]
    [InlineData("none", false, false)]
    [InlineData(null, false, false)]
    [InlineData("BEFORE", true, false)]
    public void Dhcp_mode_gates_before_after_renewal(
        string? mode, bool before, bool after)
    {
        Assert.Equal(before, AuthMethodOptions.WantsDhcpBefore(mode));
        Assert.Equal(after, AuthMethodOptions.WantsDhcpAfter(mode));
    }

    [Fact]
    public void Profile_import_switches_auth_mode_in_settings()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "rj-authmode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new UserStore(dir);
            var profilePath = Path.Combine(dir, "peap-school.json");
            File.WriteAllText(profilePath, """
                {
                  "name": "PEAP大学",
                  "auth_mode": "PEAP",
                  "service_name": "校园网服务",
                  "static_id": "Static:PEAP01",
                  "dhcp_mode": "before",
                  "dhcp_timeout": 15
                }
                """);
            var profile = ProfileStore.Load(profilePath);

            var result = ProfileStore.Import(profile, store, profilePath);

            var settings = store.LoadSettings();
            Assert.Equal("PEAP", settings.AuthMode);
            Assert.Equal("before", settings.DhcpMode);
            Assert.Equal(15, settings.DhcpTimeout);
            Assert.Equal("校园网服务", settings.ServiceName);
            Assert.Contains("auth_mode", result.Applied);

            var method = AuthMethodOptions.Parse(settings.AuthMode);
            Assert.Equal(AuthMethodKind.PeapV0, method);
            Assert.True(AuthMethodOptions.WantsDhcpBefore(settings.DhcpMode));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
