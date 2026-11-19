using Microsoft.Extensions.Logging.Abstractions;
using RuijieCampusAuth.Core.Debug;
using RuijieCampusAuth.Service;

namespace RuijieCampusAuth.Service.Tests;

public sealed class DebugPipeTests
{
    [Fact]
    public async Task Pipe_serves_list_and_run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var server = new DebugPipeServer(
            NullLogger<DebugPipeServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            var client = new DebugPipeClient();
            IReadOnlyList<DebugFeatureDescriptor>? features = null;
            for (var attempt = 0; attempt < 20 && features is null; attempt++)
            {
                await Task.Delay(100);
                features = await client.TryListAsync(1000);
            }

            Assert.NotNull(features);
            Assert.Contains(features!, item => item.Name == "system-info");
            Assert.Contains(features!, item => item.Name == "peap");

            var settings = await client.TryRequestAsync(
                new DebugPipeRequest { Action = "settings" },
                1000);
            Assert.NotNull(settings);
            Assert.True(settings!.Ok);
            Assert.NotNull(settings.Settings);

            var run = await client.TryRequestAsync(
                new DebugPipeRequest
                {
                    Action = "run",
                    Feature = "system-info",
                },
                2000);
            Assert.NotNull(run);
            Assert.NotNull(run!.Result);
            Assert.False(run.Result!.Ok);
            Assert.Contains("总开关", run.Result.Message);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
        }
    }
}
