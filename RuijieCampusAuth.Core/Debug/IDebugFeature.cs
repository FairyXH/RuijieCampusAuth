namespace RuijieCampusAuth.Core.Debug;

public interface IDebugFeature
{
    string Name { get; }
    string DisplayName { get; }
    string Category { get; }
    string Phase { get; }
    string Description { get; }
    bool RequiresService { get; }
    bool RequiresConfirmation { get; }
    bool SupportsArguments { get; }

    Task<DebugFeatureStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public abstract class DebugFeatureBase : IDebugFeature
{
    public abstract string Name { get; }
    public virtual string DisplayName => Name;
    public virtual string Category => "system";
    public virtual string Phase => "P1";
    public virtual string Description => "";
    public virtual bool RequiresService => false;
    public virtual bool RequiresConfirmation => false;
    public virtual bool SupportsArguments => false;

    public virtual Task<DebugFeatureStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new DebugFeatureStatus(
            Name,
            false,
            "available",
            "",
            DateTimeOffset.Now));
    }

    public abstract Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken);

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected DebugFeatureResult Ok(string message, string? data = null)
    {
        return new DebugFeatureResult(
            Name,
            true,
            message,
            data,
            DateTimeOffset.Now);
    }

    protected DebugFeatureResult Fail(string message)
    {
        return new DebugFeatureResult(
            Name,
            false,
            message,
            null,
            DateTimeOffset.Now);
    }
}
