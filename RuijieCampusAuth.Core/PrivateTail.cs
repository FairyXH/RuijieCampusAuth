using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RuijieCampusAuth.Core;

public sealed record NetworkInfo(
    string Ip = "",
    string Netmask = "",
    string Gateway = "",
    string Dhcp = "",
    byte[]? Ipv6 = null,
    string IpString = "",
    byte[]? StaticId = null)
{
    public byte[] StaticIdBytes => StaticId ?? PrivateTail.DefaultStaticId;
}

public sealed record TailOptions
{
    public byte[] ClientName { get; init; } = PrivateTail.ClientName;
    public byte[] ClientVersion { get; init; } = PrivateTail.ClientVersion;
    public byte[] ClientFlag { get; init; } = PrivateTail.ClientFlag;
    public int HeaderFlag { get; init; } = 1;
    public byte[] Nonce { get; init; } = PrivateTail.RandomHex(16);
    public byte[] Fingerprint { get; init; } = PrivateTail.RandomHex(64);
    public byte[] ServiceName { get; init; } = PrivateTail.DefaultServiceName;
}

public static class PrivateTail
{
    public static readonly byte[] Marker = [0x00, 0x00, 0x13, 0x11];
    public static readonly byte[] ClientName = Encoding.ASCII.GetBytes("8021x.exe");
    public static readonly byte[] ClientVersion = [0x06, 0x56, 0x00, 0x00];
    public static readonly byte[] ClientFlag = [0x01];
    public static readonly byte[] DefaultStaticId = Encoding.ASCII.GetBytes("Static:AB45A862");
    public static readonly byte[] DefaultServiceName = CreateServiceName();
    public const byte AttrTag = 0x1A;
    private static readonly byte[] ZeroIp = [0, 0, 0, 0];
    private static readonly Regex MacPattern = new(
        "^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static PrivateTail()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static byte[] CreateServiceName()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030").GetBytes("电信宽带服务");
    }

    public static string NormalizeMac(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant().Replace('-', ':');
        if (Regex.IsMatch(text, "^[0-9a-f]{12}$"))
        {
            text = string.Join(":", Enumerable.Range(0, 6).Select(i => text.Substring(i * 2, 2)));
        }
        return MacPattern.IsMatch(text) ? text : "";
    }

    public static byte[] RandomHex(int byteCount)
    {
        var data = RandomNumberGenerator.GetBytes(byteCount);
        return Encoding.ASCII.GetBytes(Convert.ToHexString(data));
    }

    public static ushort Crc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    public static byte[] Obfuscate(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var value = (byte)~data[i];
            result[i] = (byte)(
                ((value & 0x10) >> 1) |
                ((value & 0x20) >> 3) |
                ((value & 0x40) >> 5) |
                ((value & 0x80) >> 7) |
                (2 * (value & 0x08)) |
                (8 * (value & 0x04)) |
                (32 * (value & 0x02)) |
                ((value & 0x01) << 7));
        }
        return result;
    }

    public static byte[] Pad16(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return new byte[16];
        }
        var remainder = data.Length % 16;
        if (remainder == 0)
        {
            return data.ToArray();
        }
        var result = new byte[data.Length + (16 - remainder)];
        data.CopyTo(result);
        return result;
    }

    public static byte[] EncryptCredentials(byte[] username, byte[] password, byte[] iv)
    {
        if (iv.Length != 16)
        {
            throw new ArgumentException("iv must be exactly 16 bytes", nameof(iv));
        }
        var plain = Pad16(password);
        var previous = iv;
        using var output = new MemoryStream();
        for (var offset = 0; offset < plain.Length; offset += 16)
        {
            var block = plain.AsSpan(offset, 16).ToArray();
            var seed = new byte[username.Length + 16];
            Buffer.BlockCopy(username, 0, seed, 0, username.Length);
            Buffer.BlockCopy(previous, 0, seed, username.Length, 16);
            var stream = MD5.HashData(seed);
            var cipher = new byte[16];
            for (var i = 0; i < 16; i++)
            {
                cipher[i] = (byte)(block[i] ^ stream[i]);
            }
            output.Write(cipher);
            previous = cipher;
        }
        return output.ToArray();
    }

    private static byte[] IpBytes(string value)
    {
        return IPAddress.TryParse(value, out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes()
            : ZeroIp;
    }

    private static byte[] Attribute(byte type, byte[] payload)
    {
        var total = 8 + payload.Length;
        if (total > 0xFF)
        {
            throw new ArgumentException($"attribute 0x{type:X2} is too long: {total}");
        }
        var result = new byte[2 + 4 + 2 + payload.Length];
        result[0] = AttrTag;
        result[1] = (byte)total;
        Marker.CopyTo(result, 2);
        result[6] = type;
        result[7] = (byte)(2 + payload.Length);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Header(NetworkInfo network, int flag)
    {
        var raw = new byte[21];
        Marker.CopyTo(raw, 0);
        raw[4] = (byte)(flag & 0xFF);
        IpBytes(network.Ip).CopyTo(raw, 5);
        IpBytes(network.Netmask).CopyTo(raw, 9);
        IpBytes(network.Gateway).CopyTo(raw, 13);
        IpBytes(network.Dhcp).CopyTo(raw, 17);
        var crc = Crc16Xmodem(raw);
        raw = [.. raw, (byte)(crc >> 8), (byte)crc];
        return Obfuscate(raw);
    }

    private static byte[] Padded(byte[] value, int length)
    {
        var result = new byte[length];
        Array.Copy(value, result, Math.Min(value.Length, length));
        return result;
    }

    public static byte[] BuildTail(
        byte[] username,
        byte[] password,
        string mac,
        NetworkInfo network,
        byte[] iv,
        TailOptions? options = null)
    {
        options ??= new TailOptions();
        var normalized = NormalizeMac(mac);
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"invalid MAC address: {mac}", nameof(mac));
        }
        var macBytes = Convert.FromHexString(normalized.Replace(":", ""));
        var encrypted = EncryptCredentials(username, password, iv);
        var attributes = new List<byte>();
        attributes.AddRange(Attribute(0x17, options.Nonce));
        attributes.AddRange(Attribute(0x18, new byte[4]));
        attributes.AddRange(Attribute(0x2D, macBytes));
        attributes.AddRange(Attribute(0x2F, encrypted));
        attributes.AddRange(Attribute(0x35, [0x01]));
        attributes.AddRange(Attribute(0x36, new byte[16]));
        attributes.AddRange(Attribute(0x38, Padded(network.Ipv6 ?? new byte[16], 16)));
        attributes.AddRange(Attribute(0x4E, new byte[16]));
        attributes.AddRange(Attribute(0x4D, options.Fingerprint));
        attributes.AddRange(Attribute(0x39, Padded(options.ServiceName, 32)));
        attributes.AddRange(Attribute(0x54, Padded(network.StaticIdBytes, 64)));
        attributes.AddRange(Attribute(0x62, [0x00]));
        attributes.AddRange(Attribute(0x6B, [0x00]));
        attributes.AddRange(Attribute(0x7E, [0x00, 0x00, 0x01, 0x00, 0x00, 0x0C, 0x00, 0x00]));
        attributes.AddRange(Attribute(0x70, [0x40]));
        attributes.AddRange(Attribute(0x6F, [0x00]));
        attributes.AddRange(Attribute(0x79, [0x02]));
        var ipString = string.IsNullOrWhiteSpace(network.IpString)
            ? (string.IsNullOrWhiteSpace(network.Ip) ? "0.0.0.0" : network.Ip)
            : network.IpString;
        attributes.AddRange(Attribute(0x76, Encoding.ASCII.GetBytes(ipString)));

        using var output = new MemoryStream();
        output.Write(Header(network, options.HeaderFlag));
        output.Write(Marker);
        output.Write(Padded(options.ClientName, 32));
        output.Write(options.ClientVersion);
        output.Write(options.ClientFlag);
        output.Write(Marker);
        output.Write([(byte)(attributes.Count >> 8), (byte)attributes.Count]);
        output.Write(CollectionsMarshal.AsSpan(attributes));
        return output.ToArray();
    }

    public static NetworkInfo CollectNetworkInfo(string? interfaceHint)
    {
        var nic = FindInterface(interfaceHint);
        if (nic is null)
        {
            return new NetworkInfo(IpString: "0.0.0.0");
        }
        var properties = nic.GetIPProperties();
        var unicast = properties.UnicastAddresses
            .FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork);
        var gateway = properties.GatewayAddresses
            .FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork);
        var ip = unicast?.Address.ToString() ?? "";
        var netmask = unicast?.IPv4Mask?.ToString() ?? "";
        var gatewayText = gateway?.Address.ToString() ?? "";
        return new NetworkInfo(
            Ip: ip,
            Netmask: netmask,
            Gateway: gatewayText,
            Dhcp: gatewayText,
            IpString: ip);
    }

    public static NetworkInterface? FindInterface(string? interfaceHint)
    {
        if (string.IsNullOrWhiteSpace(interfaceHint))
        {
            return null;
        }
        var hint = interfaceHint.Trim();
        var guidMatch = Regex.Match(hint, "\\{([0-9a-fA-F-]{36})\\}");
        var guid = guidMatch.Success ? guidMatch.Groups[1].Value : "";
        var mac = NormalizeMac(hint);
        return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic =>
            (!string.IsNullOrEmpty(guid) &&
             string.Equals(nic.Id.Trim('{', '}'), guid, StringComparison.OrdinalIgnoreCase)) ||
            nic.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
            (mac.Length > 0 && NormalizeMac(nic.GetPhysicalAddress().ToString()) == mac));
    }

    public static NetworkInterface? FindInterfaceByMac(string mac)
    {
        var normalized = NormalizeMac(mac);
        return string.IsNullOrEmpty(normalized)
            ? null
            : NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                nic => NormalizeMac(nic.GetPhysicalAddress().ToString()) == normalized);
    }
}
