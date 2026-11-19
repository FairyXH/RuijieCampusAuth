using System.Text.Json;
using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

public sealed class PrivatePropertiesDebugFeature : DebugFeatureBase
{
    public override string Name => "private-properties";
    public override string DisplayName => "服务器私有属性";
    public override string Category => "auth";
    public override string Phase => "P2";
    public override string Description =>
        "解析 EAP-Success 服务器私有属性 TLV（type/len/value）；0x5C/0x5D 用 RC4(com.ruijie.www) 解出 8 字节。";
    public override bool SupportsArguments => true;

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        string? hex;
        try
        {
            hex = ExtractHex(argumentsJson);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"参数无效: {ex.Message}"));
        }
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Task.FromResult(Fail(
                "需要参数：{\"hex\":\"<EAP-Success 私有属性 hex>\"} 或直接传 hex 字符串"));
        }

        byte[] payload;
        try
        {
            payload = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            return Task.FromResult(Fail($"hex 解析失败: {ex.Message}"));
        }

        var parsed = PrivateProperties.Parse(payload);
        var array = new JsonArray();
        foreach (var property in parsed.Properties)
        {
            array.Add(new JsonObject
            {
                ["type"] = property.Type,
                ["name"] = property.Name,
                ["length"] = property.Raw.Length,
                ["value"] = property.DisplayValue,
                ["text"] = property.Text,
                ["number"] = property.Number,
                ["hex"] = property.Hex,
            });
        }
        var data = new JsonObject
        {
            ["declared_length"] = parsed.DeclaredLength,
            ["property_count"] = parsed.Properties.Count,
            ["properties"] = array,
            ["errors"] = BuildErrors(parsed.Errors),
        };
        return Task.FromResult(Ok(
            parsed.Errors.Count == 0
                ? $"解析 {parsed.Properties.Count} 项私有属性"
                : $"解析 {parsed.Properties.Count} 项，{parsed.Errors.Count} 个问题",
            data.ToJsonString()));
    }

    private static JsonArray BuildErrors(IReadOnlyList<string> errors)
    {
        var array = new JsonArray();
        foreach (var error in errors)
        {
            array.Add(error);
        }
        return array;
    }

    private static string? ExtractHex(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }
        var text = argumentsJson.Trim();
        if (text.StartsWith('{'))
        {
            var node = JsonNode.Parse(text);
            var value = node?["hex"]?.GetValue<string>()
                        ?? node?["payload"]?.GetValue<string>();
            text = value ?? "";
        }
        var cleaned = new string(text
            .Where(ch => Uri.IsHexDigit(ch))
            .ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            throw new ArgumentException("hex 长度必须为偶数");
        }
        return cleaned;
    }
}
