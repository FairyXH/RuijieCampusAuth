using System.Text;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public sealed class PrivatePropertiesTests
{
    [Fact]
    public void Rc4_matches_standard_vector()
    {
        var output = Rc4.Transform(
            Encoding.ASCII.GetBytes("Plaintext"),
            Encoding.ASCII.GetBytes("Key"));
        Assert.Equal(
            "bbf316e8d940af0ad3",
            Convert.ToHexString(output).ToLowerInvariant());
    }

    [Fact]
    public void Parse_decrypts_key_and_iv_and_reads_fields()
    {
        const string accountInfo = "hello";
        var plainKey = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var encryptedKey = Rc4.Transform(plainKey, PrivateProperties.Rc4Key);
        var payload = new List<byte>();
        Add(payload, (byte)PrivatePropertyType.EncryptKey, encryptedKey);
        Add(payload, (byte)PrivatePropertyType.AccountInfo,
            Encoding.ASCII.GetBytes(accountInfo));
        Add(payload, (byte)PrivatePropertyType.IndicatePort, [0x00, 0x00, 0x13, 0x89]);
        Add(payload, (byte)PrivatePropertyType.SuReauthInterval, [0x00, 0x00, 0x00, 0x1E]);
        Add(payload, 0x99, [0xAA]);

        var result = PrivateProperties.Parse(payload.ToArray());

        Assert.Empty(result.Errors);
        Assert.Equal(5, result.Properties.Count);

        var key = result.Properties[0];
        Assert.Equal("ENCRYPT_KEY", key.Name);
        Assert.Equal(Convert.ToHexString(plainKey).ToLowerInvariant(), key.Hex);

        var account = result.Properties[1];
        Assert.Equal("ACCOUNT_INFO", account.Name);
        Assert.Equal(accountInfo, account.Text);

        Assert.Equal(5001, result.Properties[2].Number);
        Assert.Equal(30, result.Properties[3].Number);
        Assert.Equal("UNKNOWN_0x99", result.Properties[4].Name);
    }

    [Fact]
    public void Parse_reports_overrun()
    {
        var result = PrivateProperties.Parse(
            new byte[] { (byte)PrivatePropertyType.AccountInfo, 8, (byte)'a' });
        Assert.Single(result.Errors);
        Assert.Empty(result.Properties);
    }

    private static void Add(List<byte> payload, byte type, byte[] value)
    {
        payload.Add(type);
        payload.Add((byte)value.Length);
        payload.AddRange(value);
    }
}
