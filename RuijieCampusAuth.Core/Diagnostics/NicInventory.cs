using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RuijieCampusAuth.Core.Diagnostics;

public sealed record NicInfo(
    string Id,
    string Name,
    string Description,
    string Type,
    string Status,
    string Mac,
    long SpeedBitsPerSecond,
    string Ipv4,
    string Mask,
    string Gateway);

public static class NicInventory
{
    private static readonly string[] FilterMarkers =
    [
        "Npcap Packet Driver",
        "Kaspersky Lab NDIS",
        "WFP Native MAC Layer",
        "WFP 802.3 MAC Layer",
        "QoS Packet Scheduler",
        "Virtual WiFi Filter",
        "Native WiFi Filter",
        "Microsoft Wi-Fi Direct",
        "Microsoft Teredo",
        "IP-HTTPS Platform",
        "6to4 Adapter",
        "WAN Miniport",
    ];

    public static IReadOnlyList<NicInfo> List()
    {
        var list = new List<NicInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (FilterMarkers.Any(marker =>
                    nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var properties = nic.GetIPProperties();
            var unicast = properties.UnicastAddresses.FirstOrDefault(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork);
            var gateway = properties.GatewayAddresses.FirstOrDefault(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork);
            var mac = PrivateTail.NormalizeMac(nic.GetPhysicalAddress().ToString());
            var key = $"{mac}|{nic.Description}";
            if (!seen.Add(key))
            {
                continue;
            }
            list.Add(new NicInfo(
                nic.Id,
                nic.Name,
                nic.Description,
                nic.NetworkInterfaceType.ToString(),
                nic.OperationalStatus.ToString(),
                mac,
                nic.Speed,
                unicast?.Address.ToString() ?? "",
                unicast?.IPv4Mask?.ToString() ?? "",
                gateway?.Address.ToString() ?? ""));
        }
        return list;
    }

    public static NetworkInterface? Resolve(string? hint)
    {
        return PrivateTail.FindInterface(hint);
    }

    public static string GatewayOf(NetworkInterface nic)
    {
        return nic.GetIPProperties().GatewayAddresses
            .FirstOrDefault(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "";
    }
}
