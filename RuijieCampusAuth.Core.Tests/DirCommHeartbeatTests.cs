using System.Net;
using System.Net.Sockets;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public class DirCommHeartbeatTests
{
    private static readonly byte[] LiveKey = Convert.FromHexString("7C73533126403871");
    private static readonly byte[] LiveIv = Convert.FromHexString("4163332331215164");

    private static readonly byte[] ServiceName = "svct"u8.ToArray();
    private static readonly byte[] Ip = new byte[] { 127, 0, 0, 1 };
    private static readonly byte[] Mac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

    [Fact]
    public void SelectorIndex_MirrorsOfficialHash()
    {
        // (sum(htonl(seq) || 16 zero bytes) * 0x2d7) mod 53.
        Assert.Equal(38, DirCommHeartbeat.SelectorIndex(1));
        Assert.Equal(23, DirCommHeartbeat.SelectorIndex(2));
        Assert.Equal(47, DirCommHeartbeat.SelectorIndex(0x11223344));
    }

    [Fact]
    public void TableEntry_SlicesEmbeddedTable()
    {
        var entry = DirCommHeartbeat.TableEntry(0);
        Assert.Equal(128, entry.Length);
        Assert.Equal("a4996b0f47e71c6c", Convert.ToHexString(entry, 0, 8).ToLowerInvariant());

        // seq=1 selects entry 38, which must differ from entry 0.
        Assert.Equal(38, DirCommHeartbeat.SelectorIndex(1));
        var selected = DirCommHeartbeat.TableEntry(1);
        Assert.Equal(128, selected.Length);
        Assert.NotEqual(Convert.ToHexString(entry), Convert.ToHexString(selected));
    }

    [Fact]
    public void Plaintext_MatchesRecoveredLayout()
    {
        var body = DirCommHeartbeat.Plaintext(ServiceName, Ip, Mac, 1);
        // 01 01 02 | 03 04 svct | 04 04 7f000001 | 05 06 aabbccddeeff
        // | 14 80 <entry 38> | zero padding to 160 bytes.
        Assert.Equal(160, body.Length);
        Assert.Equal(0x01, body[0]);
        Assert.Equal(0x01, body[1]);
        Assert.Equal(0x02, body[2]);
        Assert.Equal(0x03, body[3]);
        Assert.Equal(0x04, body[4]);
        Assert.Equal((byte)'s', body[5]);
        Assert.Equal(0x04, body[9]);
        Assert.Equal(127, body[11]);
        Assert.Equal(0x05, body[15]);
        Assert.Equal(0x14, body[23]);
        Assert.Equal(0x80, body[24]);
        var entry = DirCommHeartbeat.TableEntry(1);
        Assert.True(body.AsSpan(25, 128).SequenceEqual(entry));
        Assert.True(body.AsSpan(153, 7).SequenceEqual(new byte[7]));
    }

    [Fact]
    public void Encrypt_MatchesDesCbcVector()
    {
        // Vector computed with pycryptodome for the key/IV observed in
        // dircomm.log: DES-CBC(one block 00..07) = 96dd177a6ec9671a.
        var block = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var encrypted = DirCommHeartbeat.Encrypt(LiveKey, LiveIv, block);
        Assert.Equal("96dd177a6ec9671a", Convert.ToHexString(encrypted).ToLowerInvariant());
    }

    [Fact]
    public void BuildHeartbeat_IsAlignedAndSealed()
    {
        var packet = DirCommHeartbeat.BuildHeartbeat(ServiceName, Ip, Mac, LiveKey, LiveIv, 7);
        Assert.Equal(0, packet.Length % 8);
        // The service name must not survive into the ciphertext.
        Assert.DoesNotContain("svct"u8.ToArray(), packet);
    }

    [Fact]
    public void ParseServerAddress_DecodesObservedProperty()
    {
        var endpoint = DirCommHeartbeat.ParseServerAddress(2887125761, 53);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("172.22.11.1"), 53), endpoint);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DirCommHeartbeat.ParseServerAddress(1, 0));
    }

    [Fact]
    public async Task Monitor_EchoServerStaysHealthy()
    {
        using var sam = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var samEndpoint = (IPEndPoint)sam.Client.LocalEndPoint!;
        var echo = Task.Run(async () =>
        {
            while (true)
            {
                var received = await sam.ReceiveAsync();
                await sam.SendAsync(received.Buffer, received.Buffer.Length, received.RemoteEndPoint);
            }
        });

        using var monitor = new DirCommHeartbeatMonitor(
            ServiceName, Ip, Mac, LiveKey, LiveIv, samEndpoint, 0,
            TimeSpan.FromMilliseconds(40), 1, TimeSpan.FromMilliseconds(300), null);
        var reauth = 0;
        monitor.ReauthRequested += () => reauth++;

        // Several beats against the echo server: no reauth, no dispose crash.
        await Task.Delay(600);
        Assert.Equal(0, reauth);
    }

    [Fact]
    public async Task Monitor_SilentServerTriggersReauth()
    {
        // A bound-but-silent SAM: the monitor must raise the reauth event
        // after ReauthThreshold unanswered beats.
        using var sam = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)sam.Client.LocalEndPoint!;

        using var monitor = new DirCommHeartbeatMonitor(
            ServiceName, Ip, Mac, LiveKey, LiveIv, endpoint, 0,
            TimeSpan.FromMilliseconds(30), 0, TimeSpan.FromMilliseconds(60), null);
        var triggered = new TaskCompletionSource();
        monitor.ReauthRequested += () => triggered.TrySetResult();

        var done = await Task.WhenAny(triggered.Task, Task.Delay(3000));
        Assert.Same(triggered.Task, done);
    }
}
