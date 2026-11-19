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
            "password-change",
            "修改密码",
            "portal",
            "P5",
            "修改校园网账号密码。",
            supportsArguments: true),
        new PlannedDebugFeature(
            "config-update",
            "配置更新",
            "ops",
            "P5",
            "处理配置更新事件。"),
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
