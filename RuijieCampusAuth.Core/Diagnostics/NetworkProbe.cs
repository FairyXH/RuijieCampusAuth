using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace RuijieCampusAuth.Core.Diagnostics;

public sealed record NetworkProbeResult(
    string Interface,
    string Gateway,
    bool GatewayReachable,
    long? GatewayMs,
    bool DnsOk,
    string DnsDetail,
    bool HttpOk,
    int? HttpStatus,
    string Detail,
    long ElapsedMs);

public static class NetworkProbe
{
    public static async Task<NetworkProbeResult> RunAsync(
        string? interfaceHint,
        string url,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nic = string.IsNullOrWhiteSpace(interfaceHint)
            ? null
            : NicInventory.Resolve(interfaceHint);
        var gateway = nic is null ? "" : NicInventory.GatewayOf(nic);
        var gatewayReachable = false;
        long? gatewayMs = null;
        var detail = new List<string>();

        if (!string.IsNullOrWhiteSpace(gateway))
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(gateway, 2000);
                gatewayReachable = reply.Status == IPStatus.Success;
                gatewayMs = gatewayReachable ? reply.RoundtripTime : null;
                detail.Add($"gateway={gateway} ping={reply.Status}");
            }
            catch (Exception ex)
            {
                detail.Add($"gateway ping failed: {ex.Message}");
            }
        }
        else
        {
            detail.Add("gateway=unavailable");
        }

        var dnsOk = false;
        var dnsDetail = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                dnsOk = addresses.Length > 0;
                dnsDetail = $"{uri.Host} -> {string.Join(",", addresses.Take(2))}";
            }
            catch (Exception ex)
            {
                dnsDetail = $"{uri.Host} -> {ex.Message}";
            }
        }
        else
        {
            dnsDetail = $"invalid url: {url}";
        }

        var httpOk = false;
        int? httpStatus = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(5),
                };
                using var response = await client.GetAsync(uri, cancellationToken);
                httpOk = true;
                httpStatus = (int)response.StatusCode;
                detail.Add($"http={httpStatus}");
            }
            catch (Exception ex)
            {
                detail.Add($"http failed: {ex.Message}");
            }
        }

        return new NetworkProbeResult(
            nic?.Name ?? "(default)",
            gateway,
            gatewayReachable,
            gatewayMs,
            dnsOk,
            dnsDetail,
            httpOk,
            httpStatus,
            string.Join("; ", detail),
            (long)stopwatch.ElapsedMilliseconds);
    }
}
