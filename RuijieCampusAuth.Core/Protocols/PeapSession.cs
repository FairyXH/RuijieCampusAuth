using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RuijieCampusAuth.Core.Protocols;

public enum EapTunnelMode
{
    Peap,
    EapTls,
}

public sealed class PeapOptions
{
    public EapTunnelMode Mode { get; set; } = EapTunnelMode.Peap;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string TargetHost { get; set; } = "";
    public bool VerifyServerCertificate { get; set; } = true;
    public X509Certificate2? ClientCertificate { get; set; }
    public SslProtocols Protocols { get; set; } = SslProtocols.Tls12;
    public int MaxFragmentBytes { get; set; } = 1200;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public byte[]? IdentityTail { get; set; }
}

public sealed record PeapResult(
    bool Ok,
    string Message,
    string? TlsProtocol,
    string? ServerSubject,
    IReadOnlyList<string> Steps,
    string? OuterFailure);

public interface IPeapTransport : IDisposable
{
    string SourceMac { get; }
    string? ServerMac { get; }
    bool OuterSuccess { get; }
    string? OuterFailure { get; }
    byte[] Start(string identity, byte[]? identityTail);
    byte[] Exchange(byte[] eapPacket, byte[]? tail);
}

public static class PeapSession
{
    private const byte FlagLength = 0x80;
    private const byte FlagMore = 0x40;
    private const byte FlagStart = 0x20;

    public static PeapResult Run(IPeapTransport transport, PeapOptions options)
    {
        var steps = new List<string>();
        var deadline = DateTimeOffset.UtcNow + options.Timeout;
        EapTlsStream? stream = null;
        SslStream? ssl = null;
        CancellationTokenSource? pumpCts = null;
        Task? pump = null;
        try
        {
            var first = transport.Start(options.UserName, options.IdentityTail);
            steps.Add($"server={transport.ServerMac ?? "-"}");
            if (!IsTunnelRequest(first, options.Mode))
            {
                return Failure("first server packet is not a PEAP/EAP-TLS request", steps);
            }

            stream = new EapTlsStream();
            ssl = new SslStream(
                stream,
                leaveInnerStreamOpen: true,
                (_, certificate, chain, errors) =>
                    Validate(options, certificate, chain, errors));
            pumpCts = new CancellationTokenSource(options.Timeout);
            var pumpToken = pumpCts.Token;
            pump = Task.Run(
                () => PumpAsync(transport, stream, options, pumpToken),
                pumpToken);

            FeedFirstPayload(transport, stream, first, steps, options);

            var authentication = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(options.TargetHost)
                    ? "localhost"
                    : options.TargetHost,
                EnabledSslProtocols = options.Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            if (options.ClientCertificate is not null)
            {
                authentication.ClientCertificates =
                    new X509CertificateCollection { options.ClientCertificate };
            }
            using (var token = new CancellationTokenSource(
                       Remaining(deadline)))
            {
                ssl.AuthenticateAsClientAsync(authentication, token.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            steps.Add($"tls={ssl.SslProtocol} cipher={ssl.NegotiatedCipherSuite}");
            var serverCertificate = ssl.RemoteCertificate as X509Certificate2;
            steps.Add($"server-cert={serverCertificate?.Subject ?? "-"}");

            if (options.Mode == EapTunnelMode.EapTls)
            {
                WaitOuterSuccess(transport, deadline);
                return new PeapResult(
                    transport.OuterSuccess,
                    transport.OuterSuccess ? "EAP-TLS 认证成功" : "EAP-TLS 未收到外层成功",
                    ssl.SslProtocol.ToString(),
                    serverCertificate?.Subject,
                    steps,
                    transport.OuterFailure);
            }

            var phase2 = RunPhase2(ssl, options, steps, deadline);
            if (!phase2)
            {
                return new PeapResult(
                    false,
                    "PEAP phase2 失败",
                    ssl.SslProtocol.ToString(),
                    serverCertificate?.Subject,
                    steps,
                    transport.OuterFailure);
            }
            WaitOuterSuccess(transport, deadline);
            return new PeapResult(
                transport.OuterSuccess,
                transport.OuterSuccess ? "PEAP/MSCHAPv2 认证成功" : "PEAP 未收到外层成功",
                ssl.SslProtocol.ToString(),
                serverCertificate?.Subject,
                steps,
                transport.OuterFailure);
        }
        catch (Exception ex)
        {
            return new PeapResult(
                false,
                ex.Message,
                ssl?.SslProtocol.ToString(),
                (ssl?.RemoteCertificate as X509Certificate2)?.Subject,
                steps,
                transport.OuterFailure);
        }
        finally
        {
            pumpCts?.Cancel();
            try
            {
                pump?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Cleanup timeout.
            }
            ssl?.Dispose();
            stream?.Dispose();
            pumpCts?.Dispose();
        }
    }

    private static void PumpAsync(
        IPeapTransport transport,
        EapTlsStream stream,
        PeapOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var outgoing = stream.TakeOutgoing(50);
                if (outgoing is null)
                {
                    if (transport.OuterSuccess || transport.OuterFailure is not null)
                    {
                        stream.CompleteInput();
                        return;
                    }
                    continue;
                }
                var next = SendClientPayload(transport, outgoing, options);
                if (next is null)
                {
                    stream.CompleteInput();
                    return;
                }
                if (transport.OuterSuccess || transport.OuterFailure is not null)
                {
                    stream.CompleteInput();
                    return;
                }
                var payload = ReceiveServerPayload(transport, next, options);
                if (payload is null)
                {
                    stream.CompleteInput();
                    return;
                }
                stream.Feed(payload);
            }
        }
        catch
        {
            stream.CompleteInput();
        }
    }

