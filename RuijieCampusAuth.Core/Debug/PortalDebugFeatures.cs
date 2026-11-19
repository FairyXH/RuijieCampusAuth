using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Diagnostics;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

public sealed class PortalDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "portal-auth";
    public override string DisplayName => "Portal 认证";
    public override string Category => "portal";
    public override string Phase => "P5";
    public override string Description =>
        "eportal login/logout/getOnlineUserInfo/pageInfo（JSON POST，jsoncpp）。";
    public override bool SupportsArguments => true;

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Fail(error);
        }
        var settings = store.LoadSettings();
        var action = (DebugArgs.String(args, "action") ?? "login").ToLowerInvariant();
        var portalUrl = DebugArgs.FirstNonEmpty(
            DebugArgs.String(args, "portalUrl"),
            settings.PortalUrl,
            settings.Debug.Endpoints.Portal);
        if (string.IsNullOrWhiteSpace(portalUrl))
        {
            return Fail("缺少 portalUrl：请传 {\"portalUrl\":\"http://...\"} 或在档案中配置");
        }

        var account = store.LoadAccounts().FirstOrDefault(item =>
                          item.Id == settings.ActiveAccountId)
                      ?? store.LoadAccounts().FirstOrDefault(item => item.Enabled);
        var userId = DebugArgs.String(args, "userId") ?? account?.Username ?? "";
        var password = DebugArgs.String(args, "password") ?? account?.Password ?? "";
        var service = DebugArgs.String(args, "service") ?? settings.ServiceName;
        if (!string.IsNullOrWhiteSpace(service) &&
            !service.EndsWith("PC", StringComparison.Ordinal))
        {
            service += "PC";
        }
        var nic = NicInventory.Resolve(settings.Interface);
        var userIp = DebugArgs.String(args, "userIp");
        if (string.IsNullOrWhiteSpace(userIp) && nic is not null)
        {
            userIp = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(item =>
                    item.Address.AddressFamily ==
                    System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "";
        }

        var options = new PortalRequestOptions
        {
            PortalUrl = portalUrl,
            UserId = userId,
            Password = password,
            Service = service,
            QueryString = DebugArgs.String(args, "queryString") ?? "",
            UserIp = userIp ?? "",
            UserAgent = DebugArgs.String(args, "userAgent") ?? "HttpClient",
            UserIndex = DebugArgs.String(args, "userIndex") ?? "",
            Extra = args?["extra"] as JsonObject,
        };

        HttpJsonResponse response;
        try
        {
            response = action switch
            {
                "login" => await PortalClient.LoginAsync(options, cancellationToken),
                "logout" => await PortalClient.LogoutAsync(options, cancellationToken),
                "online" => await PortalClient.GetOnlineUserInfoAsync(
                    options,
                    cancellationToken),
                "pageinfo" => await PortalClient.PageInfoAsync(
                    options,
                    cancellationToken),
                _ => throw new ArgumentException(
                    $"未知 action: {action}（login/logout/online/pageinfo）"),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        var data = new JsonObject
        {
            ["action"] = action,
            ["url"] = response.Url,
            ["status"] = response.Status,
            ["ok"] = response.Ok,
            ["body"] = response.Body,
            ["json"] = response.Json?.DeepClone(),
        };
        return new DebugFeatureResult(
            Name,
            response.Ok,
            response.Ok
                ? $"Portal {action} HTTP {response.Status}"
                : $"Portal {action} 失败 HTTP {response.Status}",
            data.ToJsonString(),
            DateTimeOffset.Now);
    }
}

public sealed class ClientCenterDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "client-center";
    public override string DisplayName => "客户端中心";
    public override string Category => "portal";
    public override string Phase => "P5";
    public override string Description =>
        "按原版查询串上报/查询客户端中心（mac/ipv4/product/version/schoolcode）。";
    public override bool SupportsArguments => true;

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Fail(error);
        }
        var settings = store.LoadSettings();
        var url = DebugArgs.FirstNonEmpty(
            DebugArgs.String(args, "url"),
            settings.Debug.Endpoints.ClientCenter);
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail("缺少客户端中心 URL：请传 {\"url\":\"http://...\"}");
        }
        var nic = NicInventory.Resolve(settings.Interface);
        var mac = nic is null
            ? ""
            : PrivateTail.NormalizeMac(nic.GetPhysicalAddress().ToString());
        var ipv4 = nic?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(item =>
                item.Address.AddressFamily ==
                System.Net.Sockets.AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "0";
        var options = new ClientCenterOptions
        {
            Url = url,
            Mac = mac,
            Ipv4 = DebugArgs.String(args, "ipv4") ?? ipv4,
            Product = DebugArgs.String(args, "product") ?? "33554432",
            MainVersion = DebugArgs.String(args, "mainver") ?? "100663296",
            SubVersion = DebugArgs.String(args, "subver") ?? "1442840576",
            SchoolCode = DebugArgs.String(args, "schoolcode") ?? settings.SchoolCode,
            OsBits = DebugArgs.String(args, "osbits")
                     ?? (Environment.Is64BitProcess ? "64" : "32"),
            ReleaseDate = DebugArgs.String(args, "releasedate")
                          ?? DateTime.Now.ToString("yyyy.MM.dd"),
        };
        var response = await ClientCenterClient.QueryAsync(options, cancellationToken);
        var data = new JsonObject
        {
            ["url"] = response.Url,
            ["status"] = response.Status,
            ["ok"] = response.Ok,
            ["body"] = response.Body,
            ["json"] = response.Json?.DeepClone(),
        };
        return new DebugFeatureResult(
            Name,
            response.Ok,
            response.Ok
                ? $"客户端中心 HTTP {response.Status}"
                : $"客户端中心失败 HTTP {response.Status}",
            data.ToJsonString(),
            DateTimeOffset.Now);
    }
}

