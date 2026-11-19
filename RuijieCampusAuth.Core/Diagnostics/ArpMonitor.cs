using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RuijieCampusAuth.Core.Diagnostics;

public sealed record ArpObservation(DateTimeOffset Time, string Ip, string Mac);

public sealed record ArpMonitorResult(
    string Interface,
    string Gateway,
    string? InitialMac,
    string? FinalMac,
    IReadOnlyList<string> Changes,
    IReadOnlyList<ArpObservation>? Observations);

public static class ArpMonitor
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destination, uint source, byte[] mac, ref uint length);

    public static string? ResolveMac(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }
        var mac = new byte[6];
        uint length = (uint)mac.Length;
        var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        var rc = SendARP(destination, 0, mac, ref length);
        if (rc != 0 || length < 6)
        {
            return null;
        }
        return string.Join(":", mac.Select(value => value.ToString("x2")));
    }

    public static async Task<ArpMonitorResult> MonitorAsync(
        string interfaceHint,
        TimeSpan duration,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var nic = NicInventory.Resolve(interfaceHint)
                  ?? throw new InvalidOperationException(
                      $"interface not found: {interfaceHint}");
        var gateway = NicInventory.GatewayOf(nic);
        if (string.IsNullOrWhiteSpace(gateway))
        {
            throw new InvalidOperationException($"no IPv4 gateway on {nic.Name}");
        }

        var observations = new List<ArpObservation>();
        var changes = new List<string>();
        string? initial = null;
        string? previous = null;
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mac = ResolveMac(gateway);
            var now = DateTimeOffset.Now;
            observations.Add(new ArpObservation(now, gateway, mac ?? "(no reply)"));
            initial ??= mac;
            if (previous is not null && !string.Equals(previous, mac, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"{now:HH:mm:ss} {previous} -> {mac ?? "(no reply)"}");
            }
            previous = mac;
            await Task.Delay(interval, cancellationToken);
        }

        return new ArpMonitorResult(
            nic.Name,
            gateway,
            initial,
            previous,
            changes,
            observations);
    }
}
