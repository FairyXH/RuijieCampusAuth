namespace RuijieCampusAuth.Core.Diagnostics;

public static class OuiDetect
{
    // Known Ruijie OUIs. Extend as new device families are observed.
    private static readonly Dictionary<string, string> Vendors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["00:1A:A9"] = "Ruijie Networks",
            ["00:74:9C"] = "Ruijie Networks",
            ["00:23:89"] = "Ruijie Networks",
        };

    public static string VendorOf(string? mac)
    {
        var normalized = PrivateTail.NormalizeMac(mac);
        if (normalized.Length < 8)
        {
            return "Unknown";
        }
        var oui = normalized[..8];
        return Vendors.TryGetValue(oui, out var vendor) ? vendor : "Unknown";
    }

    public static bool IsRuijie(string? mac)
    {
        return VendorOf(mac) != "Unknown";
    }
}
