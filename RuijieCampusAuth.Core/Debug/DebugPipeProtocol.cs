using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuijieCampusAuth.Core.Debug;

public static class DebugPipeProtocol
{
    public const string PipeName = "HXXY-CampusAuth-Debug";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class DebugPipeRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "list";
    public string? Feature { get; set; }
    public string? Arguments { get; set; }
}

public sealed class DebugPipeResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string? Source { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<DebugFeatureDescriptor>? Features { get; set; }
    public DebugFeatureStatus? Status { get; set; }
    public DebugFeatureResult? Result { get; set; }
    public DebugSettings? Settings { get; set; }
}

public sealed class DebugPipeClient(string pipeName = DebugPipeProtocol.PipeName)
{
    public async Task<DebugPipeResponse?> TryRequestAsync(
        DebugPipeRequest request,
        int timeoutMilliseconds = 500,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(request, DebugPipeProtocol.JsonOptions));
            var line = await reader.ReadLineAsync(timeout.Token);
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<DebugPipeResponse>(
                    line,
                    DebugPipeProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DebugFeatureDescriptor>?> TryListAsync(
        int timeoutMilliseconds = 500,
        CancellationToken cancellationToken = default)
    {
        var response = await TryRequestAsync(
            new DebugPipeRequest { Action = "list" },
            timeoutMilliseconds,
            cancellationToken);
        return response is { Ok: true, Features.Count: > 0 }
            ? response.Features
            : null;
    }
}
