using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

public enum AuthMethodKind
{
    EapMd5,
    PeapV0,
    PeapV1,
    EapTls,
}

/// <summary>
/// Resolves the configured auth_mode into the concrete authentication path,
/// and turns dhcp_mode into before/after renewal decisions. Defined values
/// match the original client's SuConfig AuthMode capability names.
/// </summary>
public static class AuthMethodOptions
{
    public static AuthMethodKind Parse(string? value)
    {
        var text = (value ?? "").Trim().Replace("-", "").Replace("_", "");
        if (text.Length == 0)
        {
            return AuthMethodKind.EapMd5;
        }
        var upper = text.ToUpperInvariant();
        return upper switch
        {
            "EAPMD5" or "MD5" => AuthMethodKind.EapMd5,
            "PEAP" or "PEAPV0" or "PEAPMSCHAPV2" => AuthMethodKind.PeapV0,
            "PEAPV1" => AuthMethodKind.PeapV1,
            "EAPTLS" or "TLS" => AuthMethodKind.EapTls,
            _ => AuthMethodKind.EapMd5,
        };
    }

    public static EapTunnelMode ToTunnelMode(AuthMethodKind kind)
    {
        return kind switch
        {
            AuthMethodKind.PeapV1 => EapTunnelMode.Peap,
            AuthMethodKind.EapTls => EapTunnelMode.EapTls,
            _ => EapTunnelMode.Peap,
        };
    }

    public static string Display(AuthMethodKind kind)
    {
        return kind switch
        {
            AuthMethodKind.PeapV0 => "PEAPv0 + MSCHAPv2",
            AuthMethodKind.PeapV1 => "PEAPv1 + MSCHAPv2",
            AuthMethodKind.EapTls => "EAP-TLS",
            _ => "EAP-MD5",
        };
    }

    /// <summary>dhcp_mode == before：认证前先续租。</summary>
    public static bool WantsDhcpBefore(string? dhcpMode)
    {
        return NormalizeDhcp(dhcpMode) == "before";
    }

    /// <summary>dhcp_mode == after：认证成功后先续租。</summary>
    public static bool WantsDhcpAfter(string? dhcpMode)
    {
        return NormalizeDhcp(dhcpMode) == "after";
    }

    private static string NormalizeDhcp(string? dhcpMode)
    {
        return (dhcpMode ?? "").Trim().ToLowerInvariant();
    }
}
