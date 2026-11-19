using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RuijieCampusAuth.Core.Diagnostics;

public sealed record ProxyProbeResult(
    string Gateway,
    IReadOnlyList<int> OpenPorts,
    string? EnvironmentProxy);

public static class ProxyDetect
{
    public static readonly int[] DefaultPorts = [1080, 3128, 8000, 8080, 8888];

    public static async Task<ProxyProbeResult> ProbeAsync(
        string gateway,
        IReadOnlyList<int> ports,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var open = new List<int>();
        if (!string.IsNullOrWhiteSpace(gateway))
        {
            foreach (var port in ports)
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(Math.Max(100, timeoutMilliseconds));
                try
                {
                    await client.ConnectAsync(gateway, port, timeout.Token);
                    if (client.Connected)
                    {
                        open.Add(port);
                    }
                }
                catch
                {
                    // Closed, filtered or timed out.
                }
            }
        }
        var environment = Environment.GetEnvironmentVariable("HTTP_PROXY")
                          ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
                          ?? Environment.GetEnvironmentVariable("ALL_PROXY");
        return new ProxyProbeResult(gateway, open, environment);
    }

    public static IReadOnlyList<string> DetectDialup()
    {
        var reasons = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }
            var type = nic.NetworkInterfaceType;
            var name = $"{nic.Name} | {nic.Description}";
            if (type is NetworkInterfaceType.Ppp
                or NetworkInterfaceType.Wwanpp
                or NetworkInterfaceType.Wwanpp2)
            {
                reasons.Add($"{name} ({type})");
                continue;
            }
            if (name.Contains("拨号", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PPP", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("4G", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("5G", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"{name} ({type})");
            }
        }
        return reasons;
    }
}
