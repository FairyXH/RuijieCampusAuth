using System.Collections.Concurrent;
using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

public sealed class PcapEapTransport : IPeapTransport
{
    private readonly string _interface;
    private readonly EapTunnelMode _mode;
    private readonly bool _logoffFirst;
    private readonly int _repeats;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;
    private PcapDevice? _device;
    private BlockingCollection<byte[]>? _received;
    private byte _lastIdentifier;

    public PcapEapTransport(
        string interfaceName,
        EapTunnelMode mode,
        bool logoffFirst = true,
        int repeats = 3,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        _interface = interfaceName;
        _mode = mode;
        _logoffFirst = logoffFirst;
        _repeats = Math.Max(1, repeats);
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _log = log;
        var nic = PrivateTail.FindInterface(interfaceName)
                  ?? throw new ArgumentException($"interface not found: {interfaceName}");
        SourceMac = PrivateTail.NormalizeMac(nic.GetPhysicalAddress().ToString());
        if (SourceMac.Length == 0)
        {
            throw new InvalidOperationException("interface has no MAC address");
        }
    }

    public string SourceMac { get; }
    public string? ServerMac { get; private set; }
    public bool OuterSuccess { get; private set; }
    public string? OuterFailure { get; private set; }

    public byte[] Start(string identity, byte[]? identityTail)
    {
        _received = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        _device = new PcapDevice(_interface);
        _device.PacketReceived += packet =>
        {
            if (_received is not null && !_received.IsAddingCompleted)
            {
                try
                {
                    _received.Add(packet);
                }
                catch (InvalidOperationException)
                {
                }
            }
        };
        Thread.Sleep(300);
        if (_logoffFirst)
        {
            SendControl(2, identityTail);
            Thread.Sleep(100);
        }
        SendControl(1, identityTail);

        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (true)
        {
            var packet = NextEap(deadline);
            if (packet is null)
            {
                throw new TimeoutException("no EAP identity request");
            }
            if (packet[0] == Eapol.EapSuccess)
            {
                OuterSuccess = true;
                throw new InvalidOperationException("server sent EAP-Success before PEAP start");
            }
            if (packet[0] == Eapol.EapFailure)
            {
                OuterFailure = "EAP-Failure before PEAP start";
                throw new InvalidOperationException(OuterFailure);
            }
            if (packet[0] == Eapol.EapRequest &&
                packet.Length >= 5 &&
                packet[4] == Eapol.EapIdentity)
            {
                _log?.Invoke("收到 Identity 请求，提交用户身份");
                var response = Eapol.IdentityResponse(
                    packet[1],
                    Encoding.UTF8.GetBytes(identity));
                SendEap(Concat(response, identityTail));
                break;
            }
        }

        var tunnelType = _mode == EapTunnelMode.Peap ? (byte)25 : (byte)13;
        while (true)
        {
            var packet = NextEap(deadline);
            if (packet is null)
            {
                throw new TimeoutException("no PEAP/EAP-TLS request");
            }
            if (packet[0] == Eapol.EapSuccess)
            {
                OuterSuccess = true;
                return packet;
            }
            if (packet[0] == Eapol.EapFailure)
            {
                OuterFailure = "EAP-Failure";
                return packet;
            }
            if (packet[0] == Eapol.EapRequest &&
                packet.Length >= 5 &&
                packet[4] == tunnelType)
            {
                _log?.Invoke($"收到 {(_mode == EapTunnelMode.Peap ? "PEAP" : "EAP-TLS")} 请求");
                return packet;
            }
        }
    }

    public byte[] Exchange(byte[] eapPacket, byte[]? tail)
    {
        eapPacket[1] = _lastIdentifier;
        SendEap(Concat(eapPacket, tail));
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (true)
        {
            var packet = NextEap(deadline);
            if (packet is null)
            {
                throw new TimeoutException("no server EAP response");
            }
            if (packet[0] == Eapol.EapSuccess)
            {
                OuterSuccess = true;
                return packet;
            }
            if (packet[0] == Eapol.EapFailure)
            {
                OuterFailure = "EAP-Failure";
                return packet;
            }
            return packet;
        }
    }

    private byte[]? NextEap(DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (!_received!.TryTake(
                    out var frame,
                    (int)Math.Max(1, remaining.TotalMilliseconds)))
            {
                return null;
            }
            if (frame.Length < 18)
            {
                continue;
            }
            var source = FormatMac(frame.AsSpan(6, 6));
            if (string.Equals(source, SourceMac, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!Eapol.TryParseEap(frame, out _, out _, out var eap))
            {
                continue;
            }
            ServerMac ??= source;
            _lastIdentifier = eap[1];
            return eap;
        }
        return null;
    }

    private void SendEap(byte[] eap)
    {
        if (_device is null || ServerMac is null)
        {
            throw new InvalidOperationException("transport is not started");
        }
        var frame = Eapol.BuildFrame(SourceMac, ServerMac, eap, []);
        _device.SendBurst(Enumerable.Repeat(frame, _repeats));
    }

    private void SendControl(byte type, byte[]? tail)
    {
        var destination = type == 2
            ? Eapol.StandardGroup
            : Eapol.RuijieGroup;
        var frame = Eapol.BuildControl(SourceMac, destination, type, tail ?? []);
        _device!.SendBurst(Enumerable.Repeat(frame, _repeats));
    }

    private static byte[] Concat(byte[] first, byte[]? second)
    {
        if (second is null || second.Length == 0)
        {
            return first;
        }
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static string FormatMac(ReadOnlySpan<byte> bytes)
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = bytes[i].ToString("x2");
        }
        return string.Join(":", parts);
    }

    public void Dispose()
    {
        _received?.CompleteAdding();
        _device?.Dispose();
        _device = null;
        _received = null;
    }
}
