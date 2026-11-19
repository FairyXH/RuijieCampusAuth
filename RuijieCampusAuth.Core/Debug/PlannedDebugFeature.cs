namespace RuijieCampusAuth.Core.Debug;

public sealed class PlannedDebugFeature(
    string name,
    string displayName,
    string category,
    string phase,
    string description,
    bool requiresService = false,
    bool requiresConfirmation = false,
    bool supportsArguments = false) : DebugFeatureBase
{
    public override string Name => name;
    public override string DisplayName => displayName;
    public override string Category => category;
    public override string Phase => phase;
    public override string Description => description;
    public override bool RequiresService => requiresService;
    public override bool RequiresConfirmation => requiresConfirmation;
    public override bool SupportsArguments => supportsArguments;

    public override Task<DebugFeatureStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new DebugFeatureStatus(
            Name,
            false,
            "planned",
            $"计划阶段 {Phase}",
            DateTimeOffset.Now));
    }

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Fail($"该功能尚未实现（计划阶段 {Phase}）"));
    }
}

public static class DebugFeatureCatalog
{
    public static IReadOnlyList<IDebugFeature> Planned { get; } =
    [
        new PlannedDebugFeature(
            "service-switch",
            "服务切换",
            "auth",
            "P5",
            "读取服务列表并执行服务切换。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "peap",
            "PEAP / MSCHAPv2",
            "auth",
            "P6",
            "PEAP v0/v1 + MSCHAPv2 认证实验台。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "eap-tls",
            "EAP-TLS",
            "auth",
            "P6",
            "EAP-TLS 客户端证书认证实验台。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "portal-auth",
            "Portal 认证",
            "portal",
            "P5",
            "eportal login/logout/getOnlineUserInfo/pageInfo。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "portal-udp",
            "Portal 消息",
            "portal",
            "P5",
            "监听 Portal UDP 通知。",
            requiresService: true),
        new PlannedDebugFeature(
            "client-center",
            "客户端中心",
            "portal",
            "P5",
            "上报/查询客户端中心。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "self-service",
            "自助服务/余额",
            "portal",
            "P5",
            "拉取自助 ball JSON、余额、套餐、在线时长。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "password-change",
            "修改密码",
            "portal",
            "P5",
            "修改校园网账号密码。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "sam-direct",
            "SAM 直通",
            "portal",
            "P8",
            "SAM 直通协议（DES / UDP）。",
            requiresService: true,
            supportsArguments: true),
        new PlannedDebugFeature(
            "server-list-update",
            "服务器列表更新",
            "ops",
            "P5",
            "处理服务器下发的列表更新。"),
        new PlannedDebugFeature(
            "config-update",
            "配置更新",
            "ops",
            "P5",
            "处理配置更新事件。"),
        new PlannedDebugFeature(
            "user-messages",
            "用户消息",
            "ops",
            "P5",
            "读取服务器广播/用户消息。"),
        new PlannedDebugFeature(
            "upgrade-check",
            "升级检查",
            "ops",
            "P8",
            "检查并下载升级包（不自动安装）。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "wireless-profile",
            "无线配置",
            "wireless",
            "P7",
            "创建/删除 WPA2-Enterprise 无线配置。",
            requiresService: true,
            requiresConfirmation: true,
            supportsArguments: true),
        new PlannedDebugFeature(
            "xiaorui-share",
            "小锐 WiFi 分享",
            "wireless",
            "P8",
            "小锐 WiFi / NAT / ICS 分享。",
            requiresService: true,
            requiresConfirmation: true,
            supportsArguments: true),
    ];
}
