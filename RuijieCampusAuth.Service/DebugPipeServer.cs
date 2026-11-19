using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RuijieCampusAuth.Core;
using RuijieCampusAuth.Core.Debug;

namespace RuijieCampusAuth.Service;

public sealed class DebugPipeServer(ILogger<DebugPipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogInformation("debug pipe: non-Windows host, skipped");
            return;
        }
        logger.LogInformation("debug pipe: listening on {Pipe}", DebugPipeProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer();
                await server.WaitForConnectionAsync(stoppingToken);
                await HandleAsync(server, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "debug pipe loop error");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            DebugPipeProtocol.PipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private static async Task HandleAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(
            server,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken);
        }
        catch (IOException)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        DebugPipeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<DebugPipeRequest>(
                line,
                DebugPipeProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            // Fall through with a null request.
        }

        var response = await DispatchAsync(request, cancellationToken);
        try
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(response, DebugPipeProtocol.JsonOptions));
        }
        catch (IOException)
        {
            // GUI 断开（退出/崩溃/闪断）：管道损坏不是错误，静默收线。
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    private static async Task<DebugPipeResponse> DispatchAsync(
        DebugPipeRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return new DebugPipeResponse
            {
                Ok = false,
                Error = "invalid request",
            };
        }
        try
        {
            var store = new UserStore(AppContext.BaseDirectory);
            var settings = store.LoadDebugSettings();
            var registry = DebugFeatureFactory.Create(store, settings);
            switch (request.Action.ToLowerInvariant())
            {
                case "list":
                    return new DebugPipeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Source = "service",
                        Features = await registry.DescribeAsync(cancellationToken),
                    };
                case "status":
                    if (string.IsNullOrWhiteSpace(request.Feature))
                    {
                        return Error(request, "feature required");
                    }
                    return new DebugPipeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Source = "service",
                        Status = await registry.GetStatusAsync(
                            request.Feature,
                            cancellationToken),
                    };
                case "run":
                    if (string.IsNullOrWhiteSpace(request.Feature))
                    {
                        return Error(request, "feature required");
                    }
                    return new DebugPipeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Source = "service",
                        Result = await registry.RunAsync(
                            request.Feature,
                            request.Arguments,
                            cancellationToken),
                    };
                case "stop":
                    if (string.IsNullOrWhiteSpace(request.Feature))
                    {
                        return Error(request, "feature required");
                    }
                    return new DebugPipeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Source = "service",
                        Result = await registry.StopAsync(
                            request.Feature,
                            cancellationToken),
                    };
                case "settings":
                    return new DebugPipeResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Source = "service",
                        Settings = settings,
                    };
                default:
                    return Error(request, $"unknown action: {request.Action}");
            }
        }
        catch (Exception ex)
        {
            return new DebugPipeResponse
            {
                Id = request.Id,
                Ok = false,
                Error = ex.Message,
            };
        }
    }

    private static DebugPipeResponse Error(DebugPipeRequest request, string message)
    {
        return new DebugPipeResponse
        {
            Id = request.Id,
            Ok = false,
            Error = message,
        };
    }
}
