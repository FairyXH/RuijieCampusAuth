namespace RuijieCampusAuth.Core.Debug;

public sealed class DebugFeatureRegistry
{
    private readonly Dictionary<string, IDebugFeature> _features;
    private readonly DebugAuditLog _audit;

    public DebugFeatureRegistry(
        IEnumerable<IDebugFeature> features,
        DebugSettings settings,
        DebugAuditLog audit)
    {
        _features = new Dictionary<string, IDebugFeature>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
        {
            _features[feature.Name] = feature;
        }
        Settings = settings;
        _audit = audit;
    }

    public DebugSettings Settings { get; }

    public IReadOnlyCollection<string> Names => _features.Keys;

    public async Task<IReadOnlyList<DebugFeatureDescriptor>> DescribeAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<DebugFeatureDescriptor>(_features.Count);
        foreach (var feature in _features.Values)
        {
            var status = await feature.GetStatusAsync(cancellationToken);
            var enabled = Settings.IsFeatureEnabled(feature.Name);
            var state = status.State;
            if (state != "planned")
            {
                if (!Settings.Enabled)
                {
                    state = "debug-off";
                }
                else if (!enabled)
                {
                    state = "feature-off";
                }
            }
            result.Add(new DebugFeatureDescriptor(
                feature.Name,
                feature.DisplayName,
                feature.Category,
                feature.Phase,
                feature.Description,
                feature.RequiresService,
                feature.RequiresConfirmation,
                feature.SupportsArguments,
                enabled,
                state,
                status.Detail));
        }
        return result;
    }

    public async Task<DebugFeatureStatus> GetStatusAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var feature = Resolve(name);
        var status = await feature.GetStatusAsync(cancellationToken);
        var enabled = Settings.IsFeatureEnabled(feature.Name);
        var state = status.State;
        if (state != "planned")
        {
            if (!Settings.Enabled)
            {
                state = "debug-off";
            }
            else if (!enabled)
            {
                state = "feature-off";
            }
        }
        return status with { Enabled = enabled, State = state };
    }

    public async Task<DebugFeatureResult> RunAsync(
        string name,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var feature = Resolve(name);
        if (!Settings.Enabled)
        {
            return Deny(feature, "调试功能总开关未启用", argumentsJson);
        }
        if (!Settings.IsFeatureEnabled(name))
        {
            return Deny(feature, "该功能未启用", argumentsJson);
        }

        var started = DateTimeOffset.Now;
        _audit.Write(name, "run-start", true, null, argumentsJson);
        try
        {
            var result = await feature.RunAsync(argumentsJson, cancellationToken);
            _audit.Write(name, "run", result.Ok, result.Message, argumentsJson);
            return result;
        }
        catch (OperationCanceledException)
        {
            var cancelled = new DebugFeatureResult(
                name,
                false,
                "已取消",
                null,
                DateTimeOffset.Now);
            _audit.Write(name, "run", false, "已取消", argumentsJson);
            return cancelled;
        }
        catch (Exception ex)
        {
            var failed = new DebugFeatureResult(
                name,
                false,
                ex.Message,
                null,
                DateTimeOffset.Now);
            _audit.Write(name, "run", false, ex.ToString(), argumentsJson);
            return failed;
        }
        finally
        {
            _ = started;
        }
    }

    public async Task<DebugFeatureResult> StopAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var feature = Resolve(name);
        _audit.Write(name, "stop-start", true, null);
        try
        {
            await feature.StopAsync(cancellationToken);
            _audit.Write(name, "stop", true, null);
            return new DebugFeatureResult(
                name,
                true,
                "已停止",
                null,
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _audit.Write(name, "stop", false, ex.ToString());
            return new DebugFeatureResult(
                name,
                false,
                ex.Message,
                null,
                DateTimeOffset.Now);
        }
    }

    private IDebugFeature Resolve(string name)
    {
        return _features.TryGetValue(name, out var feature)
            ? feature
            : throw new ArgumentException($"unknown debug feature: {name}", nameof(name));
    }

    private DebugFeatureResult Deny(
        IDebugFeature feature,
        string message,
        string? argumentsJson)
    {
        _audit.Write(feature.Name, "denied", false, message, argumentsJson);
        return new DebugFeatureResult(
            feature.Name,
            false,
            message,
            null,
            DateTimeOffset.Now);
    }
}

public static class DebugFeatureFactory
{
    public static DebugFeatureRegistry Create(UserStore store, DebugSettings settings)
    {
        var features = new List<IDebugFeature>
        {
            new SystemInfoDebugFeature(store),
            new PrivatePropertiesDebugFeature(),
            new DhcpClientDebugFeature(store),
            new OuiDetectDebugFeature(store),
            new NicMonitorDebugFeature(),
            new ArpMonitorDebugFeature(store),
            new NetworkProbeDebugFeature(store),
            new ProxyDetectDebugFeature(store),
            new DialupDetectDebugFeature(),
            new DoctorPipeDebugFeature(),
        };
        features.AddRange(DebugFeatureCatalog.Planned);
        return new DebugFeatureRegistry(
            features,
            settings,
            new DebugAuditLog(store.BaseDirectory));
    }
}
