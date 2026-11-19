using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

public enum PrivatePropertyType : byte
{
    SuNewestVersion = 5,
    ProxyAvoid = 20,
    DialupAvoid = 21,
    SuUpgradeUrl = 55,
    AccountInfo = 60,
    PersonalInfo = 61,
    ServerListUpdate = 66,
    BroadcastInfo = 82,
    RejectReason = 83,
    SuReauthInterval = 86,
    HelloInterval = 89,
    IndicatePort = 90,
    IndicateServerIp = 91,
    EncryptKey = 92,
    EncryptIv = 93,
    ServerUtcTime = 94,
    RadiusServerType = 97,
    ServerListUpdateLegacy = 102,
    UserLoginUrl = 104,
    MessageClientPort = 110,
    UtrustUrl = 114,
    SmpIpAddress = 115,
    ProxyDetectKinds = 116,
    ClientMessagePeriod = 117,
    AutoOpenSsoUrl = 119,
    DirectCommunicationHighestVersion = 121,
    DirectCommunicationHeartbeatFlags = 128,
    WifiConnectionCountFlags = 152,
}

public sealed record PrivateProperty(
    byte Type,
    string Name,
    byte[] Raw,
    string? Text,
    long? Number,
    string Hex)
{
    public string DisplayValue => Text ?? Number?.ToString() ?? Hex;
}

public sealed record PrivatePropertiesResult(
    IReadOnlyList<PrivateProperty> Properties,
    IReadOnlyList<string> Messages,
    long? Section2Value,
    long? Section5Hash,
    long? Section5IntervalMs,
    int ConsumedBytes,
    IReadOnlyList<string> Errors);

/// <summary>
/// Parser for the server private-property block carried by EAP Success and
/// Failure packets, byte-compatible with 8021x.exe
/// ParsePrivateProperty (0x537ee0):
///
///   [00 00 13 11][2B len][GBK message]                    section 1
///   [00 00 13 11][2B][4B value][...][1B count][count*32]  section 2 (0x6F layout)
///   [00 00 13 11][ver][ver][4B]                           section 3 (ver==1, sub<2)
///   [00 00 13 11][ver][ver][4B]                           section 4 (ver==1, sub<2)
///   [15 obfuscated bytes]                                 section 5, de-obfuscated
///     in place: [1311][0x0A gate][sub][4B hash][4B sec][1B]
///   [00 00 13 11][type][len=value+2][value] ...           property TLV loop
///
/// The 15-byte section 5 chunk uses the same bit-reverse+invert transform as
/// the EAPOL private tail (FUN_0053df40 == FUN_0053de90), so PrivateTail.
/// Obfuscate is its own inverse.
/// </summary>
public static class PrivateProperties
{
    public const int MaximumPacketLength = 0x578;
    public const int MaximumValueLength = 0xfe;
    public static readonly byte[] Rc4Key = Encoding.ASCII.GetBytes("com.ruijie.www");
    private static readonly byte[] Marker = [0x00, 0x00, 0x13, 0x11];

    private static readonly HashSet<byte> StringTypes =
    [
        (byte)PrivatePropertyType.SuUpgradeUrl,
        (byte)PrivatePropertyType.AccountInfo,
        (byte)PrivatePropertyType.PersonalInfo,
        (byte)PrivatePropertyType.ServerListUpdate,
        (byte)PrivatePropertyType.BroadcastInfo,
        (byte)PrivatePropertyType.RejectReason,
        (byte)PrivatePropertyType.ServerListUpdateLegacy,
        (byte)PrivatePropertyType.UserLoginUrl,
        (byte)PrivatePropertyType.UtrustUrl,
    ];

