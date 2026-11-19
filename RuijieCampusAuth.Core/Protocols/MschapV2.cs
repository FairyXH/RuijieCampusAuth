using System.Security.Cryptography;
using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

public static class Md4
{
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        var length = input.Length;
        var paddedLength = ((length + 8) / 64 + 1) * 64;
        var buffer = new byte[paddedLength];
        input.CopyTo(buffer);
        buffer[length] = 0x80;
        var bitLength = (ulong)length * 8;
        for (var i = 0; i < 8; i++)
        {
            buffer[paddedLength - 8 + i] = (byte)(bitLength >> (8 * i));
        }

        var x = new uint[16];
        int[] round2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
        int[] round3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
        int[] shift2 = [3, 5, 9, 13];
        int[] shift3 = [3, 9, 11, 15];
        for (var offset = 0; offset < paddedLength; offset += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = BitConverter.ToUInt32(buffer, offset + i * 4);
            }
            var aa = a;
            var bb = b;
            var cc = c;
            var dd = d;

            for (var i = 0; i < 16; i++)
            {
                var sum = x[i];
                switch (i % 4)
                {
                    case 0:
                        a = RotateLeft(a + F(b, c, d) + sum, 3);
                        break;
                    case 1:
                        d = RotateLeft(d + F(a, b, c) + sum, 7);
                        break;
                    case 2:
                        c = RotateLeft(c + F(d, a, b) + sum, 11);
                        break;
                    case 3:
                        b = RotateLeft(b + F(c, d, a) + sum, 19);
                        break;
                }
            }
            for (var i = 0; i < 16; i++)
            {
                var sum = x[round2[i]] + 0x5a827999;
                var shift = shift2[i % 4];
                switch (i % 4)
                {
                    case 0:
                        a = RotateLeft(a + G(b, c, d) + sum, shift);
                        break;
                    case 1:
                        d = RotateLeft(d + G(a, b, c) + sum, shift);
                        break;
                    case 2:
                        c = RotateLeft(c + G(d, a, b) + sum, shift);
                        break;
                    case 3:
                        b = RotateLeft(b + G(c, d, a) + sum, shift);
                        break;
                }
            }
            for (var i = 0; i < 16; i++)
            {
                var sum = x[round3[i]] + 0x6ed9eba1;
                var shift = shift3[i % 4];
                switch (i % 4)
                {
                    case 0:
                        a = RotateLeft(a + H(b, c, d) + sum, shift);
                        break;
                    case 1:
                        d = RotateLeft(d + H(a, b, c) + sum, shift);
                        break;
                    case 2:
                        c = RotateLeft(c + H(d, a, b) + sum, shift);
                        break;
                    case 3:
                        b = RotateLeft(b + H(c, d, a) + sum, shift);
                        break;
                }
            }

            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var result = new byte[16];
        BitConverter.GetBytes(a).CopyTo(result, 0);
        BitConverter.GetBytes(b).CopyTo(result, 4);
        BitConverter.GetBytes(c).CopyTo(result, 8);
        BitConverter.GetBytes(d).CopyTo(result, 12);
        return result;
    }

    private static uint RotateLeft(uint value, int bits)
    {
        return (value << bits) | (value >> (32 - bits));
    }

    private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);

    private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;
}

public sealed record MschapV2Challenge(
    byte Identifier,
    byte MschapIdentifier,
    byte[] Challenge,
    string Name);

public sealed record MschapV2Response(
    byte[] PeerChallenge,
    byte[] NtResponse,
    string UserName);

public static class MschapV2
{
    private const byte ChallengeOpcode = 1;
    private const byte ResponseOpcode = 2;
    private const byte SuccessOpcode = 3;
    private const byte FailureOpcode = 4;

    public static bool TryParseChallenge(
        ReadOnlySpan<byte> eapPacket,
        out MschapV2Challenge challenge,
        out string? error)
    {
        challenge = null!;
        error = null;
        if (eapPacket.Length < 5 + 21 || eapPacket[0] != 1 || eapPacket[4] != 26)
        {
            error = "not an EAP-MSCHAPv2 packet";
            return false;
        }
        var data = eapPacket[5..];
        if (data[0] != ChallengeOpcode || data[4] != 16)
        {
            error = "not an MSCHAPv2 challenge";
            return false;
        }
        var nameLength = data.Length - 21;
        if (nameLength < 0)
        {
            error = "challenge too short";
            return false;
        }
        challenge = new MschapV2Challenge(
            Identifier: eapPacket[1],
            MschapIdentifier: data[1],
            Challenge: data.Slice(5, 16).ToArray(),
            Name: Encoding.UTF8.GetString(data[21..]));
        return true;
    }

