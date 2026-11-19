using System.Text;
using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

/// <summary>
/// Vectors captured by calling the original 8021x.exe functions directly in
/// Unicorn (tools/re_diff). They lock the C# implementation to the original
/// binary's output, not just to a Python reimplementation.
/// </summary>
public sealed class OriginalDifferentialTests
{
    [Theory]
    [InlineData("", 0x0000)]
    [InlineData("123456789", 0x31C3)]
    [InlineData("hello", 0xC362)]
    public void Crc16_matches_original_binary(string input, int expected)
    {
        var data = Encoding.ASCII.GetBytes(input);
        Assert.Equal((ushort)expected, PrivateTail.Crc16Xmodem(data));
    }

    [Fact]
    public void Crc16_matches_original_on_32_byte_vector()
    {
        var data = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        Assert.Equal(0xD2FF, PrivateTail.Crc16Xmodem(data));
    }

    [Fact]
    public void Obfuscate_matches_original_binary()
    {
        var first = Enumerable.Range(0, 23).Select(value => (byte)value).ToArray();
        Assert.Equal(
            "ff7fbf3fdf5f9f1fef6faf2fcf4f8f0ff777b737d75797",
            Convert.ToHexString(PrivateTail.Obfuscate(first)).ToLowerInvariant());

        byte[] second = [0, 1, 2, 3, 4, 5, 255];
        Assert.Equal(
            "ff7fbf3fdf5f00",
            Convert.ToHexString(PrivateTail.Obfuscate(second)).ToLowerInvariant());
    }

    [Fact]
    public void Credential_cipher_matches_original_binary_one_block()
    {
        var username = Encoding.ASCII.GetBytes("user");
        var iv = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var password = Encoding.ASCII.GetBytes("AAAAAAAAAAAAAAAA");
        var cipher = PrivateTail.EncryptCredentials(username, password, iv);
        Assert.Equal(
            "4062203e75a3ef4031f53933b24a0189",
            Convert.ToHexString(cipher).ToLowerInvariant());
    }

    [Fact]
    public void Credential_cipher_matches_original_binary_chained_blocks()
    {
        var username = Encoding.ASCII.GetBytes("student01");
        var iv = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var password = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var cipher = PrivateTail.EncryptCredentials(username, password, iv);
        Assert.Equal(
            "3e13a4539d3d0be514550ddd9dd3263d05e75c8299442039ad594b768b49c01d",
            Convert.ToHexString(cipher).ToLowerInvariant());
    }

    [Fact]
    public void Peap_credential_segment_matches_original_fixed_iv()
    {
        var segment = PeapVendorSegments.Credential(
            Encoding.ASCII.GetBytes("user"),
            Encoding.ASCII.GetBytes("pass"));
        Assert.Equal(
            "5f10b4b2fe1f0c17ba50d534831278f1a135",
            Convert.ToHexString(segment).ToLowerInvariant());

        var second = PeapVendorSegments.Credential(
            Encoding.ASCII.GetBytes("student01"),
            Encoding.ASCII.GetBytes("xxxxxxxxxxxxxxxx"));
        Assert.Equal(
            "5f1013b90055a7386ac235c8a73074dc03ba",
            Convert.ToHexString(second).ToLowerInvariant());
    }

    [Fact]
    public void Peap_mac_and_service_segments_match_original_layout()
    {
        Assert.Equal(
            "2d06001aa917ffff",
            Convert.ToHexString(PeapVendorSegments.Mac("00:1a:a9:17:ff:ff"))
                .ToLowerInvariant());

        var service = PeapVendorSegments.ServiceName(
            Encoding.GetEncoding("GB18030").GetBytes("电信宽带服务"));
        Assert.Equal(0x39, service[0]);
        Assert.Equal(0x20, service[1]);
        Assert.Equal(34, service.Length);

        Assert.Equal("630103", Convert.ToHexString(
            PeapVendorSegments.DhcpPhase(3)).ToLowerInvariant());
        Assert.Empty(PeapVendorSegments.DhcpPhase(0));
    }
}