    private static readonly Dictionary<byte, string> Names = new()
    {
        [(byte)PrivatePropertyType.SuNewestVersion] = "SU_NEWEST_VER",
        [(byte)PrivatePropertyType.ProxyAvoid] = "PROXY_AVOID",
        [(byte)PrivatePropertyType.DialupAvoid] = "DIALUP_AVOID",
        [(byte)PrivatePropertyType.SuUpgradeUrl] = "SU_UPGRADE_URL",
        [(byte)PrivatePropertyType.AccountInfo] = "ACCOUNT_INFO",
        [(byte)PrivatePropertyType.PersonalInfo] = "PERSIONAL_INFO",
        [(byte)PrivatePropertyType.ServerListUpdate] = "SERVER_LIST_UPDATE",
        [(byte)PrivatePropertyType.BroadcastInfo] = "BROADCASE_INFO",
        [(byte)PrivatePropertyType.RejectReason] = "REJECT_REASON",
        [(byte)PrivatePropertyType.SuReauthInterval] = "SU_REAUTH_INTERV",
        [(byte)PrivatePropertyType.HelloInterval] = "HELLO_INTERV",
        [(byte)PrivatePropertyType.IndicatePort] = "INDICATE_PORT",
        [(byte)PrivatePropertyType.IndicateServerIp] = "INDICATE_SERV_IP",
        [(byte)PrivatePropertyType.EncryptKey] = "ENCRYPT_KEY",
        [(byte)PrivatePropertyType.EncryptIv] = "ENCRYPT_IV",
        [(byte)PrivatePropertyType.ServerUtcTime] = "SERVER_UTC_TIME",
        [(byte)PrivatePropertyType.RadiusServerType] = "RADIUS_SERV_TYPE",
        [(byte)PrivatePropertyType.ServerListUpdateLegacy] = "SERVER_LIST_UPDATE_LEGACY",
        [(byte)PrivatePropertyType.UserLoginUrl] = "USER_LOGIN_URL",
        [(byte)PrivatePropertyType.MessageClientPort] = "MSG_CLIENT_PORT",
        [(byte)PrivatePropertyType.UtrustUrl] = "UTRUST_URL",
        [(byte)PrivatePropertyType.SmpIpAddress] = "SMP_IPADDRESS",
        [(byte)PrivatePropertyType.ProxyDetectKinds] = "PROXY_DECTECT_KINDS",
        [(byte)PrivatePropertyType.ClientMessagePeriod] = "CLIENT_MSG_PERIOD",
        [(byte)PrivatePropertyType.AutoOpenSsoUrl] = "AUTO_OPEN_SSO_URL",
        [(byte)PrivatePropertyType.DirectCommunicationHighestVersion] =
            "DIRECT_COMM_HIGHEST_VERSION",
        [(byte)PrivatePropertyType.DirectCommunicationHeartbeatFlags] =
            "DIRECT_COMM_HEARTBEAT_FLAGS",
        [(byte)PrivatePropertyType.WifiConnectionCountFlags] = "WIFI_CNNT_NUM_FLAGS",
    };

    public static PrivatePropertiesResult Parse(ReadOnlySpan<byte> payload)
    {
        var properties = new List<PrivateProperty>();
        var messages = new List<string>();
        var errors = new List<string>();
        var data = payload.ToArray();
        var total = data.Length;
        if (total > MaximumPacketLength)
        {
            errors.Add($"payload length {total} exceeds {MaximumPacketLength}");
            return new PrivatePropertiesResult(properties, messages, null, null,
                null, 0, errors);
        }

        var pos = 0;
        // Section 1: marker + 2B length + GBK message
        if (total < 6 || !MarkerAt(data, 0))
        {
            errors.Add("missing section 1 marker");
            return new PrivatePropertiesResult(properties, messages, null, null,
                null, 0, errors);
        }
        var messageLength = ReadUInt16BE(data, 4);
        if (messageLength + 6 > total)
        {
            errors.Add($"section 1 length {messageLength} overruns payload");
            return new PrivatePropertiesResult(properties, messages, null, null,
                null, 0, errors);
        }
        var message = DecodeText(data.AsSpan(6, messageLength).ToArray());
        if (!string.IsNullOrWhiteSpace(message))
        {
            messages.Add(message);
        }
        pos = 6 + messageLength;

        // Section 2: fixed 0x6F layout with count-prefixed 32-byte entries
        long? section2Value = null;
        if (pos + 0x6F <= total)
        {
            if (MarkerAt(data, pos))
            {
                pos += 6;
                section2Value = ReadUInt32BE(data, pos);
                var count = data[pos + 0x68];
                pos += 0x69;
                if (count * 0x20 + pos <= total)
                {
                    pos += count * 0x20;
                }
                else
                {
                    errors.Add($"section 2 count {count} overruns payload");
                    return new PrivatePropertiesResult(properties, messages,
                        section2Value, null, null, pos, errors);
                }
            }
            else
            {
                errors.Add($"missing section 2 marker at offset {pos}");
                return new PrivatePropertiesResult(properties, messages,
                    section2Value, null, null, pos, errors);
            }
        }
        else
        {
            return new PrivatePropertiesResult(properties, messages,
                section2Value, null, null, pos, errors);
        }

        // Sections 3/4: marker + ver + sub + 4B, ver must be 1 and sub < 2
        for (var section = 3; section <= 4; section++)
        {
            if (pos + 6 > total)
            {
                return new PrivatePropertiesResult(properties, messages,
                    section2Value, null, null, pos, errors);
            }
            if (!MarkerAt(data, pos))
            {
                errors.Add($"missing section {section} marker at offset {pos}");
                return new PrivatePropertiesResult(properties, messages,
                    section2Value, null, null, pos, errors);
            }
            var version = data[pos + 4];
            var sub = data[pos + 5];
            if (version == 1 && sub < 2)
            {
                pos += 6;
            }
            else
            {
                errors.Add(
                    $"section {section} version {version}/sub {sub} rejected");
                return new PrivatePropertiesResult(properties, messages,
                    section2Value, null, null, pos, errors);
            }
        }

        // Section 5: 15 obfuscated bytes, de-obfuscated in place
        long? hash = null;
        long? intervalMs = null;
        if (pos + 15 > total)
        {
            return new PrivatePropertiesResult(properties, messages,
                section2Value, null, null, pos, errors);
        }
        var plain = new byte[15];
        var obfuscated = data.AsSpan(pos, 15).ToArray();
        PrivateTail.Obfuscate(obfuscated).CopyTo(plain, 0);
        if (!MarkerAt(plain, 0))
        {
            errors.Add($"section 5 de-obfuscated marker mismatch: {Convert.ToHexString(plain)}");
            return new PrivatePropertiesResult(properties, messages,
                section2Value, null, null, pos, errors);
        }
        if (plain[4] != 0x0A)
        {
            errors.Add($"section 5 gate byte {plain[4]:X2} != 0x0A");
            return new PrivatePropertiesResult(properties, messages,
                section2Value, hash, intervalMs, pos, errors);
        }
        hash = ReadUInt32BE(plain, 6);
        intervalMs = ReadUInt32BE(plain, 10) * 1000;
        pos += 15;

        // Property TLV loop: [1311][type][len=value+2][value]
        while (pos + 6 <= total)
        {
            if (!MarkerAt(data, pos))
            {
                break;
            }
            var type = data[pos + 4];
            var declared = data[pos + 5];
            if (declared < 2)
            {
                errors.Add($"type {type} length {declared} too small at offset {pos}");
                break;
            }
            var valueLength = declared - 2;
            if (pos + 6 + valueLength > total)
            {
                errors.Add($"type {type} overruns payload at offset {pos}");
                break;
            }
            var value = data.AsSpan(pos + 6, valueLength).ToArray();
            pos += 6 + valueLength;
            properties.Add(Decode(type, value));
        }

        if (pos != total)
        {
            errors.Add($"trailing {total - pos} byte(s)");
        }
        return new PrivatePropertiesResult(properties, messages, section2Value,
            hash, intervalMs, pos, errors);
    }

