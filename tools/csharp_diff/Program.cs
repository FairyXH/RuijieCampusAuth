// DiffRunner: reads one JSON op per line from stdin, prints one JSON result
// per line to stdout. Used by tools/re_diff/full_diff.py to compare the C#
// implementation against the original 8021x.exe functions called in Unicorn.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Protocols;

namespace DiffRunner;

internal static class Program
{
    private static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            string output;
            try
            {
                var op = JsonDocument.Parse(line).RootElement;
                output = Execute(op);
            }
            catch (Exception ex)
            {
                output = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
            Console.WriteLine(output);
        }
    }

    private static string Execute(JsonElement op)
    {
        var name = op.GetProperty("op").GetString();
        switch (name)
        {
            case "crc16":
            {
                var data = Hex(op.GetProperty("data").GetString()!);
                var crc = PrivateTail.Crc16Xmodem(data);
                return Ok(new { crc_hex = $"{crc:X4}" });
            }
            case "obfuscate":
            {
                var data = Hex(op.GetProperty("data").GetString()!);
                return Ok(new { hex = Str(PrivateTail.Obfuscate(data)) });
            }
            case "md5":
            {
                var data = Hex(op.GetProperty("data").GetString()!);
                return Ok(new { hex = Str(MD5.HashData(data)) });
            }
            case "encrypt":
            {
                var username = Encoding.ASCII.GetBytes(op.GetProperty("username").GetString()!);
                var password = op.TryGetProperty("password_hex", out var passwordHex)
                    ? Hex(passwordHex.GetString()!)
                    : Encoding.ASCII.GetBytes(op.GetProperty("password").GetString()!);
                var iv = Hex(op.GetProperty("iv").GetString()!);
                return Ok(new { hex = Str(PrivateTail.EncryptCredentials(username, password, iv)) });
            }
            case "header":
            {
                var network = new NetworkInfo(
                    Ip: op.GetProperty("ip").GetString()!,
                    Netmask: op.GetProperty("mask").GetString()!,
                    Gateway: op.GetProperty("gateway").GetString()!,
                    Dhcp: op.GetProperty("dhcp").GetString()!);
                var flag = op.GetProperty("flag").GetInt32();
                var header = InvokePrivate(nameof(PrivateTail), "Header", network, flag);
                return Ok(new { hex = Str((byte[])header![0]!) });
            }
            case "build_tail":
            {
                var username = Encoding.ASCII.GetBytes(op.GetProperty("username").GetString()!);
                var password = Encoding.ASCII.GetBytes(op.GetProperty("password").GetString()!);
                var network = new NetworkInfo(
                    Ip: op.GetProperty("ip").GetString()!,
                    Netmask: op.GetProperty("mask").GetString()!,
                    Gateway: op.GetProperty("gateway").GetString()!,
                    Dhcp: op.GetProperty("dhcp").GetString()!,
                    IpString: op.GetProperty("ip").GetString()!);
                var options = new TailOptions
                {
                    Nonce = Hex(op.GetProperty("nonce").GetString()!),
                    Fingerprint = Hex(op.GetProperty("fingerprint").GetString()!),
                    ServiceName = Encoding.ASCII.GetBytes(op.GetProperty("service").GetString()!),
                    ClientFlag = [op.GetProperty("client_flag").GetByte()],
                };
                if (op.TryGetProperty("static_id", out var sid) && sid.GetString() is { Length: > 0 } sidText)
                {
                    var padded = new NetworkInfo
                    {
                        StaticId = Encoding.ASCII.GetBytes(sidText),
                    };
                    network = network with { StaticId = padded.StaticId };
                }
                var tail = PrivateTail.BuildTail(username, password,
                    op.GetProperty("mac").GetString()!, network,
                    Hex(op.GetProperty("iv").GetString()!), options);
                return Ok(new { hex = Str(tail), length = tail.Length });
            }
            case "parse_props":
            {
                var payload = Hex(op.GetProperty("hex").GetString()!);
                var result = PrivateProperties.Parse(payload);
                return Ok(new
                {
                    ok = true,
                    errors = result.Errors,
                    consumed_bytes = result.ConsumedBytes,
                    messages = result.Messages,
                    section2_value = result.Section2Value,
                    section5_hash = result.Section5Hash,
                    section5_interval_ms = result.Section5IntervalMs,
                    properties = result.Properties.Select(p => new
                    {
                        type = $"0x{p.Type:X2}",
                        name = p.Name,
                        hex = p.Hex,
                        text = p.Text,
                        number = p.Number,
                    }).ToArray(),
                });
            }
            case "rc4":
            {
                var data = Hex(op.GetProperty("data").GetString()!);
                var key = op.GetProperty("key").GetString()!;
                return Ok(new { hex = Str(Rc4.Transform(data, Encoding.ASCII.GetBytes(key))) });
            }
            case "mschap_nt_response":
            {
                var response = MschapV2.GenerateNtResponse(
                    Hex(op.GetProperty("auth_challenge").GetString()!),
                    Hex(op.GetProperty("peer_challenge").GetString()!),
                    op.GetProperty("username").GetString()!,
                    op.GetProperty("password").GetString()!);
                return Ok(new { hex = Str(response) });
            }
            case "peap_credential":
            {
                var segment = PeapVendorSegments.Credential(
                    Encoding.ASCII.GetBytes(op.GetProperty("username").GetString()!),
                    Encoding.ASCII.GetBytes(op.GetProperty("password").GetString()!));
                return Ok(new { hex = Str(segment) });
            }
            case "peap_segments":
            {
                var mac = PeapVendorSegments.Mac(op.GetProperty("mac").GetString()!);
                var service = PeapVendorSegments.ServiceName(
                    Encoding.GetEncoding("GB18030").GetBytes(op.GetProperty("service").GetString()!));
                var phase = op.TryGetProperty("dhcp_phase", out var phaseElement)
                    ? PeapVendorSegments.DhcpPhase(phaseElement.GetByte())
                    : [];
                return Ok(new
                {
                    mac = Str(mac),
                    service = Str(service),
                    phase = Str(phase),
                });
            }
            default:
                return JsonSerializer.Serialize(new { ok = false, error = $"unknown op {name}" });
        }
    }

    private static object?[]? InvokePrivate(string typeName, string methodName, params object?[] args)
    {
        var type = Type.GetType($"RuijieCampusAuth.Core.{typeName}, RuijieCampusAuth.Core")
            ?? throw new InvalidOperationException($"type {typeName} not found");
        var method = type.GetMethod(methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException($"method {methodName} not found");
        return new object?[] { method.Invoke(null, args) };
    }

    private static string Ok(object payload) => JsonSerializer.Serialize(
        new Dictionary<string, object?> { ["ok"] = true, ["result"] = payload });

    private static byte[] Hex(string text) => Convert.FromHexString(text.Trim());

    private static string Str(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