    public static byte[] BuildResponse(
        MschapV2Challenge challenge,
        string userName,
        string password,
        ReadOnlySpan<byte> peerChallenge)
    {
        if (peerChallenge.Length != 16)
        {
            throw new ArgumentException("peer challenge must be 16 bytes", nameof(peerChallenge));
        }
        var ntResponse = GenerateNtResponse(
            challenge.Challenge,
            peerChallenge.ToArray(),
            userName,
            password);
        var name = Encoding.UTF8.GetBytes(userName);
        const int valueSize = 49;
        var msLength = 5 + valueSize + name.Length;
        var packet = new byte[5 + msLength];
        packet[0] = 2;
        packet[1] = challenge.Identifier;
        packet[2] = (byte)((5 + msLength) >> 8);
        packet[3] = (byte)(5 + msLength);
        packet[4] = 26;
        packet[5] = ResponseOpcode;
        packet[6] = challenge.MschapIdentifier;
        packet[7] = (byte)(msLength >> 8);
        packet[8] = (byte)msLength;
        packet[9] = valueSize;
        peerChallenge.CopyTo(packet.AsSpan(10, 16));
        // Reserved 8 bytes remain zero.
        ntResponse.CopyTo(packet.AsSpan(34, 24));
        packet[58] = 0; // Flags
        name.CopyTo(packet.AsSpan(59));
        return packet;
    }

    public static byte[] GenerateNtResponse(
        byte[] authenticatorChallenge,
        byte[] peerChallenge,
        string userName,
        string password)
    {
        var challenge = ChallengeHash(peerChallenge, authenticatorChallenge, userName);
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var passwordHash = Md4.Hash(passwordBytes);
        var zPasswordHash = new byte[24];
        passwordHash.CopyTo(zPasswordHash, 0);
        var response = new byte[24];
        for (var i = 0; i < 3; i++)
        {
            var key = new byte[8];
            ExpandDesKey(zPasswordHash.AsSpan(i * 7, 7), key);
            SetOddParity(key);
            using var des = DES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            des.Key = key;
            using var encryptor = des.CreateEncryptor();
            encryptor.TransformBlock(challenge, 0, 8, response, i * 8);
        }
        return response;
    }

    public static byte[] ChallengeHash(
        ReadOnlySpan<byte> peerChallenge,
        ReadOnlySpan<byte> authenticatorChallenge,
        string userName)
    {
        var name = Encoding.UTF8.GetBytes(userName.Split('\\').Last());
        var seed = new byte[peerChallenge.Length +
                             authenticatorChallenge.Length +
                             name.Length];
        peerChallenge.CopyTo(seed);
        authenticatorChallenge.CopyTo(seed.AsSpan(peerChallenge.Length));
        name.CopyTo(seed.AsSpan(peerChallenge.Length + authenticatorChallenge.Length));
        return SHA1.HashData(seed)[..8];
    }

    public static bool IsSuccess(ReadOnlySpan<byte> eapPacket)
    {
        return eapPacket.Length >= 5 &&
               eapPacket[4] == 26 &&
               eapPacket[5] == SuccessOpcode;
    }

    public static bool IsFailure(ReadOnlySpan<byte> eapPacket)
    {
        return eapPacket.Length >= 5 &&
               eapPacket[4] == 26 &&
               eapPacket[5] == FailureOpcode;
    }

    private static void ExpandDesKey(ReadOnlySpan<byte> key7, Span<byte> key8)
    {
        key8[0] = key7[0];
        key8[1] = (byte)((key7[0] << 7) | (key7[1] >> 1));
        key8[2] = (byte)((key7[1] << 6) | (key7[2] >> 2));
        key8[3] = (byte)((key7[2] << 5) | (key7[3] >> 3));
        key8[4] = (byte)((key7[3] << 4) | (key7[4] >> 4));
        key8[5] = (byte)((key7[4] << 3) | (key7[5] >> 5));
        key8[6] = (byte)((key7[5] << 2) | (key7[6] >> 6));
        key8[7] = (byte)(key7[6] << 1);
    }

    private static void SetOddParity(byte[] key)
    {
        for (var i = 0; i < key.Length; i++)
        {
            var value = key[i] & 0xFE;
            var parity = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                parity += (value >> bit) & 1;
            }
            if (parity % 2 == 0)
            {
                value |= 1;
            }
            key[i] = (byte)value;
        }
    }
}