    private static bool MarkerAt(byte[] data, int offset)
    {
        return offset + 4 <= data.Length &&
               data[offset] == Marker[0] && data[offset + 1] == Marker[1] &&
               data[offset + 2] == Marker[2] && data[offset + 3] == Marker[3];
    }

    private static ushort ReadUInt16BE(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static int ReadUInt32BE(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) |
               (data[offset + 2] << 8) | data[offset + 3];
    }

    private static PrivateProperty Decode(byte type, byte[] value)
    {
        var name = Names.TryGetValue(type, out var known) ? known : $"UNKNOWN_0x{type:X2}";
        var hex = Convert.ToHexString(value).ToLowerInvariant();
        string? text = null;
        long? number = null;

        if (type is (byte)PrivatePropertyType.EncryptKey or (byte)PrivatePropertyType.EncryptIv)
        {
            var decoded = Rc4.Transform(value, Rc4Key);
            hex = Convert.ToHexString(decoded).ToLowerInvariant();
            return new PrivateProperty(type, name, decoded, null, null, hex);
        }

        if (StringTypes.Contains(type))
        {
            text = DecodeText(value);
        }
        else
        {
            number = DecodeNumber(value);
        }
        return new PrivateProperty(type, name, value, text, number, hex);
    }

    private static string? DecodeText(byte[] value)
    {
        if (value.Length == 0)
        {
            return "";
        }
        try
        {
            var text = Encoding.GetEncoding("GB18030").GetString(value);
            return text.Trim('\0');
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(value);
        }
    }

    private static long? DecodeNumber(byte[] value)
    {
        return value.Length switch
        {
            1 => value[0],
            2 => (value[0] << 8) | value[1],
            4 => ((long)value[0] << 24) | ((long)value[1] << 16) |
                 ((long)value[2] << 8) | value[3],
            8 => ((long)value[0] << 56) | ((long)value[1] << 48) |
                 ((long)value[2] << 40) | ((long)value[3] << 32) |
                 ((long)value[4] << 24) | ((long)value[5] << 16) |
                 ((long)value[6] << 8) | value[7],
            _ => null,
        };
    }

    public static string NameOf(byte type)
    {
        return Names.TryGetValue(type, out var name) ? name : $"UNKNOWN_0x{type:X2}";
    }
}