    private static byte[]? SendClientPayload(
        IPeapTransport transport,
        byte[] tlsData,
        PeapOptions options)
    {
        var offset = 0;
        var first = true;
        var max = Math.Max(200, options.MaxFragmentBytes);
        byte[]? lastServer = null;
        while (offset < tlsData.Length)
        {
            var length = Math.Min(max, tlsData.Length - offset);
            var last = offset + length >= tlsData.Length;
            var flags = (byte)0;
            if (first)
            {
                flags |= FlagLength;
            }
            if (!last)
            {
                flags |= FlagMore;
            }
            var header = first ? 4 : 0;
            var payload = new byte[1 + header + length];
            payload[0] = flags;
            if (first)
            {
                payload[1] = (byte)(tlsData.Length >> 24);
                payload[2] = (byte)(tlsData.Length >> 16);
                payload[3] = (byte)(tlsData.Length >> 8);
                payload[4] = (byte)tlsData.Length;
            }
            Array.Copy(tlsData, offset, payload, 1 + header, length);
            var eap = BuildResponse(options.Mode, payload);
            lastServer = transport.Exchange(eap, options.IdentityTail);
            offset += length;
            first = false;
            if (transport.OuterSuccess || transport.OuterFailure is not null)
            {
                return lastServer;
            }
        }
        return lastServer;
    }

    private static byte[]? ReceiveServerPayload(
        IPeapTransport transport,
        byte[] firstPacket,
        PeapOptions options)
    {
        var assembled = new MemoryStream();
        var packet = firstPacket;
        while (true)
        {
            if (!TryParseTunnel(packet, options.Mode, out var flags, out var data))
            {
                return null;
            }
            assembled.Write(data);
            if ((flags & FlagMore) == 0)
            {
                return assembled.ToArray();
            }
            var ack = BuildResponse(options.Mode, [0x00]);
            packet = transport.Exchange(ack, options.IdentityTail);
            if (transport.OuterSuccess || transport.OuterFailure is not null)
            {
                return null;
            }
        }
    }

    private static void FeedFirstPayload(
        IPeapTransport transport,
        EapTlsStream stream,
        byte[] packet,
        List<string> steps,
        PeapOptions options)
    {
        var payload = ReceiveServerPayload(transport, packet, options);
        if (payload is { Length: > 0 })
        {
            steps.Add($"server-tls-fragment={payload.Length}");
            stream.Feed(payload);
        }
        else
        {
            steps.Add("server-start");
        }
    }

