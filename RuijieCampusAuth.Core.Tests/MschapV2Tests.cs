using System.Text;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public sealed class MschapV2Tests
{
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    public void Md4_matches_vectors(string input, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(
            Md4.Hash(Encoding.ASCII.GetBytes(input))).ToLowerInvariant());
    }

    [Fact]
    public void Nt_response_matches_rfc_vector()
    {
        var authenticator = Convert.FromHexString(
            "5B5D7C7D7B3F2F3E3C2C602132262628");
        var peer = Convert.FromHexString(
            "21402324255E262A28295F2B3A337C7E");
        Assert.Equal(
            "d02e4386bce91226",
            Convert.ToHexString(MschapV2.ChallengeHash(peer, authenticator, "User"))
                .ToLowerInvariant());
        Assert.Equal(
            "41c00c584bd2d91c4017a2a12fa59f3f",
            Convert.ToHexString(Md4.Hash(Md4.Hash(
                Encoding.Unicode.GetBytes("clientPass")))).ToLowerInvariant());
        var nt = MschapV2.GenerateNtResponse(
            authenticator,
            peer,
            "User",
            "clientPass");
        Assert.Equal(
            "82309ecd8d708b5ea08faa3981cd83544233114a3d85d6df",
            Convert.ToHexString(nt).ToLowerInvariant());
    }

    [Fact]
    public void Challenge_parse_and_response_layout()
    {
        var challengeBytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var name = Encoding.UTF8.GetBytes("server");
        var data = new byte[5 + 16 + name.Length];
        data[0] = 1;
        data[1] = 9;
        data[2] = 0;
        data[3] = (byte)(5 + 16 + name.Length);
        data[4] = 16;
        challengeBytes.CopyTo(data, 5);
        name.CopyTo(data, 21);
        var eap = new byte[5 + data.Length];
        eap[0] = 1;
        eap[1] = 7;
        eap[2] = (byte)((5 + data.Length) >> 8);
        eap[3] = (byte)(5 + data.Length);
        eap[4] = 26;
        data.CopyTo(eap, 5);

        Assert.True(MschapV2.TryParseChallenge(eap, out var challenge, out var error), error);
        Assert.Equal(7, challenge.Identifier);
        Assert.Equal(9, challenge.MschapIdentifier);
        Assert.Equal("server", challenge.Name);
        Assert.Equal(challengeBytes, challenge.Challenge);

        var peer = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var response = MschapV2.BuildResponse(challenge, "user", "pass", peer);
        Assert.Equal(2, response[0]);
        Assert.Equal(7, response[1]);
        Assert.Equal(26, response[4]);
        Assert.Equal(2, response[5]);
        Assert.Equal(9, response[6]);
        Assert.Equal(49, response[9]);
        Assert.Equal(peer, response.AsSpan(10, 16).ToArray());
        Assert.Equal("user", Encoding.UTF8.GetString(response, 59, 4));
    }
}
