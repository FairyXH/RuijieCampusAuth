using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core.Debug;

/// <summary>
/// Builds the pre-filled arguments JSON the GUI debug page shows when a
/// feature is selected. Keys mirror each feature's DebugArgs lookups; every
/// value comes from the current school configuration or the active account,
/// so what the user sees is exactly what the server-side fallback would use.
/// </summary>
public static class DebugArgumentDefaults
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string Build(string? featureName, UserSettings settings, UserAccount? account)
    {
        var node = BuildNode(featureName, settings, account);
        return node is null
            ? "{}"
            : node.ToJsonString(JsonOptions);
    }

    private static JsonObject? BuildNode(string? featureName, UserSettings settings, UserAccount? account)
    {
        var node = new JsonObject();
        switch ((featureName ?? "").ToLowerInvariant())
        {
            case "portal-auth":
                node["action"] = "login";
                Put(node, "portalUrl", settings.PortalUrl);
                Put(node, "userId", account?.Username);
                Put(node, "password", account?.Password);
                Put(node, "service", settings.ServiceName);
                break;

            case "portal-udp":
                node["interval"] = 5;
                node["duration"] = 30;
                break;

            case "user-messages":
            case "server-list-update":
                Put(node, "hex");
                break;

            case "client-center":
                Put(node, "ipv4");
                node["product"] = "33554432";
                node["mainver"] = "100663296";
                node["subver"] = "1442840576";
                Put(node, "schoolcode", settings.SchoolCode);
                node["osbits"] = "64";
                break;

            case "self-service":
                Put(node, "url", settings.SelfServiceUrl);
                break;

            case "upgrade-check":
                Put(node, "url", settings.UpdateUrl);
                break;

            case "sam-direct":
                Put(node, "host", settings.SamHost);
                node["port"] = settings.SamPort > 0 ? settings.SamPort : 53;
                node["timeout_ms"] = 2000;
                break;

            case "peap":
            case "eap-tls":
                Put(node, "interface", settings.Interface);
                Put(node, "username", account?.Username);
                Put(node, "password", account?.Password);
                node["verify_cert"] = false;
                node["max_fragment"] = 1200;
                node["timeout"] = settings.Timeout;
                break;

            case "dhcp-client":
                Put(node, "interface", settings.Interface);
                node["operation"] = "renew";
                node["timeout"] = settings.DhcpTimeout > 0 ? settings.DhcpTimeout : 30;
                node["option82"] = settings.DhcpOption82;
                break;

            case "network-probe":
                Put(node, "interface", settings.Interface);
                Put(node, "url", settings.ConnectivityUrl);
                break;

            case "wireless-profile":
                Put(node, "interface", settings.Interface);
                Put(node, "ssid");
                Put(node, "identity", account?.Username);
                Put(node, "password", account?.Password);
                node["authentication"] = "WPA2Enterprise";
                node["encryption"] = "AES";
                break;

            case "oui-detect":
                Put(node, "mac");
                break;

            case "arp-monitor":
            case "nic-monitor":
            case "proxy-detect":
            case "dialup-detect":
            case "doctor-pipe":
            case "system-info":
                Put(node, "interface", settings.Interface);
                break;

            default:
                return null;
        }
        return node;
    }

    private static void Put(JsonObject node, string key, string? value = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            node[key] = value;
        }
    }
}