    private static bool RunPhase2(
        SslStream ssl,
        PeapOptions options,
        List<string> steps,
        DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var packet = ReadEapPacket(ssl, Remaining(deadline));
            if (packet.Length < 4)
            {
                return false;
            }
            if (packet[0] == 3)
            {
                steps.Add("phase2-eap-success");
                return true;
            }
            if (packet[0] == 4)
            {
                steps.Add("phase2-eap-failure");
                return false;
            }
            if (packet[0] != 1 || packet.Length < 5)
            {
                steps.Add($"phase2-unexpected-code={packet[0]}");
                return false;
            }
            var type = packet[4];
            if (type == 1)
            {
                var identity = System.Text.Encoding.UTF8.GetBytes(options.UserName);
                var response = Eapol.IdentityResponse(packet[1], identity);
                ssl.Write(response);
                ssl.Flush();
                steps.Add("phase2-identity");
                continue;
            }
            if (type == 26)
            {
                if (!MschapV2.TryParseChallenge(packet, out var challenge, out var error))
                {
                    if (MschapV2.IsSuccess(packet))
                    {
                        steps.Add("phase2-mschap-success");
                        continue;
                    }
                    steps.Add($"phase2-mschap-error={error}");
                    return false;
                }
                var peerChallenge = System.Security.Cryptography.RandomNumberGenerator
                    .GetBytes(16);
                var response = MschapV2.BuildResponse(
                    challenge,
                    options.UserName,
                    options.Password,
                    peerChallenge);
                ssl.Write(response);
                ssl.Flush();
                steps.Add("phase2-mschap-response");
                continue;
            }
            steps.Add($"phase2-unsupported-eap-type={type}");
            return false;
        }
        return false;
    }

    private static byte[] ReadEapPacket(SslStream ssl, TimeSpan timeout)
    {
        using var token = new CancellationTokenSource(timeout);
        var header = new byte[4];
        ReadExact(ssl, header, token.Token);
        var length = (header[2] << 8) | header[3];
        if (length < 4)
        {
            return header;
        }
        var packet = new byte[length];
        header.CopyTo(packet, 0);
        ReadExact(ssl, packet, 4, token.Token);
        return packet;
    }

    private static void ReadExact(
        SslStream ssl,
        byte[] buffer,
        int startOffset,
        CancellationToken token)
    {
        var offset = startOffset;
        while (offset < buffer.Length)
        {
            var read = ssl.ReadAsync(buffer.AsMemory(offset), token)
                .GetAwaiter()
                .GetResult();
            if (read <= 0)
            {
                throw new EndOfStreamException("TLS stream closed");
            }
            offset += read;
        }
    }

    private static void ReadExact(SslStream ssl, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = ssl.ReadAsync(buffer.AsMemory(offset), token)
                .GetAwaiter()
                .GetResult();
            if (read <= 0)
            {
                throw new EndOfStreamException("TLS stream closed");
            }
            offset += read;
        }
    }

    private static void WaitOuterSuccess(IPeapTransport transport, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline &&
               !transport.OuterSuccess &&
               transport.OuterFailure is null)
        {
            Thread.Sleep(50);
        }
    }

    private static bool Validate(
        PeapOptions options,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (!options.VerifyServerCertificate)
        {
            return true;
        }
        if (errors != SslPolicyErrors.None || certificate is null)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(options.TargetHost) &&
            certificate is X509Certificate2 certificate2)
        {
            var host = options.TargetHost;
            return certificate2.Subject.Contains(
                host,
                StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private static byte[] BuildResponse(EapTunnelMode mode, byte[] payload)
    {
        var type = mode == EapTunnelMode.Peap ? (byte)25 : (byte)13;
        var length = 5 + payload.Length;
        var eap = new byte[length];
        eap[0] = 2;
        eap[1] = 0; // Identifier is replaced by the transport.
        eap[2] = (byte)(length >> 8);
        eap[3] = (byte)length;
        eap[4] = type;
        payload.CopyTo(eap, 5);
        return eap;
    }

    private static bool IsTunnelRequest(byte[] packet, EapTunnelMode mode)
    {
        var type = mode == EapTunnelMode.Peap ? (byte)25 : (byte)13;
        return packet.Length >= 5 && packet[0] == 1 && packet[4] == type;
    }

    private static bool TryParseTunnel(
        byte[] packet,
        EapTunnelMode mode,
        out byte flags,
        out byte[] data)
    {
        flags = 0;
        data = [];
        var type = mode == EapTunnelMode.Peap ? (byte)25 : (byte)13;
        if (packet.Length < 6 || packet[0] != 1 || packet[4] != type)
        {
            return false;
        }
        flags = packet[5];
        var offset = 6;
        if ((flags & FlagLength) != 0)
        {
            offset += 4;
        }
        if (offset > packet.Length)
        {
            return false;
        }
        data = packet[offset..];
        return true;
    }

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static PeapResult Failure(string message, List<string> steps)
    {
        return new PeapResult(false, message, null, null, steps, null);
    }
}
