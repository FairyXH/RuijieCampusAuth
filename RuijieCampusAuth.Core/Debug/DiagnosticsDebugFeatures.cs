using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Diagnostics;

namespace RuijieCampusAuth.Core.Debug;

internal static class DebugArgs
{
    public static JsonNode? Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            error = $"参数 JSON 无效: {ex.Message}";
            return null;
        }
    }

    public static string? String(JsonNode? node, string name)
    {
        return node?[name] is JsonValue value &&
               value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    public static double? Number(JsonNode? node, string name)
    {
        if (node?[name] is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }
        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }
        return null;
    }

    public static bool? Bool(JsonNode? node, string name)
    {
        return node?[name] is JsonValue value &&
               value.TryGetValue<bool>(out var flag)
            ? flag
            : null;
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
               ?? "";
    }
}

public sealed class OuiDetectDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "oui-detect";
    public override string DisplayName => "锐捷设备识别";
    public override string Category => "network";
    public override string Phase => "P4";
    public override string Description => "按 MAC OUI 判断认证设备是否为本司设备。";
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
        var mac = DebugArgs.String(args, "mac");
        if (string.IsNullOrWhiteSpace(mac))
        {
            var settings = store.LoadSettings();
            var nic = NicInventory.Resolve(settings.Interface);
            mac = nic is null ? "" : PrivateTail.NormalizeMac(nic.GetPhysicalAddress().ToString());
        }
        var normalized = PrivateTail.NormalizeMac(mac);
        if (normalized.Length == 0)
        {
            return Task.FromResult(Fail("没有可用的 MAC：请传 {\"mac\":\"xx:...\"}"));
        }
        var vendor = OuiDetect.VendorOf(normalized);
        var data = new JsonObject
        {
            ["mac"] = normalized,
            ["oui"] = normalized[..8],
            ["vendor"] = vendor,
            ["is_ruijie"] = vendor != "Unknown",
        };
        return Task.FromResult(Ok(
            vendor == "Unknown" ? $"{normalized} 非已知锐捷 OUI" : $"{normalized} -> {vendor}",
            data.ToJsonString()));
    }
}

public sealed class NicMonitorDebugFeature : DebugFeatureBase
{
    public override string Name => "nic-monitor";
    public override string DisplayName => "网卡监控";
    public override string Category => "network";
    public override string Phase => "P4";
    public override string Description => "枚举网卡、状态、速率、IP、网关与 MAC。";

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var items = new JsonArray();
        foreach (var nic in NicInventory.List())
        {
            items.Add(new JsonObject
            {
                ["id"] = nic.Id,
                ["name"] = nic.Name,
                ["description"] = nic.Description,
                ["type"] = nic.Type,
                ["status"] = nic.Status,
                ["mac"] = nic.Mac,
                ["speed_bps"] = nic.SpeedBitsPerSecond,
                ["ipv4"] = nic.Ipv4,
                ["mask"] = nic.Mask,
                ["gateway"] = nic.Gateway,
            });
        }
        return Task.FromResult(Ok(
            $"{items.Count} 个网卡",
            new JsonObject { ["nics"] = items }.ToJsonString()));
    }
}

public sealed class ArpMonitorDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "arp-monitor";
    public override string DisplayName => "ARP 网关监控";
    public override string Category => "network";
    public override string Phase => "P4";
    public override string Description =>
        "用 SendARP 观察网关 MAC，默认采样 10 秒；出现变化时记录。";
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
        var interfaceHint = DebugArgs.String(args, "interface") ?? settings.Interface;
        if (string.IsNullOrWhiteSpace(interfaceHint))
        {
            return Fail("未指定网卡：请传 {\"interface\":\"...\"} 或先保存网卡设置");
        }
        var duration = Math.Clamp(DebugArgs.Number(args, "duration") ?? 10, 1, 120);
        var interval = Math.Clamp(DebugArgs.Number(args, "interval") ?? 1, 0.2, 10);
        try
        {
            var result = await ArpMonitor.MonitorAsync(
                interfaceHint,
                TimeSpan.FromSeconds(duration),
                TimeSpan.FromSeconds(interval),
                cancellationToken);
            var changes = new JsonArray();
            foreach (var change in result.Changes)
            {
                changes.Add(change);
            }
            var observations = new JsonArray();
            foreach (var observation in result.Observations?.Take(100) ?? [])
            {
                observations.Add(new JsonObject
                {
                    ["time"] = observation.Time.ToString("O"),
                    ["ip"] = observation.Ip,
                    ["mac"] = observation.Mac,
                });
            }
            var data = new JsonObject
            {
                ["interface"] = result.Interface,
                ["gateway"] = result.Gateway,
                ["initial_mac"] = result.InitialMac,
                ["final_mac"] = result.FinalMac,
                ["change_count"] = result.Changes.Count,
                ["changes"] = changes,
                ["observations"] = observations,
            };
            return Ok(
                result.Changes.Count == 0
                    ? $"网关 MAC 稳定: {result.InitialMac ?? "(no reply)"}"
                    : $"网关 MAC 变化 {result.Changes.Count} 次",
                data.ToJsonString());
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
}

