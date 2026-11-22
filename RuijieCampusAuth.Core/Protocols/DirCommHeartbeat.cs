using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;

namespace RuijieCampusAuth.Core.Protocols;

/// <summary>
///     SAM direct-comm heartbeat recovered from the official 8021x.exe engine
///     (docs/protocols/dircomm-heartbeat.md). After 802.1X succeeds the
///     official client keeps a UDP channel to the SAM server: it binds
///     MSG_CLIENT_PORT (property 0x6E), sends a DES-CBC encrypted heartbeat
///     every HELLO_INTERV (0x59) to INDICATE_SERV_IP:INDICATE_PORT
///     (0x5B/0x5A) using the ENCRYPT_KEY/ENCRYPT_IV pair (0x5C/0x5D) from
///     the same authentication, and re-authenticates once the heartbeat has
///     been unanswered past the retry threshold.
/// </summary>
public static class DirCommHeartbeat
{
    private const int TableEntries = 53;
    private const int TableEntrySize = 128;
    private const int SelectorHashMultiplier = 0x2d7;
    private const int SelectorModulus = 0x35;
    private const int SelectorMagicSize = 16;
    private const byte ProtocolVersion = 2;

    /// <summary>Fallback period when the server sends no HELLO_INTERV.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>Official retry/timeout (dircomm.log: 重传 2 次, 超时 2000ms).</summary>
    public const int DefaultRetry = 2;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Unanswered beats before the re-auth decision (直通心跳超时重认证).</summary>
    public const int ReauthThreshold = 3;

    private static readonly Lazy<byte[]> Table = new(LoadTable);

    private static byte[] LoadTable()
    {
        using var stream = typeof(DirCommHeartbeat).Assembly
            .GetManifestResourceStream("RuijieCampusAuth.Protocols.dircomm-table.bin");
        if (stream is null || stream.Length != TableEntries * TableEntrySize)
        {
            throw new InvalidOperationException("dircomm-table.bin embedded resource missing");
        }
        using var reader = new BinaryReader(stream);
        return reader.ReadBytes(TableEntries * TableEntrySize);
    }

    /// <summary>
    ///     Mirrors the blob selection in sub_4a6e30: a 20-byte header of
    ///     htonl(seq) followed by the (zero) magic slot is summed,
    ///     multiplied by 0x2d7 and reduced mod 53.
    /// </summary>
    public static int SelectorIndex(uint seq)
    {
        var sum = (int)(seq >> 24) + (int)((seq >> 16) & 0xFF) +
                  (int)((seq >> 8) & 0xFF) + (int)(seq & 0xFF);
        return sum * SelectorHashMultiplier % SelectorModulus;
    }

    /// <summary>Returns the 128-byte anti-replay blob for seq.</summary>
    public static byte[] TableEntry(uint seq)
    {
        var index = SelectorIndex(seq);
        var entry = new byte[TableEntrySize];
        Array.Copy(Table.Value, index * TableEntrySize, entry, 0, TableEntrySize);
        return entry;
    }

    private static byte[] Tlv(byte type, ReadOnlySpan<byte> value)
    {
        if (value.Length > byte.MaxValue)
        {
            throw new ArgumentException($"TLV 0x{type:X2} value too long: {value.Length}");
        }
        var output = new byte[2 + value.Length];
        output[0] = type;
        output[1] = (byte)value.Length;
        value.CopyTo(output.AsSpan(2));
        return output;
    }

    /// <summary>
    ///     Builds the unencrypted heartbeat body: version 2, GBK service
    ///     name, IPv4, MAC and the selected table entry, zero-padded to a
    ///     multiple of 8 for DES.
    /// </summary>
    public static byte[] Plaintext(
        ReadOnlySpan<byte> serviceName,
        ReadOnlySpan<byte> ip,
        ReadOnlySpan<byte> mac,
        uint seq)
    {
        if (ip.Length != 4)
        {
            throw new ArgumentException("IPv4 must be 4 bytes", nameof(ip));
        }
        if (mac.Length != 6)
        {
            throw new ArgumentException("MAC must be 6 bytes", nameof(mac));
        }
        var entry = TableEntry(seq);
        using var body = new MemoryStream();
        body.Write(Tlv(0x01, [ProtocolVersion]));
        body.Write(Tlv(0x03, serviceName));
        body.Write(Tlv(0x04, ip));
        body.Write(Tlv(0x05, mac));
        body.Write(Tlv(0x14, entry));
        var plaintext = body.ToArray();
        var rest = plaintext.Length % 8;
        if (rest != 0)
        {
            Array.Resize(ref plaintext, plaintext.Length + 8 - rest);
        }
        return plaintext;
    }

