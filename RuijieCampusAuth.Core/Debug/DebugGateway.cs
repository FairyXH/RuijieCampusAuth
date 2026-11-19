namespace RuijieCampusAuth.Core.Debug;

public sealed record DebugListResult(
    IReadOnlyList<DebugFeatureDescriptor> Features,
    string Source);

public sealed class DebugGateway(UserStore? store = null)
{
    private readonly UserStore _store = store ?? new UserStore();
    private readonly DebugPipeClient _client = new();

    public string LastSource { get; private set; } = "local";

    public async Task<DebugListResult> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var remote = await _client.TryListAsync(500, cancellationToken);
        if (remote is { Count: > 0 })
        {
            LastSource = "service";
            return new DebugListResult(remote, LastSource);
        }
        LastSource = "local";
        var registry = CreateLocal();
        return new DebugListResult(
            await registry.DescribeAsync(cancellationToken),
            LastSource);
    }

    public async Task<DebugFeatureResult> RunAsync(
        string feature,
        string? argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var remote = await _client.TryRequestAsync(
            new DebugPipeRequest
            {
                Action = "run",
                Feature = feature,
                Arguments = argumentsJson,
            },
            5000,
            cancellationToken);
        if (remote?.Result is not null)
        {
            LastSource = "service";
            return remote.Result;
        }
        LastSource = "local";
        var registry = CreateLocal();
        return await registry.RunAsync(feature, argumentsJson, cancellationToken);
    }

    public async Task<DebugFeatureResult> StopAsync(
        string feature,
        CancellationToken cancellationToken = default)
    {
        var remote = await _client.TryRequestAsync(
            new DebugPipeRequest { Action = "stop", Feature = feature },
            5000,
            cancellationToken);
        if (remote?.Result is not null)
        {
            LastSource = "service";
            return remote.Result;
        }
        LastSource = "local";
        var registry = CreateLocal();
        return await registry.StopAsync(feature, cancellationToken);
    }

    public DebugSettings LoadDebugSettings()
    {
        return _store.LoadSettings().Debug;
    }

    public void SaveDebugSettings(DebugSettings settings)
    {
        _store.SaveDebugSettings(settings);
    }

    public void SetGlobalEnabled(bool enabled)
    {
        var settings = _store.LoadSettings();
        settings.Debug.Enabled = enabled;
        _store.SaveSettings(settings);
    }

    public void SetFeatureEnabled(string name, bool enabled)
    {
        var settings = _store.LoadSettings();
        settings.Debug.Enabled = true;
        settings.Debug.SetFeature(name, enabled);
        _store.SaveSettings(settings);
    }

    private DebugFeatureRegistry CreateLocal()
    {
        var settings = _store.LoadSettings().Debug;
        return DebugFeatureFactory.Create(_store, settings);
    }
}
