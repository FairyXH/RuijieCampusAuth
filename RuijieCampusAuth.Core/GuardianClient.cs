using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuijieCampusAuth.Core;

public sealed record GuardianProbe(
    bool? Online,
    bool StableOnline,
    bool CaptivePortal,
    string InterfaceName,
    string InterfaceId,
    string Description,
    string Ip,
    string Gateway,
    string Mac,
    string Summary,
    string Source,
    string Timestamp,
    string Detail)
{
    public bool IsOffline => Online == false || CaptivePortal;

    public string Short()
    {
        var name = string.IsNullOrWhiteSpace(InterfaceName) ? "认证网卡" : InterfaceName;
        var state = Online is null
            ? "状态未知"
            : CaptivePortal
                ? "疑似强制门户"
                : Online.Value ? "在线" : "离线";
        return string.IsNullOrWhiteSpace(Ip)
            ? $"{name} {state}"
            : $"{name} {state} | {Ip}";
    }

    public string Describe()
    {
        var state = Online is null
            ? "未知（无探测数据）"
            : CaptivePortal
                ? "疑似强制门户，需要认证"
                : Online.Value ? "在线（外网探测成功）" : "离线（外网探测失败）";
        var builder = new StringBuilder();
        builder.AppendLine($"网卡: {InterfaceName} | {Description}");
        builder.AppendLine($"地址: {Ip} | 网关 {Gateway} | MAC {Mac}");
        builder.AppendLine($"外网: {state} | 稳定: {(StableOnline ? "是" : "否")}");
        builder.AppendLine($"来源: {Source} | 探测时间: {Timestamp}");
        if (!string.IsNullOrWhiteSpace(Summary))
        {
            builder.AppendLine($"摘要: {Summary}");
        }
        if (!string.IsNullOrWhiteSpace(Detail))
        {
            builder.Append(Detail);
        }
        return builder.ToString().TrimEnd();
    }
}

