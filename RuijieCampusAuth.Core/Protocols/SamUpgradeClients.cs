using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RuijieCampusAuth.Core.Protocols;

public sealed record UpgradeCheckResult(
    bool Ok,
    int Status,
    string Url,
    string Body,
    System.Text.Json.Nodes.JsonNode? Json);

public static class UpgradeClient
{
    public static Task<UpgradeCheckResult> CheckAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("upgrade url is empty", nameof(url));
        }
        return CheckCoreAsync(url, cancellationToken);
    }

    private static async Task<UpgradeCheckResult> CheckCoreAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var response = await HttpJsonClient.GetAsync(url, cancellationToken);
        return new UpgradeCheckResult(
            response.Ok,
            response.Status,
            response.Url,
            response.Body,
            response.Json);
    }
}

public sealed record SamProbeResult(
    bool Ok,
    string Host,
    int Port,
    string RequestHex,
    string ResponseHex,
    string? Error,
    long ElapsedMs);

public enum DesMode
{
    Cbc,
    Ecb,
}

public static class SamDirectClient
{
    public static async Task<SamProbeResult> ProbeAsync(
        string host,
        int port,
        byte[] payload,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(Math.Max(100, timeoutMilliseconds));
        var started = Environment.TickCount64;
        try
        {
            await client.SendAsync(payload, payload.Length, host, port);
            var response = await client.ReceiveAsync(timeout.Token);
            return new SamProbeResult(
                true,
                host,
                port,
                Convert.ToHexString(payload).ToLowerInvariant(),
                Convert.ToHexString(response.Buffer).ToLowerInvariant(),
                null,
                Environment.TickCount64 - started);
        }
        catch (OperationCanceledException)
        {
            return new SamProbeResult(
                false,
                host,
                port,
                Convert.ToHexString(payload).ToLowerInvariant(),
                "",
                "timeout",
                Environment.TickCount64 - started);
        }
        catch (Exception ex)
        {
            return new SamProbeResult(
                false,
                host,
                port,
                Convert.ToHexString(payload).ToLowerInvariant(),
                "",
                ex.Message,
                Environment.TickCount64 - started);
        }
    }

    public static byte[] DesTransform(
        byte[] data,
        byte[] key,
        byte[]? iv,
        DesMode mode,
        bool encrypt)
    {
        using var des = DES.Create();
        des.Key = key;
        des.Mode = mode == DesMode.Cbc ? CipherMode.CBC : CipherMode.ECB;
        des.Padding = PaddingMode.None;
        if (mode == DesMode.Cbc)
        {
            des.IV = iv is { Length: 8 }
                ? iv
                : throw new ArgumentException("CBC IV must be 8 bytes", nameof(iv));
        }
        using var transform = encrypt
            ? des.CreateEncryptor()
            : des.CreateDecryptor();
        var output = new byte[data.Length];
        transform.TransformBlock(data, 0, data.Length, output, 0);
        return output;
    }
}
