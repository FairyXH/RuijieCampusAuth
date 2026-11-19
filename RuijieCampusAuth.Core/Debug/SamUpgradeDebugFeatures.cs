using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

public sealed class UpgradeCheckDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "upgrade-check";
    public override string DisplayName => "升级检查";
    public override string Category => "ops";
    public override string Phase => "P8";
    public override string Description =>
        "GET 升级地址并返回状态/内容（不下载、不安装）。";
    public override bool SupportsArguments => true;

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Fail(error);
        }
        var settings = store.LoadSettings();
        var url = DebugArgs.FirstNonEmpty(
            DebugArgs.String(args, "url"),
            settings.UpdateUrl,
            settings.Debug.Endpoints.Upgrade);
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail("缺少升级地址：请传 {\"url\":\"http://...\"}");
        }
        try
        {
            var result = await UpgradeClient.CheckAsync(url, cancellationToken);
            var data = new JsonObject
            {
                ["ok"] = result.Ok,
                ["status"] = result.Status,
                ["url"] = result.Url,
                ["body"] = result.Body,
                ["json"] = result.Json?.DeepClone(),
            };
            return new DebugFeatureResult(
                Name,
                result.Ok,
                result.Ok ? $"升级检查 HTTP {result.Status}" : $"升级检查失败 HTTP {result.Status}",
                data.ToJsonString(),
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
}

public sealed class SamDirectDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "sam-direct";
    public override string DisplayName => "SAM 直通";
    public override string Category => "portal";
    public override string Phase => "P8";
    public override string Description =>
        "SAM 直通探针：UDP 发送 caller-supplied 报文，可选 DES-CBC/ECB 变换。报文布局未确认，不伪造握手。";
    public override bool SupportsArguments => true;

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Fail(error);
        }
        var settings = store.LoadSettings();
        var host = DebugArgs.FirstNonEmpty(
            DebugArgs.String(args, "host"),
            settings.SamHost);
        var port = (int)(DebugArgs.Number(args, "port") ?? settings.SamPort);
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return Fail("需要 host/port（或档案中的 sam_host/sam_port）");
        }
        var payloadHex = DebugArgs.String(args, "payload_hex");
        if (string.IsNullOrWhiteSpace(payloadHex))
        {
            return Fail("需要 payload_hex");
        }
        var payload = ParseHex(payloadHex);
        if (payload.Length == 0)
        {
            return Fail("payload_hex 为空");
        }

        var output = payload;
        var keyHex = DebugArgs.String(args, "des_key");
        var ivHex = DebugArgs.String(args, "des_iv");
        var transform = !string.IsNullOrWhiteSpace(keyHex);
        if (transform)
        {
            var key = ParseHex(keyHex!);
            if (key.Length != 8)
            {
                return Fail("des_key 必须为 8 字节");
            }
            var iv = string.IsNullOrWhiteSpace(ivHex) ? null : ParseHex(ivHex!);
            var mode = (DebugArgs.String(args, "des_mode") ?? "cbc")
                .Equals("ecb", StringComparison.OrdinalIgnoreCase)
                ? DesMode.Ecb
                : DesMode.Cbc;
            var encrypt = DebugArgs.Bool(args, "encrypt") ?? true;
            try
            {
                output = SamDirectClient.DesTransform(payload, key, iv, mode, encrypt);
            }
            catch (Exception ex)
            {
                return Fail($"DES 变换失败: {ex.Message}");
            }
        }

        var timeout = Math.Clamp(
            (int)(DebugArgs.Number(args, "timeout_ms") ?? 2000),
            200,
            30000);
        var result = await SamDirectClient.ProbeAsync(
            host,
            port,
            output,
            timeout,
            cancellationToken);
        var data = new JsonObject
        {
            ["ok"] = result.Ok,
            ["host"] = result.Host,
            ["port"] = result.Port,
            ["request_hex"] = result.RequestHex,
            ["response_hex"] = result.ResponseHex,
            ["des_transformed"] = transform,
            ["elapsed_ms"] = result.ElapsedMs,
            ["error"] = result.Error,
        };
        return new DebugFeatureResult(
            Name,
            result.Ok,
            result.Ok ? $"收到响应 {result.ResponseHex.Length / 2} 字节" : $"无响应: {result.Error}",
            data.ToJsonString(),
            DateTimeOffset.Now);
    }

    private static byte[] ParseHex(string value)
    {
        var cleaned = new string(value.Where(Uri.IsHexDigit).ToArray());
        if (cleaned.Length % 2 != 0)
        {
            throw new ArgumentException("hex 长度必须为偶数");
        }
        return cleaned.Length == 0 ? [] : Convert.FromHexString(cleaned);
    }
}
