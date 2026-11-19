namespace RuijieCampusAuth.Core.Protocols;

public static class Rc4
{
    public static byte[] Transform(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("RC4 key must not be empty", nameof(key));
        }
        var state = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[data.Length];
        int a = 0, b = 0;
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) & 0xFF;
            b = (b + state[a]) & 0xFF;
            (state[a], state[b]) = (state[b], state[a]);
            var k = state[(state[a] + state[b]) & 0xFF];
            output[i] = (byte)(data[i] ^ k);
        }
        return output;
    }
}
