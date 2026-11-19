using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Debug;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public sealed class PortalTests
{
    [Fact]
    public async Task Login_posts_expected_json_to_eportal_path()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream);
            const string payload = "{\"success\":true}";
            var body = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                       $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n" +
                       payload;
            var bytes = Encoding.ASCII.GetBytes(body);
            await stream.WriteAsync(bytes);
            return request;
        });

        var response = await PortalClient.LoginAsync(
            new PortalRequestOptions
            {
                PortalUrl = $"http://127.0.0.1:{port}/",
                UserId = "user1",
                Password = "pass1",
                Service = "服务PC",
                QueryString = "q=1",
                UserIp = "10.0.0.2",
                UserAgent = "HttpClient",
            },
            CancellationToken.None);
        var captured = await server;
        listener.Stop();

        Assert.True(response.Ok);
        Assert.Contains("/eportal/inferface/authAPI/login", captured.Path);
        Assert.Contains("Content-Type: application/json", captured.Headers);
        var json = JsonNode.Parse(captured.Body)!;
        Assert.Equal("user1", json["userId"]!.GetValue<string>());
        Assert.Equal("pass1", json["password"]!.GetValue<string>());
        Assert.Equal("服务PC", json["service"]!.GetValue<string>());
        Assert.Equal("q=1", json["queryString"]!.GetValue<string>());
        Assert.Equal("10.0.0.2", json["userip"]!.GetValue<string>());
        Assert.Equal("HttpClient", json["userAgent"]!.GetValue<string>());
    }

    [Fact]
    public void Client_center_query_contains_expected_fields()
    {
        var url = ClientCenterClient.BuildUrl(new ClientCenterOptions
        {
            Url = "http://center.example/api",
            Mac = "00:1a:a9:17:ff:ff",
            Ipv4 = "10.0.0.2",
            Product = "1",
            MainVersion = "2",
            SubVersion = "3",
            SchoolCode = "EXAM",
            OsBits = "64",
            ReleaseDate = "2026.11.19",
        });
        Assert.Contains("mac=00%3A1a%3Aa9%3A17%3Aff%3Aff", url);
        Assert.Contains("ipv61=0", url);
        Assert.Contains("schoolcode=EXAM", url);
        Assert.Contains("osbits=64", url);
        Assert.Contains("releasedate=2026.11.19", url);
    }

    [Fact]
    public async Task Server_list_update_reads_tlv_66_and_119()
    {
        var store = new UserStore(Path.Combine(
            Path.GetTempPath(),
            "ruijie-portal-" + Guid.NewGuid().ToString("N")));
        var settings = new DebugSettings { Enabled = true };
        settings.SetFeature("server-list-update", true);
        var registry = DebugFeatureFactory.Create(store, settings);

        var payload = PrivatePropertiesTests.BuildSectioned(
            [],
            (66, Encoding.ASCII.GetBytes("http://portal.example/")),
            (119, [0x01]));
        var result = await registry.RunAsync(
            "server-list-update",
            "{\"hex\":\"" + Convert.ToHexString(payload).ToLowerInvariant() + "\"}",
            CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var json = JsonNode.Parse(result.Data!)!;
        var entries = json["entries"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal("SERVER_LIST_UPDATE", entries[0]!["name"]!.GetValue<string>());
        Assert.Equal("http://portal.example/", entries[0]!["value"]!.GetValue<string>());
        Assert.Equal("1", entries[1]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task User_messages_extracts_broadcast_and_reject_text()
    {
        var store = new UserStore(Path.Combine(
            Path.GetTempPath(),
            "ruijie-msg-" + Guid.NewGuid().ToString("N")));
        var settings = new DebugSettings { Enabled = true };
        settings.SetFeature("user-messages", true);
        var registry = DebugFeatureFactory.Create(store, settings);
        var payload = PrivatePropertiesTests.BuildSectioned(
            [],
            (82, Encoding.ASCII.GetBytes("hello")),
            (83, Encoding.ASCII.GetBytes("denied")));
        var result = await registry.RunAsync(
            "user-messages",
            "{\"hex\":\"" + Convert.ToHexString(payload).ToLowerInvariant() + "\"}",
            CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        var messages = JsonNode.Parse(result.Data!)!["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("hello", messages[0]!["text"]!.GetValue<string>());
        Assert.Equal("denied", messages[1]!["text"]!.GetValue<string>());
    }

    private static void Add(List<byte> payload, byte type, byte[] value)
    {
        payload.Add(type);
        payload.Add((byte)value.Length);
        payload.AddRange(value);
    }

    private static async Task<(string Path, string Headers, string Body)> ReadRequestAsync(
        NetworkStream stream)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        var headerEnd = -1;
        while (headerEnd < 0 && total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }
            total += read;
            headerEnd = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
        }
        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd + 4);
        var contentLength = 0;
        foreach (var line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line[15..].Trim());
            }
        }
        var bodyStart = headerEnd + 4;
        while (total - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        var body = Encoding.UTF8.GetString(buffer, bodyStart, contentLength);
        var firstLine = headerText.Split("\r\n")[0];
        var parts = firstLine.Split(' ');
        return (parts.Length > 1 ? parts[1] : "", headerText, body);
    }
}
