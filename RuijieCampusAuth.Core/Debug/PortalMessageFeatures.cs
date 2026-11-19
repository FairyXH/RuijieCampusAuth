using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

public sealed class PortalUdpDebugFeature : DebugFeatureBase
{
    public override string Name => "portal-udp";
    public override string DisplayName => "Portal 消息";
    public override string Category => "portal";
    public override string Phase => "P5";
    public override string Description =>
        "在指定端口监听 Portal/指示 UDP 消息，限时收集并返回原文与 hex。";
    public override bool RequiresService => true;
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
        var port = (int)(DebugArgs.Number(args, "port") ?? 0);
        if (port is < 1 or > 65535)
        {
            return Fail("需要 {\"port\":<1-65535>}");
        }
        var duration = Math.Clamp(DebugArgs.Number(args, "duration") ?? 5, 1, 60);
        var messages = new JsonArray();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        var localEndpoint = client.Client.LocalEndPoint?.ToString() ?? "";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(duration));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(timeout.Token);
                var text = Encoding.UTF8.GetString(result.Buffer);
                messages.Add(new JsonObject
                {
                    ["from"] = result.RemoteEndPoint.ToString(),
                    ["length"] = result.Buffer.Length,
                    ["text"] = text,
                    ["hex"] = Convert.ToHexString(result.Buffer).ToLowerInvariant(),
                });
                if (messages.Count >= 20)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Duration reached or caller cancelled.
        }
        catch (SocketException ex)
        {
            return Fail(ex.Message);
        }
        return Ok(
            $"端口 {port} 收到 {messages.Count} 条消息",
            new JsonObject
            {
                ["port"] = port,
                ["local_endpoint"] = localEndpoint,
                ["duration_seconds"] = duration,
                ["messages"] = messages,
            }.ToJsonString());
    }
}

public sealed class UserMessagesDebugFeature : DebugFeatureBase
{
    public override string Name => "user-messages";
    public override string DisplayName => "用户消息";
    public override string Category => "ops";
    public override string Phase => "P5";
    public override string Description =>
        "解析私有属性中的账户/个人/广播/拒绝原因文本。";
    public override bool SupportsArguments => true;

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Task.FromResult(Fail(error));
        }
        var hex = DebugArgs.String(args, "hex");
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Task.FromResult(Fail("需要 {\"hex\":\"<私有属性 hex>\"}"));
        }
        var cleaned = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            return Task.FromResult(Fail("hex 长度必须为偶数"));
        }
        var parsed = PrivateProperties.Parse(Convert.FromHexString(cleaned));
        var messages = new JsonArray();
        foreach (var property in parsed.Properties)
        {
            if (property.Type is
                (byte)PrivatePropertyType.AccountInfo or
                (byte)PrivatePropertyType.PersonalInfo or
                (byte)PrivatePropertyType.BroadcastInfo or
                (byte)PrivatePropertyType.RejectReason)
            {
                messages.Add(new JsonObject
                {
                    ["type"] = property.Type,
                    ["name"] = property.Name,
                    ["text"] = property.DisplayValue,
                });
            }
        }
        return Task.FromResult(Ok(
            $"提取 {messages.Count} 条文本",
            new JsonObject { ["messages"] = messages }.ToJsonString()));
    }
}
