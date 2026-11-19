using System.Security.Cryptography;
using System.Text;
using RuijieCampusAuth.Core;

namespace RuijieCampusAuth.Core.Tests;

public class PrivateTailTests
{
    [Fact]
    public void TailMatchesPythonReference()
    {
        var network = new NetworkInfo(
            Ip: "172.18.28.6",
            Netmask: "255.255.128.0",
            Gateway: "172.18.0.1",
            Dhcp: "172.18.0.1",
            Ipv6: new byte[16],
            IpString: "172.18.28.6",
            StaticId: Encoding.ASCII.GetBytes("Static:AB45A862"));
        var options = new TailOptions
        {
            Nonce = Enumerable.Repeat((byte)'A', 32).ToArray(),
            Fingerprint = Enumerable.Repeat((byte)'B', 128).ToArray(),
            ServiceName = Encoding.GetEncoding("GB18030").GetBytes("电信宽带服务"),
        };
        var tail = PrivateTail.BuildTail(
            Encoding.ASCII.GetBytes("user"),
            Encoding.ASCII.GetBytes("pass"),
            "aa:bb:cc:dd:ee:ff",
            network,
            new byte[16],
            options);

        Assert.Equal(569, tail.Length);
        Assert.Equal(
            "779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5",
            Convert.ToHexString(SHA256.HashData(tail)).ToLowerInvariant());
    }

    [Fact]
    public void Md5ResponseMatchesPythonLayout()
    {
        var response = Eapol.Md5Response(
            7,
            Encoding.ASCII.GetBytes("pass"),
            Enumerable.Repeat((byte)0x11, 16).ToArray(),
            Encoding.ASCII.GetBytes("user"));
        Assert.Equal(2, response[0]);
        Assert.Equal(7, response[1]);
        Assert.Equal(4, response[4]);
        Assert.Equal(16, response[5]);
        Assert.Equal(26, response.Length);
    }

    [Fact]
    public void ControlFrameHasPrivateTailOutsideEapolLength()
    {
        var tail = new byte[569];
        var frame = Eapol.BuildControl(
            "aa:bb:cc:dd:ee:ff",
            Eapol.RuijieGroup,
            1,
            tail);
        Assert.Equal(587, frame.Length);
        Assert.Equal(0x88, frame[12]);
        Assert.Equal(0x8E, frame[13]);
        Assert.Equal(0, frame[16]);
        Assert.Equal(0, frame[17]);
    }
}
