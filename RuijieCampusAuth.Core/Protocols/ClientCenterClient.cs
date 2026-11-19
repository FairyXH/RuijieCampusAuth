using System.Text;

namespace RuijieCampusAuth.Core.Protocols;

public sealed class ClientCenterOptions
{
    public string Url { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Ipv4 { get; set; } = "0";
    public string Product { get; set; } = "";
    public string MainVersion { get; set; } = "";
    public string SubVersion { get; set; } = "";
    public string SchoolCode { get; set; } = "";
    public string OsBits { get; set; } = "32";
    public string ReleaseDate { get; set; } = "";
}

public static class ClientCenterClient
{
    public static string BuildQuery(ClientCenterOptions options)
    {
        var pairs = new (string Key, string Value)[]
        {
            ("mac", options.Mac),
            ("ipv4", options.Ipv4),
            ("ipv61", "0"),
            ("ipv62", "0"),
            ("ipv63", "0"),
            ("ipv64", "0"),
            ("product", options.Product),
            ("mainver", options.MainVersion),
            ("subver", options.SubVersion),
            ("schoolcode", options.SchoolCode),
            ("osbits", options.OsBits),
            ("releasedate", options.ReleaseDate),
        };
        var builder = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }
            builder.Append(key).Append('=').Append(
                Uri.EscapeDataString(value ?? ""));
        }
        return builder.ToString();
    }

    public static string BuildUrl(ClientCenterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            throw new ArgumentException("client center url is empty", nameof(options));
        }
        var separator = options.Url.Contains('?') ? "&" : "?";
        return options.Url + separator + BuildQuery(options);
    }

    public static Task<HttpJsonResponse> QueryAsync(
        ClientCenterOptions options,
        CancellationToken cancellationToken)
    {
        return HttpJsonClient.GetAsync(BuildUrl(options), cancellationToken);
    }
}

public static class SelfServiceClient
{
    public static Task<HttpJsonResponse> GetAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("self-service url is empty", nameof(url));
        }
        return HttpJsonClient.GetAsync(url, cancellationToken);
    }
}
