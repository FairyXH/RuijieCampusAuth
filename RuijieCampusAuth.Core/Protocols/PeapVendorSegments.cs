using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

/// <summary>
/// PEAP/EAP-Host vendor segments discovered in 8021x.exe. Segment layout is
/// [type][length][payload]; the outer EAPOL wrapper is separate.
/// </summary>
public static class PeapVendorSegments
{
    public static readonly byte[] CredentialIv =
        Encoding.ASCII.GetBytes("!jierui9002pmsus");

    public static byte[] Credential(
        ReadOnlySpan<byte> username,
        ReadOnlySpan<byte> password)
    {
        var cipher = PrivateTail.EncryptCredentials(
            username.ToArray(),
            password.ToArray(),
            CredentialIv);
        return [0x5F, (byte)cipher.Length, .. cipher];
    }

    public static byte[] Mac(string mac)
    {
        var normalized = PrivateTail.NormalizeMac(mac);
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"invalid MAC address: {mac}", nameof(mac));
        }
        var bytes = Convert.FromHexString(normalized.Replace(":", ""));
        return [0x2D, 0x06, .. bytes];
    }

    public static byte[] ServiceName(ReadOnlySpan<byte> service, bool wide = false)
    {
        var length = wide ? 0x40 : 0x20;
        var payload = new byte[length];
        service[..Math.Min(service.Length, length)].CopyTo(payload);
        return [0x39, (byte)length, .. payload];
    }

    public static byte[] DhcpPhase(byte phase)
    {
        return phase == 0 ? [] : [0x63, 0x01, phase];
    }

    public static byte[] ClientVersionSegment(ReadOnlySpan<byte> payload)
    {
        return [0x11, 0x24, .. payload[..Math.Min(payload.Length, 0x24)]];
    }
}
