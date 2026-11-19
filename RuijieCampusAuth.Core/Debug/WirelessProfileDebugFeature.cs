using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

public sealed class WirelessProfileDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "wireless-profile";
    public override string DisplayName => "无线配置";
    public override string Category => "wireless";
    public override string Phase => "P7";
    public override string Description =>
        "通过 Windows WLAN API 列出/创建/删除/连接 WPA2-Enterprise（PEAP-MSCHAPv2）配置。";
    public override bool RequiresConfirmation => true;
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
        var action = (DebugArgs.String(args, "action") ?? "list").ToLowerInvariant();
        var settings = store.LoadSettings();
        var interfaceHint = DebugArgs.FirstNonEmpty(
            DebugArgs.String(args, "interface"),
            settings.Interface);
        try
        {
            var guid = WlanProfileManager.ResolveInterface(interfaceHint);
            switch (action)
            {
                case "list":
                    return Task.FromResult(List(guid));
                case "create":
                {
                    var ssid = DebugArgs.String(args, "ssid");
                    if (string.IsNullOrWhiteSpace(ssid))
                    {
                        return Task.FromResult(Fail("create 需要 ssid"));
                    }
                    var account = store.LoadAccounts().FirstOrDefault(item =>
                                      item.Id == settings.ActiveAccountId)
                                  ?? store.LoadAccounts().FirstOrDefault(item => item.Enabled);
                    var identity = DebugArgs.FirstNonEmpty(
                        DebugArgs.String(args, "identity"),
                        account?.Username ?? "");
                    var password = DebugArgs.FirstNonEmpty(
                        DebugArgs.String(args, "password"),
                        account?.Password ?? "");
                    if (string.IsNullOrWhiteSpace(identity) ||
                        string.IsNullOrWhiteSpace(password))
                    {
                        return Task.FromResult(Fail("create 需要 identity/password"));
                    }
                    WlanProfileManager.CreatePeapProfile(
                        guid,
                        ssid,
                        identity,
                        password,
                        DebugArgs.String(args, "authentication") ?? "WPA2",
                        DebugArgs.String(args, "encryption") ?? "AES",
                        DebugArgs.Bool(args, "auto") ?? true,
                        DebugArgs.String(args, "server_names") ?? "",
                        DebugArgs.String(args, "trusted_root_ca") ?? "");
                    return Task.FromResult(Ok(
                        $"已创建/更新无线配置 {ssid}（个人凭据已写入）",
                        ProfilesJson(guid, "create")));
                }
                case "delete":
                {
                    var profile = DebugArgs.FirstNonEmpty(
                        DebugArgs.String(args, "profile"),
                        DebugArgs.String(args, "ssid") ?? "");
                    if (string.IsNullOrWhiteSpace(profile))
                    {
                        return Task.FromResult(Fail("delete 需要 profile 或 ssid"));
                    }
                    WlanProfileManager.DeleteProfile(guid, profile);
                    return Task.FromResult(Ok(
                        $"已删除无线配置 {profile}",
                        ProfilesJson(guid, "delete")));
                }
                case "connect":
                {
                    var profile = DebugArgs.FirstNonEmpty(
                        DebugArgs.String(args, "profile"),
                        DebugArgs.String(args, "ssid") ?? "");
                    if (string.IsNullOrWhiteSpace(profile))
                    {
                        return Task.FromResult(Fail("connect 需要 profile 或 ssid"));
                    }
                    WlanProfileManager.Connect(guid, profile);
                    return Task.FromResult(Ok(
                        $"已请求连接 {profile}",
                        new JsonObject { ["interface"] = guid.ToString(), ["profile"] = profile }
                            .ToJsonString()));
                }
                case "disconnect":
                    WlanProfileManager.Disconnect(guid);
                    return Task.FromResult(Ok(
                        "已请求断开 WLAN",
                        new JsonObject { ["interface"] = guid.ToString() }.ToJsonString()));
                default:
                    return Task.FromResult(Fail(
                        $"未知 action: {action}（list/create/delete/connect/disconnect）"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex.Message));
        }
    }

    private static DebugFeatureResult List(Guid guid)
    {
        var interfaces = WlanProfileManager.ListInterfaces();
        var profiles = WlanProfileManager.ListProfiles(guid);
        var interfaceArray = new JsonArray();
        foreach (var item in interfaces)
        {
            interfaceArray.Add(new JsonObject
            {
                ["id"] = item.Id.ToString(),
                ["description"] = item.Description,
                ["state"] = item.State,
            });
        }
        var profileArray = new JsonArray();
        foreach (var item in profiles)
        {
            profileArray.Add(new JsonObject
            {
                ["name"] = item.Name,
                ["flags"] = item.Flags,
            });
        }
        var data = new JsonObject
        {
            ["interfaces"] = interfaceArray,
            ["selected_interface"] = guid.ToString(),
            ["profiles"] = profileArray,
        };
        return new DebugFeatureResult(
            "wireless-profile",
            true,
            $"WLAN 接口 {interfaces.Count} 个，配置 {profiles.Count} 个",
            data.ToJsonString(),
            DateTimeOffset.Now);
    }

    private static string ProfilesJson(Guid guid, string action)
    {
        var profiles = WlanProfileManager.ListProfiles(guid);
        var array = new JsonArray();
        foreach (var item in profiles)
        {
            array.Add(new JsonObject { ["name"] = item.Name, ["flags"] = item.Flags });
        }
        return new JsonObject
        {
            ["action"] = action,
            ["interface"] = guid.ToString(),
            ["profiles"] = array,
        }.ToJsonString();
    }
}