public sealed class NetworkProbeDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "network-probe";
    public override string DisplayName => "网络诊断";
    public override string Category => "network";
    public override string Phase => "P4";
    public override string Description => "网关 ping、DNS 解析、HTTP 探测。";
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
        var interfaceHint = DebugArgs.String(args, "interface") ?? settings.Interface;
        var url = DebugArgs.String(args, "url") ?? settings.ConnectivityUrl;
        try
        {
            var result = await NetworkProbe.RunAsync(interfaceHint, url, cancellationToken);
            var data = new JsonObject
            {
                ["interface"] = result.Interface,
                ["gateway"] = result.Gateway,
                ["gateway_reachable"] = result.GatewayReachable,
                ["gateway_ms"] = result.GatewayMs,
                ["dns_ok"] = result.DnsOk,
                ["dns_detail"] = result.DnsDetail,
                ["http_ok"] = result.HttpOk,
                ["http_status"] = result.HttpStatus,
                ["detail"] = result.Detail,
                ["elapsed_ms"] = result.ElapsedMs,
            };
            return Ok(
                result.GatewayReachable && result.DnsOk && result.HttpOk
                    ? "网关、DNS、HTTP 正常"
                    : "存在异常项，见结果",
                data.ToJsonString());
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
}

public sealed class ProxyDetectDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "proxy-detect";
    public override string DisplayName => "代理检测";
    public override string Category => "network";
    public override string Phase => "P4";
    public override string Description =>
        "探测网关上的常见代理端口，并读取环境代理变量。";
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
        var gateway = DebugArgs.String(args, "gateway");
        if (string.IsNullOrWhiteSpace(gateway))
        {
            var nic = NicInventory.Resolve(settings.Interface);
            gateway = nic is null ? "" : NicInventory.GatewayOf(nic);
        }
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return Fail("没有网关地址");
        }

        var ports = args?["ports"] is JsonArray array
            ? array.Select(item => item?.GetValue<int>() ?? 0)
                .Where(port => port > 0 && port <= 65535)
                .ToArray()
            : ProxyDetect.DefaultPorts;
        var timeout = Math.Clamp(DebugArgs.Number(args, "timeout_ms") ?? 300, 100, 5000);
        var result = await ProxyDetect.ProbeAsync(
            gateway,
            ports,
            (int)timeout,
            cancellationToken);
        var open = new JsonArray();
        foreach (var port in result.OpenPorts)
        {
            open.Add(port);
        }
        var data = new JsonObject
        {
            ["gateway"] = result.Gateway,
            ["ports"] = new JsonArray(ports
                .Select(port => (JsonNode)JsonValue.Create(port)!)
                .ToArray()),
            ["open_ports"] = open,
            ["environment_proxy"] = result.EnvironmentProxy,
        };
        return Ok(
            result.OpenPorts.Count == 0
                ? "网关未发现常见代理端口"
                : $"发现代理端口: {string.Join(",", result.OpenPorts)}",
            data.ToJsonString());
    }
}

public sealed class DialupDetectDebugFeature : DebugFeatureBase
{
    public override string Name => "dialup-detect";
    public override string DisplayName => "拨号规避";
    public override string Category => "network";
    public override string Phase => "P4";
    public override string Description => "检测 PPP/3G/4G/VPN 适配器冲突。";

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var reasons = ProxyDetect.DetectDialup();
        var array = new JsonArray();
        foreach (var reason in reasons)
        {
            array.Add(reason);
        }
        var data = new JsonObject
        {
            ["detected"] = reasons.Count > 0,
            ["adapters"] = array,
        };
        return Task.FromResult(Ok(
            reasons.Count == 0 ? "未发现拨号/VPN 适配器" : $"发现 {reasons.Count} 个",
            data.ToJsonString()));
    }
}

public sealed class DoctorPipeDebugFeature : DebugFeatureBase
{
    public override string Name => "doctor-pipe";
    public override string DisplayName => "小锐医生管道";
    public override string Category => "ops";
    public override string Phase => "P4";
    public override string Description => "探测 \\\\.\\pipe\\SU_DOCTOR_INTERFACE 是否存在。";

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var available = DoctorPipe.IsAvailable();
        var data = new JsonObject
        {
            ["pipe"] = DoctorPipe.PipePath,
            ["available"] = available,
        };
        return Task.FromResult(Ok(
            available ? "医生管道可用" : "医生管道不存在",
            data.ToJsonString()));
    }
}
