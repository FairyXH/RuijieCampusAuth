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
    int DeclaredLength,
    IReadOnlyList<string> Errors);

public static class PrivateProperties
{
    public const int MaximumPacketLength = 0x578;
    public const int MaximumValueLength = 0xfe;
    public static readonly byte[] Rc4Key = Encoding.ASCII.GetBytes("com.ruijie.www");

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
        var errors = new List<string>();
        if (payload.Length > MaximumPacketLength)
        {
            errors.Add(
                $"payload length {payload.Length} exceeds {MaximumPacketLength}");
            return new PrivatePropertiesResult(properties, payload.Length, errors);
        }

        var offset = 0;
        while (offset + 2 <= payload.Length)
        {
            var type = payload[offset];
            var length = payload[offset + 1];
            offset += 2;

            if (type == 0)
            {
                errors.Add($"type 0 at offset {offset - 2}");
                offset = payload.Length;
                break;
            }
            if (length > MaximumValueLength)
            {
                errors.Add($"length {length} for type {type} exceeds {MaximumValueLength}");
                offset = payload.Length;
                break;
            }
            if (offset + length > payload.Length)
            {
                errors.Add($"type {type} overruns payload at offset {offset - 2}");
                offset = payload.Length;
                break;
            }

            var value = payload.Slice(offset, length).ToArray();
            offset += length;
            properties.Add(Decode(type, value));
        }

        if (offset != payload.Length)
        {
            errors.Add($"trailing {payload.Length - offset} byte(s)");
        }
        return new PrivatePropertiesResult(properties, payload.Length, errors);
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
