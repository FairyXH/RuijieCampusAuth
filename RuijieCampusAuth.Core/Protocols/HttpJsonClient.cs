using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace RuijieCampusAuth.Core.Protocols;

public sealed record HttpJsonResponse(
    bool Ok,
    int Status,
    string Url,
    string Body,
    JsonNode? Json);

public static class HttpJsonClient
{
    public static async Task<HttpJsonResponse> GetAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "HttpCall");
        return await SendAsync(client, request, cancellationToken);
    }

    public static async Task<HttpJsonResponse> PostJsonAsync(
        string url,
        JsonNode body,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "HttpCall");
        request.Content = new StringContent(
            body.ToJsonString(),
            new UTF8Encoding(false),
            "application/json");
        return await SendAsync(client, request, cancellationToken);
    }

    private static async Task<HttpJsonResponse> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
            }
            catch
            {
                // Non-JSON responses are returned as raw text.
            }
            return new HttpJsonResponse(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                url,
                body,
                json);
        }
        catch (Exception ex)
        {
            return new HttpJsonResponse(false, 0, url, ex.Message, null);
        }
    }
}
