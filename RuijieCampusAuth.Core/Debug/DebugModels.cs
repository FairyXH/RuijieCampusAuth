using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core.Debug;

public sealed class DebugEndpoints
{
    public string Portal { get; set; } = "";
    public string ClientCenter { get; set; } = "";
    public string Upgrade { get; set; } = "";
    public string SelfService { get; set; } = "";
}

public sealed class DebugTlsSettings
{
    public bool Verify { get; set; } = true;
    public string CaPath { get; set; } = "";
}

public sealed class DebugSettings
{
    public bool Enabled { get; set; }
    public Dictionary<string, bool> Features { get; set; } = new();
    public DebugEndpoints Endpoints { get; set; } = new();
    public DebugTlsSettings Tls { get; set; } = new();
    public string LogLevel { get; set; } = "info";

    public bool IsFeatureEnabled(string name)
    {
        return Enabled && Features.TryGetValue(name, out var value) && value;
    }

    public void SetFeature(string name, bool enabled)
    {
        Features[name] = enabled;
    }
}

public sealed record DebugFeatureDescriptor(
    string Name,
    string DisplayName,
    string Category,
    string Phase,
    string Description,
    bool RequiresService,
    bool RequiresConfirmation,
    bool SupportsArguments,
    bool Enabled,
    string State,
    string Detail);

public sealed record DebugFeatureStatus(
    string Name,
    bool Enabled,
    string State,
    string Detail,
    DateTimeOffset ObservedAt);

public sealed record DebugFeatureResult(
    string Name,
    bool Ok,
    string Message,
    string? Data,
    DateTimeOffset FinishedAt)
{
    public static DebugFeatureResult FromJson(string name, bool ok, string message, JsonNode? data)
    {
        return new DebugFeatureResult(
            name,
            ok,
            message,
            data?.ToJsonString() ?? null,
            DateTimeOffset.Now);
    }
}
