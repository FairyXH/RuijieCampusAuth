using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core.Protocols;

public sealed class PortalRequestOptions
{
    public string PortalUrl { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public string Service { get; set; } = "";
    public string QueryString { get; set; } = "";
    public string UserIp { get; set; } = "";
    public string UserAgent { get; set; } = "HttpClient";
    public string UserIndex { get; set; } = "";
    public JsonObject? Extra { get; set; }
}

public static class PortalClient
{
    public const string LoginPath = "eportal/inferface/authAPI/login";
    public const string LogoutPath = "eportal/inferface/authAPI/logout";
    public const string OnlinePath = "eportal/inferface/authAPI/getOnlineUserInfo";
    public const string PageInfoPath = "eportal/inferface/authAPI/pageInfo";

    public static Task<HttpJsonResponse> LoginAsync(
        PortalRequestOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["userId"] = options.UserId,
            ["password"] = options.Password,
            ["service"] = options.Service,
            ["queryString"] = options.QueryString,
            ["userAgent"] = options.UserAgent,
            ["userip"] = options.UserIp,
        };
        Merge(body, options.Extra);
        return HttpJsonClient.PostJsonAsync(
            BuildUrl(options.PortalUrl, LoginPath),
            body,
            cancellationToken);
    }

    public static Task<HttpJsonResponse> LogoutAsync(
        PortalRequestOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["userip"] = options.UserIp,
            ["userIndex"] = options.UserIndex,
        };
        Merge(body, options.Extra);
        return HttpJsonClient.PostJsonAsync(
            BuildUrl(options.PortalUrl, LogoutPath),
            body,
            cancellationToken);
    }

    public static Task<HttpJsonResponse> GetOnlineUserInfoAsync(
        PortalRequestOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["userip"] = options.UserIp,
            ["userIndex"] = options.UserIndex,
        };
        Merge(body, options.Extra);
        return HttpJsonClient.PostJsonAsync(
            BuildUrl(options.PortalUrl, OnlinePath),
            body,
            cancellationToken);
    }

    public static Task<HttpJsonResponse> PageInfoAsync(
        PortalRequestOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["userip"] = options.UserIp,
            ["userIndex"] = options.UserIndex,
        };
        Merge(body, options.Extra);
        return HttpJsonClient.PostJsonAsync(
            BuildUrl(options.PortalUrl, PageInfoPath),
            body,
            cancellationToken);
    }

    public static string BuildUrl(string portalUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(portalUrl))
        {
            throw new ArgumentException("portalUrl is empty", nameof(portalUrl));
        }
        return portalUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    private static void Merge(JsonObject body, JsonObject? extra)
    {
        if (extra is null)
        {
            return;
        }
        foreach (var pair in extra)
        {
            body[pair.Key] = pair.Value?.DeepClone();
        }
    }
}
