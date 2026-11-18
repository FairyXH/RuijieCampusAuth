using System.Security.Cryptography;
using System.Text;

namespace RuijieCampusAuth.Core;

public static class Eapol
{
    public const string RuijieGroup = "01:d0:f8:00:00:03";
    public const string StandardGroup = "01:80:c2:00:00:03";
    public const ushort EtherType = 0x888E;
    public const byte EapRequest = 1;
    public const byte EapResponse = 2;
    public const byte EapSuccess = 3;
    public const byte EapFailure = 4;
    public const byte EapIdentity = 1;
    public const byte EapMd5 = 4;
    private static readonly byte[] VendorMarker = [0x00, 0x00, 0x13, 0x11];

    static Eapol()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static byte[] MacBytes(string mac)
    {
        var normalized = PrivateTail.NormalizeMac(mac);
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"invalid MAC address: {mac}", nameof(mac));
        }
        return Convert.FromHexString(normalized.Replace(":", ""));
    }

    public static byte[] BuildFrame(string source, string destination, byte[] eap, byte[] tail)
    {
        var eapLength = eap.Length >= 4 ? (eap[2] << 8) | eap[3] : eap.Length;
        if (eapLength <= 0 || eapLength > eap.Length)
        {
            eapLength = eap.Length;
        }
        var frame = new byte[14 + 4 + eap.Length + tail.Length];
        MacBytes(destination).CopyTo(frame, 0);
        MacBytes(source).CopyTo(frame, 6);
        frame[12] = (byte)(EtherType >> 8);
        frame[13] = (byte)(EtherType & 0xFF);
        frame[14] = 1;
        frame[15] = 0;
        frame[16] = (byte)(eapLength >> 8);
        frame[17] = (byte)eapLength;
        eap.CopyTo(frame, 18);
        tail.CopyTo(frame, 18 + eap.Length);
        return frame;
    }

    public static byte[] BuildControl(string source, string destination, byte type, byte[] tail)
    {
        var frame = new byte[18 + tail.Length];
        MacBytes(destination).CopyTo(frame, 0);
        MacBytes(source).CopyTo(frame, 6);
        frame[12] = (byte)(EtherType >> 8);
        frame[13] = (byte)(EtherType & 0xFF);
        frame[14] = 1;
        frame[15] = type;
        frame[16] = 0;
        frame[17] = 0;
        tail.CopyTo(frame, 18);
        return frame;
    }

    public static byte[] IdentityResponse(byte identifier, byte[] identity)
    {
        var length = 5 + identity.Length;
        var eap = new byte[length];
        eap[0] = EapResponse;
        eap[1] = identifier;
        eap[2] = (byte)(length >> 8);
        eap[3] = (byte)length;
        eap[4] = EapIdentity;
        identity.CopyTo(eap, 5);
        return eap;
    }

    public static byte[] Md5Response(
        byte identifier,
        byte[] password,
        byte[] challenge,
        byte[] identity)
    {
        var seed = new byte[1 + password.Length + challenge.Length];
        seed[0] = identifier;
        password.CopyTo(seed, 1);
        challenge.CopyTo(seed, 1 + password.Length);
        var digest = MD5.HashData(seed);
        var length = 6 + digest.Length + identity.Length;
        var eap = new byte[length];
        eap[0] = EapResponse;
        eap[1] = identifier;
        eap[2] = (byte)(length >> 8);
        eap[3] = (byte)length;
        eap[4] = EapMd5;
        eap[5] = 16;
        digest.CopyTo(eap, 6);
        identity.CopyTo(eap, 6 + digest.Length);
        return eap;
    }

    public static bool TryParseEap(
        ReadOnlySpan<byte> frame,
        out byte code,
        out byte identifier,
        out byte[] eap)
    {
        code = 0;
        identifier = 0;
        eap = [];
        if (frame.Length < 18)
        {
            return false;
        }
        if (frame[12] != (byte)(EtherType >> 8) || frame[13] != (byte)(EtherType & 0xFF))
        {
            return false;
        }
        if (frame[15] != 0)
        {
            return false;
        }
        var payload = frame[18..];
        if (payload.Length < 4)
        {
            return false;
        }
        code = payload[0];
        identifier = payload[1];
        var length = (payload[2] << 8) | payload[3];
        if (length < 4 || payload.Length < length)
        {
            return false;
        }
        eap = payload[..length].ToArray();
        return true;
    }

    public static List<string> ExtractServerMessages(byte[] eap)
    {
        var messages = new List<string>();
        if (eap.Length <= 4)
        {
            return messages;
        }
        var data = eap.AsSpan(4);
        var positions = new List<int>();
        var start = 0;
        while (true)
        {
            var index = data[start..].IndexOf(VendorMarker);
            if (index < 0)
            {
                break;
            }
            var position = start + index;
            positions.Add(position);
            start = position + VendorMarker.Length;
        }
        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            var end = i + 1 < positions.Count ? positions[i + 1] : data.Length;
            var attributeType = position + 4 < data.Length ? data[position + 4] : (byte)0xFF;
            if (attributeType != 0)
            {
                continue;
            }
            var chunk = data[(position + 6)..end].ToArray();
            while (chunk.Length > 0 && chunk[^1] == 0)
            {
                chunk = chunk[..^1];
            }
            if (chunk.Length == 0)
            {
                continue;
            }
            var candidates = new List<byte[]> { chunk };
            var declared = position + 5 < data.Length ? data[position + 5] : (byte)0;
            if (declared > 0 && chunk.Length >= declared)
            {
                candidates.Insert(0, chunk[..declared]);
            }
            foreach (var candidate in candidates)
            {
                string text;
                try
                {
                    text = Encoding.GetEncoding("GB18030")
                        .GetString(candidate)
                        .Trim('\0', ' ', '\r', '\n', '\t');
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (!text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'))
                {
                    continue;
                }
                var printable = text.Count(ch => !char.IsControl(ch) && ch != '\uFFFD');
                if (text.Length > 0 && (double)printable / text.Length >= 0.8)
                {
                    if (!messages.Contains(text))
                    {
                        messages.Add(text);
                    }
                    break;
                }
            }
        }
        return messages;
    }
}