public sealed class GuardianClient : IDisposable
{
    public const string DefaultUrl = "http://127.0.0.1:18765/mcp";
    private const string ProtocolVersion = "2025-11-25";
    private static readonly Regex GuidPattern = new(
        "\\{([0-9a-fA-F-]{36})\\}",
        RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly string _url;
    private int _sequence;
    private bool _initialized;

    public GuardianClient(string url = DefaultUrl, double timeoutSeconds = 8)
    {
        _url = NormalizeUrl(url);
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 30)),
        };
    }

    public static string NormalizeUrl(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? DefaultUrl : value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("网络管家 MCP 地址必须是 http://127.0.0.1:<端口>/mcp");
        }
        var host = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : uri.Host;
        if (host != "127.0.0.1")
        {
            throw new ArgumentException("网络管家 MCP 只允许本机 127.0.0.1");
        }
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/mcp" : uri.AbsolutePath;
        if (path is not ("/mcp" or "/mcp/"))
        {
            throw new ArgumentException("网络管家 MCP 路径必须是 /mcp");
        }
        if (uri.Port <= 0)
        {
            throw new ArgumentException("网络管家 MCP 地址必须包含端口");
        }
        return $"http://127.0.0.1:{uri.Port}{path}";
    }

    private async Task<JsonElement> RpcAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = ++_sequence,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation(
            "Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"网络管家 HTTP {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        }
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.ToString());
        }
        if (!root.TryGetProperty("result", out var result))
        {
            throw new InvalidOperationException("网络管家响应缺少 result");
        }
        return result.Clone();
    }

    public async Task<JsonElement> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            await RpcAsync(
                "initialize",
                new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "ruijie-campus-auth", version = "0.9.0" },
                },
                cancellationToken);
            _initialized = true;
        }
        var result = await RpcAsync(
            "tools/call",
            new { name = "guardian_status", arguments = new { } },
            cancellationToken);
        if (result.TryGetProperty("isError", out var isError) &&
            isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("guardian_status 调用失败");
        }
        if (result.TryGetProperty("structuredContent", out var structured))
        {
            return structured.Clone();
        }
        if (result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    using var parsed = JsonDocument.Parse(text.GetString() ?? "{}");
                    return parsed.RootElement.Clone();
                }
            }
        }
        throw new InvalidOperationException("guardian_status 返回格式无效");
    }

    public async Task<GuardianProbe> ProbeAsync(
        string? interfaceHint,
        CancellationToken cancellationToken = default)
    {
        var data = await StatusAsync(cancellationToken);
        var snapshot = data.TryGetProperty("snapshot", out var snapshotElement)
            ? snapshotElement
            : data;
        return SelectProbe(snapshot, interfaceHint);
    }

    public static GuardianProbe SelectProbe(JsonElement snapshot, string? hint)
    {
        var interfaces = snapshot.TryGetProperty("interfaces", out var items) &&
                         items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : [];
        JsonElement? chosen = null;
        var source = "";
        var guid = ExtractGuid(hint);
        if (!string.IsNullOrEmpty(guid))
        {
            chosen = interfaces
                .Where(item => GuidEquals(GetString(item, "adapterGuid"), guid))
                .Select(item => (JsonElement?)item)
                .FirstOrDefault();
            if (chosen is not null)
            {
                source = "认证网卡";
            }
        }
        if (chosen is null && !string.IsNullOrWhiteSpace(hint))
        {
            var text = hint.Trim();
            chosen = interfaces
                .Where(item =>
                    GetString(item, "name").Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    GetString(item, "description").Contains(text, StringComparison.OrdinalIgnoreCase))
                .Select(item => (JsonElement?)item)
                .FirstOrDefault();
            if (chosen is not null)
            {
                source = "认证网卡";
            }
        }
        if (chosen is null &&
            snapshot.TryGetProperty("expectedOutletInterfaceId", out var outlet) &&
            outlet.ValueKind == JsonValueKind.String)
        {
            chosen = interfaces
                .Where(item => GetString(item, "id") == outlet.GetString())
                .Select(item => (JsonElement?)item)
                .FirstOrDefault();
            if (chosen is not null)
            {
                source = "默认出口";
            }
        }
        if (chosen is null)
        {
            chosen = interfaces
                .Where(item =>
                    item.TryGetProperty("isDefaultRoute", out var value) &&
                    value.ValueKind == JsonValueKind.True)
                .Select(item => (JsonElement?)item)
                .FirstOrDefault();
            if (chosen is not null)
            {
                source = "默认路由";
            }
        }
        if (chosen is { } selected)
        {
            return ProbeFromInterface(selected, snapshot, source);
        }
        if (snapshot.TryGetProperty("globalProbe", out var global))
        {
            var online = GetBoolOrNull(global, "isOnline");
            return new GuardianProbe(
                online,
                GetBool(global, "stableOnline"),
                GetBool(global, "captivePortalSuspected"),
                "全局默认出口",
                "",
                "",
                "",
                "",
                "",
                GetString(global, "summary"),
                "全局探测",
                GetString(global, "timestampUtc"),
                "");
        }
        return new GuardianProbe(
            null, false, false, "", "", "", "", "", "", "", "", "", "网络管家没有返回探测数据");
    }

    private static GuardianProbe ProbeFromInterface(
        JsonElement item,
        JsonElement snapshot,
        string source)
    {
        var probe = item.TryGetProperty("probe", out var value) &&
                    value.ValueKind == JsonValueKind.Object
            ? value
            : default;
        if (probe.ValueKind != JsonValueKind.Object &&
            snapshot.TryGetProperty("globalProbe", out var global) &&
            GetString(global, "sourceInterfaceId") == GetString(item, "id"))
        {
            probe = global;
        }
        var online = probe.ValueKind == JsonValueKind.Object
            ? GetBoolOrNull(probe, "isOnline")
            : null;
        var stable = probe.ValueKind == JsonValueKind.Object &&
                     GetBool(probe, "stableOnline");
        var captive = probe.ValueKind == JsonValueKind.Object &&
                      GetBool(probe, "captivePortalSuspected");
        var detail = probe.ValueKind == JsonValueKind.Object
            ? ""
            : "该接口没有独立探测数据，回退到本机检测";
        if (GetBool(snapshot, "isPaused"))
        {
            online = null;
            detail = "网络管家已暂停探测，回退到本机检测";
        }
        else if (probe.ValueKind == JsonValueKind.Object &&
                 probe.TryGetProperty("isAuthoritative", out var authoritative) &&
                 authoritative.ValueKind == JsonValueKind.False)
        {
            online = null;
            detail = "网络管家探测不是权威结果，回退到本机检测";
        }
        return new GuardianProbe(
            online,
            stable,
            captive,
            GetString(item, "name"),
            GetString(item, "id"),
            GetString(item, "description"),
            GetString(item, "primaryIpv4Address"),
            GetString(item, "primaryGateway"),
            GetString(item, "macAddress"),
            GetString(probe, "summary"),
            source,
            GetString(probe, "timestampUtc"),
            detail);
    }

    private static string ExtractGuid(string? value)
    {
        var match = GuidPattern.Match(value ?? "");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static bool GuidEquals(string first, string second)
    {
        return first.Trim('{', '}').Equals(
            second.Trim('{', '}'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static bool? GetBoolOrNull(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public void Dispose() => _http.Dispose();
}
