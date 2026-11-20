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

    /// <summary>
    /// Vector captured live from a campus authentication server
    /// (build/capture/live-auth.pcap, EAP Success frame 17, private block
    /// starting at frame offset 0x16). Every value below was cross-checked
    /// against the original 8021x.exe ParsePrivateProperty (0x537ee0)
    /// executed on the same bytes in Unicorn — see
    /// build/capture/original-parse-truth.json.
    /// </summary>
    [Fact]
    public void Parse_real_captured_server_block_matches_original_binary()
    {
        const string hex =
            "000013110018bbb6d3adcab9d3c3cfc3c3c5bbaacfc3d1a7d4bacdf8c2e7000013116900000000000000"
            + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000000000000013110100000013110101ffff3777afff6765cda1ffffffd7ff"
            + "00001311560600000000000013115a0600000035000013115b06ac160b01000013115c0a6e9bafe984d4"
            + "1102000013115d0a538bcffb93b57817000013115e0a000000006afef017000013116e060000008a0000"
            + "13117903020000131180060000000000001311980601000100";
        var payload = Convert.FromHexString(hex);

        var result = PrivateProperties.Parse(payload);

        Assert.Empty(result.Errors);
        var message = Assert.Single(result.Messages);
        Assert.Equal("欢迎使用厦门华厦学院网络", message);
        Assert.Equal(0, result.Section2Value);
        Assert.Equal(0x19594C7A, result.Section5Hash);
        Assert.Equal(20000, result.Section5IntervalMs);

        Assert.Equal(10, result.Properties.Count);

        Assert.Equal("SU_REAUTH_INTERV", result.Properties[0].Name);
        Assert.Equal(0, result.Properties[0].Number);

        Assert.Equal("INDICATE_PORT", result.Properties[1].Name);
        Assert.Equal(53, result.Properties[1].Number);

        Assert.Equal("INDICATE_SERV_IP", result.Properties[2].Name);
        Assert.Equal("ac160b01", result.Properties[2].Hex);

        Assert.Equal("ENCRYPT_KEY", result.Properties[3].Name);
        Assert.Equal("7c73533126403871", result.Properties[3].Hex);

        Assert.Equal("ENCRYPT_IV", result.Properties[4].Name);
        Assert.Equal("4163332331215164", result.Properties[4].Hex);

        Assert.Equal("SERVER_UTC_TIME", result.Properties[5].Name);
        Assert.Equal("000000006afef017", result.Properties[5].Hex);
        Assert.Equal(8, result.Properties[5].Raw.Length);

        Assert.Equal("MSG_CLIENT_PORT", result.Properties[6].Name);
        Assert.Equal(138, result.Properties[6].Number);

        Assert.Equal("DIRECT_COMM_HIGHEST_VERSION", result.Properties[7].Name);
        Assert.Equal(2, result.Properties[7].Number);

        Assert.Equal("DIRECT_COMM_HEARTBEAT_FLAGS", result.Properties[8].Name);
        Assert.Equal(0, result.Properties[8].Number);

        Assert.Equal("WIFI_CNNT_NUM_FLAGS", result.Properties[9].Name);
        Assert.Equal("01000100", result.Properties[9].Hex);

        Assert.Equal(payload.Length, result.ConsumedBytes);
    }

    [Fact]
    public void Parse_builds_sectioned_vector_with_all_field_kinds()
    {
        // Synthetic vector in the original sectioned format.
        var payload = new List<byte>();
        void Marker(byte type, byte[] value)
        {
            payload.AddRange([0x00, 0x00, 0x13, 0x11, type,
                (byte)(value.Length + 2)]);
            payload.AddRange(value);
        }
        // Section 1: message
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x00, 0x04]);
        payload.AddRange(Encoding.ASCII.GetBytes("hi\0\0"));
        // Section 2: 0x69-byte block, value 1, count 0
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x69, 0x00]);
        payload.AddRange(new byte[4]);
        payload.AddRange(new byte[100]);
        payload.Add(0);
        // Sections 3/4
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x01, 0x00]);
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x01, 0x01]);
        // Section 5: obfuscated [1311][0a][00][4B hash][4B sec][00]
        var plain = new byte[]
        {
            0x00, 0x00, 0x13, 0x11, 0x0A, 0x00,
            0x11, 0x22, 0x33, 0x44,
            0x00, 0x00, 0x00, 0x1E,
            0x00,
        };
        payload.AddRange(PrivateTail.Obfuscate(plain));
        // Properties: 4-byte number, RC4 key, 2-byte number, unknown skip
        Marker(0x56, [0x00, 0x00, 0x00, 0x3C]);
        Marker(0x5C, Rc4.Transform([0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44],
            PrivateProperties.Rc4Key));
        Marker(0x75, [0x00, 0x3C]);
        Marker(0x99, [0x7F]);

        var result = PrivateProperties.Parse(payload.ToArray());

        Assert.Empty(result.Errors);
        Assert.Equal("hi", result.Messages.Single());
        Assert.Equal(0x11223344, result.Section5Hash);
        Assert.Equal(30_000, result.Section5IntervalMs);

        Assert.Equal(4, result.Properties.Count);
        Assert.Equal(60, result.Properties[0].Number);
        Assert.Equal("aabbccdd11223344", result.Properties[1].Hex);
        Assert.Equal(60, result.Properties[2].Number);
        Assert.Equal("UNKNOWN_0x99", result.Properties[3].Name);
        Assert.Equal(0x7F, result.Properties[3].Number);
    }

    [Fact]
    public void Parse_rejects_missing_marker()
    {
        var result = PrivateProperties.Parse([0x01, 0x02, 0x03, 0x04, 0x05, 0x06]);
        Assert.Single(result.Errors);
        Assert.Empty(result.Properties);
    }

    [Fact]
    public void Parse_reports_overrun()
    {
        var payload = new byte[]
        {
            0x00, 0x00, 0x13, 0x11, 0x00, 0x10, (byte)'a',
        };
        var result = PrivateProperties.Parse(payload);
        Assert.Single(result.Errors);
    }

    /// <summary>
    /// Wraps marker-TLV properties into a minimal valid sectioned block the
    /// same way the original server packets are laid out (sections 1-5 plus
    /// the property loop).
    /// </summary>
    internal static byte[] BuildSectioned(
        IReadOnlyList<string> messages,
        params (byte Type, byte[] Value)[] properties)
    {
        var payload = new List<byte>();
        var message = string.Join("\n", messages);
        var messageBytes = Encoding.ASCII.GetBytes(message);
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x00, 0x00]);
        payload[4] = (byte)(messageBytes.Length >> 8);
        payload[5] = (byte)(messageBytes.Length & 0xFF);
        payload.AddRange(messageBytes);
        // Section 2: 0x69-byte block, value 0, count 0
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x69, 0x00]);
        payload.AddRange(new byte[4]);
        payload.AddRange(new byte[100]);
        payload.Add(0);
        // Sections 3/4
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x01, 0x00]);
        payload.AddRange([0x00, 0x00, 0x13, 0x11, 0x01, 0x01]);
        // Section 5: obfuscated [1311][0a][00][4B hash][4B sec][00]
        var plain = new byte[]
        {
            0x00, 0x00, 0x13, 0x11, 0x0A, 0x00,
            0x11, 0x22, 0x33, 0x44,
            0x00, 0x00, 0x00, 0x1E,
            0x00,
        };
        payload.AddRange(PrivateTail.Obfuscate(plain));
        // Property TLV loop
        foreach (var (type, value) in properties)
        {
            payload.AddRange([0x00, 0x00, 0x13, 0x11, type,
                (byte)(value.Length + 2)]);
            payload.AddRange(value);
        }
        return payload.ToArray();
    }
}
