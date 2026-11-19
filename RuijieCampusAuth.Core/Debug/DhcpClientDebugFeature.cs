using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

public sealed class DhcpClientDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "dhcp-client";
    public override string DisplayName => "DHCP 客户端 / Option 82";
    public override string Category => "network";
    public override string Phase => "P3";
    public override string Description =>
        "复用 Windows DHCP API（GetInterfaceInfo / IpReleaseAddress / IpRenewAddress），按原版时序执行 release/renew。";
    public override bool RequiresService => true;
    public override bool RequiresConfirmation => true;
    public override bool SupportsArguments => true;

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var settings = store.LoadSettings();
        JsonNode? arguments = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                arguments = JsonNode.Parse(argumentsJson);
            }
            catch (Exception ex)
            {
                return Fail($"参数 JSON 无效: {ex.Message}");
            }
        }

        var interfaceHint = arguments?["interface"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(interfaceHint))
        {
            interfaceHint = settings.Interface;
        }
        if (string.IsNullOrWhiteSpace(interfaceHint))
        {
            return Fail("未指定网卡：请传 {\"interface\":\"...\"} 或先保存网卡设置");
        }

        var operationText = arguments?["operation"]?.GetValue<string>() ?? "renew";
        if (!TryParseOperation(operationText, out var operation))
        {
            return Fail($"未知操作: {operationText}（status/renew/release/release-renew）");
        }
        var timeoutSeconds = arguments?["timeout"]?.GetValue<double>()
                             ?? settings.DhcpTimeout;
        var option82 = arguments?["option82"]?.GetValue<bool>()
                       ?? settings.DhcpOption82;

        try
        {
            var result = await DhcpControl.RunAsync(
                interfaceHint,
                operation,
                TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
                option82,
                cancellationToken);
            var data = new JsonObject
            {
                ["ok"] = result.Ok,
                ["operation"] = operation.ToString().ToLowerInvariant(),
                ["elapsed_ms"] = (int)result.Elapsed.TotalMilliseconds,
                ["option82"] = result.Option82Requested,
                ["option82_note"] = result.Option82Requested
                    ? "原版该引擎版本未主动注入 Option 82，仅保存配置"
                    : null,
                ["lease"] = result.Lease is null
                    ? null
                    : new JsonObject
                    {
                        ["interface"] = result.Lease.Interface,
                        ["adapter_name"] = result.Lease.AdapterName,
                        ["adapter_index"] = result.Lease.AdapterIndex,
                        ["ip"] = result.Lease.Ip,
                        ["mask"] = result.Lease.Mask,
                        ["gateway"] = result.Lease.Gateway,
                    },
            };
            return new DebugFeatureResult(
                Name,
                result.Ok,
                result.Message,
                data.ToJsonString(),
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static bool TryParseOperation(string value, out DhcpOperation operation)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "status":
                operation = DhcpOperation.Status;
                return true;
            case "renew":
                operation = DhcpOperation.Renew;
                return true;
            case "release":
                operation = DhcpOperation.Release;
                return true;
            case "release-renew":
            case "renew-release":
                operation = DhcpOperation.ReleaseRenew;
                return true;
            default:
                operation = DhcpOperation.Renew;
                return false;
        }
    }
}