    /// <summary>DES-CBC encrypts the padded plaintext (sub_4a9380 → sub_55b0d0).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, byte[] plaintext)
    {
        if (key.Length != 8 || iv.Length != 8)
        {
            throw new ArgumentException("DES key/IV must be 8 bytes");
        }
        if (plaintext.Length % 8 != 0)
        {
            throw new ArgumentException("plaintext must be 8-byte aligned");
        }
        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.None;
        des.Key = key.ToArray();
        des.IV = iv.ToArray();
        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    /// <summary>Assembles the full heartbeat datagram for seq.</summary>
    public static byte[] BuildHeartbeat(
        ReadOnlySpan<byte> serviceName,
        ReadOnlySpan<byte> ip,
        ReadOnlySpan<byte> mac,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        uint seq) =>
        Encrypt(key, iv, Plaintext(serviceName, ip, mac, seq));

    /// <summary>
    ///     Converts the INDICATE_SERV_IP private property (big-endian uint32
    ///     such as 2887125761) plus the port into an IPEndPoint.
    /// </summary>
    public static IPEndPoint ParseServerAddress(uint serverIp, long port)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        var ip = new byte[4];
        ip[0] = (byte)(serverIp >> 24);
        ip[1] = (byte)(serverIp >> 16);
        ip[2] = (byte)(serverIp >> 8);
        ip[3] = (byte)serverIp;
        return new IPEndPoint(new IPAddress(ip), (int)port);
    }

    /// <summary>
    ///     Resolves the adapter identity (IPv4 + MAC) used by the heartbeat
    ///     TLVs from the authenticating NIC.
    /// </summary>
    public static bool TryGetAdapterIdentity(
        string interfaceName,
        out byte[] ip,
        out byte[] mac)
    {
        ip = [];
        mac = [];
        var match = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            item.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) ||
            item.Description.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }
        mac = match.GetPhysicalAddress().GetAddressBytes();
        if (mac.Length != 6)
        {
            return false;
        }
        var address = match.GetIPProperties().UnicastAddresses
            .Select(item => item.Address)
            .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
        if (address is null)
        {
            return false;
        }
        ip = address.GetAddressBytes();
        return ip.Length == 4;
    }
}

/// <summary>Runtime heartbeat loop mirroring the Go dircomm.Monitor.</summary>
public sealed class DirCommHeartbeatMonitor : IDisposable
{
    private readonly int _localPort;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loop;
    private readonly Action<string>? _log;

    public DirCommHeartbeatMonitor(
        byte[] serviceName,
        byte[] ip,
        byte[] mac,
        byte[] key,
        byte[] iv,
        IPEndPoint server,
        int localPort,
        TimeSpan interval,
        int retry,
        TimeSpan timeout,
        Action<string>? log)
    {
        ServerName = serviceName;
        Ip = ip;
        Mac = mac;
        Key = key;
        Iv = iv;
        Server = server;
        _localPort = localPort;
        _log = log;
        Interval = interval <= TimeSpan.Zero ? DirCommHeartbeat.DefaultInterval : interval;
        Retry = retry <= 0 ? DirCommHeartbeat.DefaultRetry : retry;
        Timeout = timeout <= TimeSpan.Zero ? DirCommHeartbeat.DefaultTimeout : timeout;
        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public event Action? ReauthRequested;

    public byte[] ServerName { get; }
    public byte[] Ip { get; }
    public byte[] Mac { get; }
    public byte[] Key { get; }
    public byte[] Iv { get; }
    public IPEndPoint Server { get; }
    public TimeSpan Interval { get; }
    public int Retry { get; }
    public TimeSpan Timeout { get; }

    private async Task RunAsync(CancellationToken token)
    {
        var seq = 0u;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            seq++;
            if (await BeatAsync(seq, token))
            {
                if (failures > 0)
                {
                    _log?.Invoke($"SAM 心跳恢复（此前连续失败 {failures} 次）");
                }
                failures = 0;
                continue;
            }
            failures++;
            _log?.Invoke($"SAM 心跳无响应（连续 {failures} 次）");
            if (failures >= DirCommHeartbeat.ReauthThreshold)
            {
                _log?.Invoke("SAM 直通心跳超时，触发重新认证");
                ReauthRequested?.Invoke();
                return;
            }
        }
    }

    private async Task<bool> BeatAsync(uint seq, CancellationToken token)
    {
        byte[] packet;
        try
        {
            packet = DirCommHeartbeat.BuildHeartbeat(ServerName, Ip, Mac, Key, Iv, seq);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"SAM 心跳构建失败: {ex.Message}");
            return false;
        }
        // One socket per beat keeps the receive side free of a pending
        // ReceiveAsync that a previous timeout left behind.
        using var client = new UdpClient(new IPEndPoint(IPAddress.Any, _localPort));
        for (var attempt = 0; attempt <= Retry; attempt++)
        {
            try
            {
                client.Connect(Server);
                await client.SendAsync(packet, token);
                var receive = client.ReceiveAsync(token).AsTask();
                var timeout = Task.Delay(Timeout, token);
                if (await Task.WhenAny(receive, timeout) != receive)
                {
                    continue;
                }
                var reply = await receive;
                if (reply.Buffer.Length > 0)
                {
                    _log?.Invoke($"SAM 推送 {reply.Buffer.Length} 字节（未解析）");
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _log?.Invoke($"SAM 心跳发送失败: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
        _cancellation.Dispose();
    }
}
