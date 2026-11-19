using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RuijieCampusAuth.Core.Protocols;

public enum DhcpOperation
{
    Status,
    Renew,
    Release,
    ReleaseRenew,
}

public sealed record DhcpLease(
    string Interface,
    string AdapterName,
    uint AdapterIndex,
    string Ip,
    string Mask,
    string Gateway);

public sealed record DhcpOperationResult(
    bool Ok,
    string Message,
    DhcpOperation Operation,
    DhcpLease? Lease,
    bool Option82Requested,
    TimeSpan Elapsed);

public static class DhcpControl
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IpAdapterIndexMap
    {
        public uint Index;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;
    }

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    private static extern int GetInterfaceInfo(IntPtr interfaceInfo, ref uint size);

    [DllImport("iphlpapi.dll")]
    private static extern int IpReleaseAddress(ref IpAdapterIndexMap adapter);

    [DllImport("iphlpapi.dll")]
    private static extern int IpRenewAddress(ref IpAdapterIndexMap adapter);

    public static async Task<DhcpOperationResult> RunAsync(
        string interfaceHint,
        DhcpOperation operation,
        TimeSpan timeout,
        bool option82,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var adapters = EnumerateAdapters();
        var nic = PrivateTail.FindInterface(interfaceHint)
                  ?? throw new InvalidOperationException(
                      $"interface not found: {interfaceHint}");
        var matches = adapters.Where(item => AdapterMatches(item.Name, nic)).ToList();
        if (matches.Count == 0)
        {
            var available = string.Join(", ", adapters.Select(item => item.Name));
            throw new InvalidOperationException(
                $"adapter index not found for {nic.Name} ({nic.Description}); available: {available}");
        }
        var adapter = matches[0];

        var messages = new List<string>();
        if (operation == DhcpOperation.Status)
        {
            var statusLease = ReadLease(nic, adapter);
            return new DhcpOperationResult(
                statusLease is not null,
                statusLease is null ? "no IPv4 lease" : "lease read",
                operation,
                statusLease,
                option82,
                stopwatch.Elapsed);
        }
        if (operation is DhcpOperation.Release or DhcpOperation.ReleaseRenew)
        {
            var rc = IpReleaseAddress(ref adapter);
            messages.Add($"IpReleaseAddress={rc}");
            if (rc != ErrorSuccess)
            {
                return new DhcpOperationResult(
                    false,
                    $"IpReleaseAddress failed: {rc}",
                    operation,
                    ReadLease(nic, adapter),
                    option82,
                    stopwatch.Elapsed);
            }
        }

        if (operation is DhcpOperation.Renew or DhcpOperation.ReleaseRenew)
        {
            var rc = IpRenewAddress(ref adapter);
            messages.Add($"IpRenewAddress={rc}");
            if (rc != ErrorSuccess)
            {
                return new DhcpOperationResult(
                    false,
                    $"IpRenewAddress failed: {rc}",
                    operation,
                    ReadLease(nic, adapter),
                    option82,
                    stopwatch.Elapsed);
            }
            await WaitForLeaseAsync(nic, timeout, cancellationToken);
        }

        var lease = ReadLease(nic, adapter);
        var ok = operation == DhcpOperation.Release
                 || (lease is not null && !string.IsNullOrWhiteSpace(lease.Ip));
        return new DhcpOperationResult(
            ok,
            ok
                ? string.Join("; ", messages)
                : $"{string.Join("; ", messages)}; no IPv4 lease within {timeout.TotalSeconds:0}s",
            operation,
            lease,
            option82,
            stopwatch.Elapsed);
    }

    private static bool AdapterMatches(string adapterName, NetworkInterface nic)
    {
        if (string.Equals(adapterName, nic.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(adapterName, nic.Description, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var match = Regex.Match(
            adapterName,
            "\\{([0-9a-fA-F-]{36})\\}",
            RegexOptions.CultureInvariant);
        return match.Success &&
               string.Equals(
                   match.Groups[1].Value,
                   nic.Id.Trim('{', '}'),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static List<IpAdapterIndexMap> EnumerateAdapters()
    {
        uint size = 0;
        var rc = GetInterfaceInfo(IntPtr.Zero, ref size);
        if (rc != ErrorInsufficientBuffer && rc != ErrorSuccess)
        {
            throw new InvalidOperationException($"GetInterfaceInfo size probe failed: {rc}");
        }
        if (size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = GetInterfaceInfo(buffer, ref size);
            if (rc != ErrorSuccess)
            {
                throw new InvalidOperationException($"GetInterfaceInfo failed: {rc}");
            }
            var count = Marshal.ReadInt32(buffer);
            var structSize = Marshal.SizeOf<IpAdapterIndexMap>();
            var list = new List<IpAdapterIndexMap>(count);
            for (var i = 0; i < count; i++)
            {
                var pointer = IntPtr.Add(buffer, 4 + i * structSize);
                list.Add(Marshal.PtrToStructure<IpAdapterIndexMap>(pointer));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static async Task WaitForLeaseAsync(
        NetworkInterface nic,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadLease(nic, null) is { } lease &&
                !string.IsNullOrWhiteSpace(lease.Ip) &&
                !lease.Ip.StartsWith("169.254.", StringComparison.Ordinal))
            {
                return;
            }
            await Task.Delay(500, cancellationToken);
        }
    }

    private static DhcpLease? ReadLease(
        NetworkInterface nic,
        IpAdapterIndexMap? adapter)
    {
        var properties = nic.GetIPProperties();
        var unicast = properties.UnicastAddresses.FirstOrDefault(item =>
            item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var gateway = properties.GatewayAddresses.FirstOrDefault(item =>
            item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (unicast is null)
        {
            return null;
        }
        return new DhcpLease(
            nic.Id,
            adapter?.Name ?? nic.Name,
            adapter?.Index ?? 0,
            unicast.Address.ToString(),
            unicast.IPv4Mask?.ToString() ?? "",
            gateway?.Address.ToString() ?? "");
    }
}
