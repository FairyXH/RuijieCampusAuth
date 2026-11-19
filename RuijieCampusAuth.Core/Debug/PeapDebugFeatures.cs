using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Debug;

internal static class PeapFeatureRunner
{
    public static async Task<DebugFeatureResult> RunAsync(
        UserStore store,
        EapTunnelMode mode,
        string name,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var args = DebugArgs.Parse(argumentsJson, out var error);
        if (error is not null)
        {
            return new DebugFeatureResult(name, false, error, null, DateTimeOffset.Now);
        }
        var settings = store.LoadSettings();
        var account = store.LoadAccounts().FirstOrDefault(item =>
                          item.Id == settings.ActiveAccountId)
                      ?? store.LoadAccounts().FirstOrDefault(item => item.Enabled);
        var interfaceHint = DebugArgs.String(args, "interface") ?? settings.Interface;
        var username = DebugArgs.String(args, "username") ?? account?.Username ?? "";
        var password = DebugArgs.String(args, "password") ?? account?.Password ?? "";
        if (string.IsNullOrWhiteSpace(interfaceHint))
        {
            return new DebugFeatureResult(
                name,
                false,
                "缺少网卡：请传 {\"interface\":\"...\"} 或先保存网卡设置",
                null,
                DateTimeOffset.Now);
        }
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new DebugFeatureResult(
                name,
                false,
                "缺少用户名/密码：请传参数或配置账号",
                null,
                DateTimeOffset.Now);
        }

        var options = new PeapOptions
        {
            Mode = mode,
            UserName = username,
            Password = password,
            TargetHost = DebugArgs.String(args, "target_host") ?? "",
            VerifyServerCertificate = DebugArgs.Bool(args, "verify_cert") ?? false,
            Protocols = ParseProtocols(DebugArgs.String(args, "protocols")),
            MaxFragmentBytes = Math.Clamp(
                (int)(DebugArgs.Number(args, "max_fragment") ?? 1200),
                200,
                1400),
            Timeout = TimeSpan.FromSeconds(
                Math.Clamp(DebugArgs.Number(args, "timeout") ?? settings.Timeout, 5, 120)),
        };
        var tailHex = DebugArgs.String(args, "tail_hex");
        if (!string.IsNullOrWhiteSpace(tailHex))
        {
            var cleaned = new string(tailHex.Where(Uri.IsHexDigit).ToArray());
            if (cleaned.Length % 2 != 0)
            {
                return new DebugFeatureResult(
                    name,
                    false,
                    "tail_hex 长度必须为偶数",
                    null,
                    DateTimeOffset.Now);
            }
            options.IdentityTail = Convert.FromHexString(cleaned);
        }
        if (mode == EapTunnelMode.EapTls)
        {
            options.ClientCertificate = LoadCertificate(args);
        }

        var log = new List<string>();
        try
        {
            using var transport = new PcapEapTransport(
                interfaceHint,
                mode,
                logoffFirst: DebugArgs.Bool(args, "logoff_first") ?? true,
                repeats: settings.Repeats,
                timeout: options.Timeout,
                log: message => log.Add(message));
            var result = await Task.Run(
                () => PeapSession.Run(transport, options),
                cancellationToken);
            var data = new JsonObject
            {
                ["ok"] = result.Ok,
                ["message"] = result.Message,
                ["tls"] = result.TlsProtocol,
                ["server_subject"] = result.ServerSubject,
                ["outer_failure"] = result.OuterFailure,
                ["steps"] = new JsonArray(result.Steps
                    .Select(step => (JsonNode)JsonValue.Create(step)!)
                    .ToArray()),
                ["log"] = new JsonArray(log
                    .Select(message => (JsonNode)JsonValue.Create(message)!)
                    .ToArray()),
            };
            return new DebugFeatureResult(
                name,
                result.Ok,
                result.Message,
                data.ToJsonString(),
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return new DebugFeatureResult(
                name,
                false,
                ex.Message,
                new JsonObject { ["log"] = new JsonArray(log
                    .Select(message => (JsonNode)JsonValue.Create(message)!)
                    .ToArray()) }.ToJsonString(),
                DateTimeOffset.Now);
        }
    }

    private static SslProtocols ParseProtocols(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tls13" => SslProtocols.Tls13,
            "auto" => SslProtocols.Tls12 | SslProtocols.Tls13,
            _ => SslProtocols.Tls12,
        };
    }

    private static X509Certificate2? LoadCertificate(JsonNode? args)
    {
        var path = DebugArgs.String(args, "cert_path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("client certificate not found", path);
            }
            var password = DebugArgs.String(args, "cert_password");
            return new X509Certificate2(
                path,
                password,
                X509KeyStorageFlags.Exportable);
        }
        var thumbprint = DebugArgs.String(args, "cert_thumbprint");
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var normalized = thumbprint.Replace(" ", "").ToUpperInvariant();
        return store.Certificates
            .Cast<X509Certificate2>()
            .FirstOrDefault(item =>
                item.Thumbprint?.Equals(normalized, StringComparison.OrdinalIgnoreCase) == true);
    }
}

public sealed class PeapDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "peap";
    public override string DisplayName => "PEAP / MSCHAPv2";
    public override string Category => "auth";
    public override string Phase => "P6";
    public override string Description =>
        "PEAP v0/v1 + MSCHAPv2 实验台：TLS 隧道、phase2、可选客户端证书。";
    public override bool SupportsArguments => true;

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        return PeapFeatureRunner.RunAsync(
            store,
            EapTunnelMode.Peap,
            Name,
            argumentsJson,
            cancellationToken);
    }
}

public sealed class EapTlsDebugFeature(UserStore store) : DebugFeatureBase
{
    public override string Name => "eap-tls";
    public override string DisplayName => "EAP-TLS";
    public override string Category => "auth";
    public override string Phase => "P6";
    public override string Description =>
        "EAP-TLS 实验台：客户端证书文件/证书库 thumbprint，TLS 隧道认证。";
    public override bool SupportsArguments => true;

    public override Task<DebugFeatureResult> RunAsync(
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        return PeapFeatureRunner.RunAsync(
            store,
            EapTunnelMode.EapTls,
            Name,
            argumentsJson,
            cancellationToken);
    }
}