public sealed class SelfServiceDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "self-service";
    public override string DisplayName => "自助服务/余额";
    public override string Category => "portal";
    public override string Phase => "P5";
    public override string Description =>
        "GET 自助服务地址并返回原始 body/JSON（余额、套餐、在线时长由服务端决定）。";
    public override bool SupportsArguments => true;

    public override async Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Fail(error);
        }
        var settings = store.LoadSettings();
        var url = DebugArgs.FirstNonEmpty(
            DebugArgs.String(args, "url"),
            settings.SelfServiceUrl,
            settings.Debug.Endpoints.SelfService);
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail("缺少自助服务 URL：请传 {\"url\":\"http://...\"}");
        }
        var response = await SelfServiceClient.GetAsync(url, cancellationToken);
        var data = new JsonObject
        {
            ["url"] = response.Url,
            ["status"] = response.Status,
            ["ok"] = response.Ok,
            ["body"] = response.Body,
            ["json"] = response.Json?.DeepClone(),
        };
        return new DebugFeatureResult(
            Name,
            response.Ok,
            response.Ok
                ? $"自助服务 HTTP {response.Status}"
                : $"自助服务失败 HTTP {response.Status}",
            data.ToJsonString(),
            DateTimeOffset.Now);
    }
}

public sealed class ServerListUpdateDebugFeature : DebugFeatureBase
{
    public override string Name => "server-list-update";
    public override string DisplayName => "服务器列表更新";
    public override string Category => "ops";
    public override string Phase => "P5";
    public override string Description =>
        "解析私有属性中的 0x42/0x66/0x68/0x77（服务列表/登录地址/SSO 开关）。";
    public override bool SupportsArguments => true;

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return Task.FromResult(Fail(error));
        }
        var hex = DebugArgs.String(args, "hex");
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Task.FromResult(Fail("需要 {\"hex\":\"<私有属性 hex>\"}"));
        }
        var cleaned = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            return Task.FromResult(Fail("hex 长度必须为偶数"));
        }
        var parsed = PrivateProperties.Parse(Convert.FromHexString(cleaned));
        var relevant = parsed.Properties
            .Where(item => item.Type is
                (byte)PrivatePropertyType.ServerListUpdate or
                (byte)PrivatePropertyType.ServerListUpdateLegacy or
                (byte)PrivatePropertyType.UserLoginUrl or
                (byte)PrivatePropertyType.AutoOpenSsoUrl)
            .Select(item => new JsonObject
            {
                ["type"] = item.Type,
                ["name"] = item.Name,
                ["value"] = item.DisplayValue,
                ["hex"] = item.Hex,
            })
            .ToArray();
        var json = new JsonArray(relevant);
        return Task.FromResult(Ok(
            $"匹配 {relevant.Length} 项",
            new JsonObject { ["entries"] = json }.ToJsonString()));
    }
}
