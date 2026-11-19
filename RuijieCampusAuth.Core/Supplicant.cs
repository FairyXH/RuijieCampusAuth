using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core;

public sealed class Supplicant : IDisposable
{
    private readonly string _interface;
    private readonly byte[] _identity;
    private readonly byte[] _password;
    private readonly double _timeout;
    private readonly int _retries;
    private readonly int _repeats;
    private readonly bool _privateTail;
    private readonly bool _logoffFirst;
    private readonly double _responseDelay;
    private readonly double _startTimeout;
    private readonly string? _lockedServer;
    private readonly byte[] _serviceName;
    private readonly NetworkInfo _network;
    private readonly byte[] _sessionNonce;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action<string>? _log;
    private readonly Action<string>? _error;
    private PcapDevice? _device;

    public string SourceMac { get; }
    public string? ServerMac { get; private set; }
    public bool Connected { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public PrivatePropertiesResult? LastPrivateProperties { get; private set; }
    public int? ServerReauthIntervalSeconds { get; private set; }

    public Supplicant(
        string interfaceName,
        string username,
        string password,
        double timeout = 20,
        int retries = 4,
        int repeats = 3,
        bool privateTail = true,
        bool logoffFirst = true,
        double responseDelay = 0.04,
        double startTimeout = 3.0,
        string? lockedServer = null,
        string? serviceName = null,
        string? staticId = null,
        NetworkInfo? network = null,
        Action<string>? log = null,
        Action<string>? error = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _interface = interfaceName;
        _identity = Encoding.GetEncoding("GB18030").GetBytes(username);
        _password = Encoding.GetEncoding("GB18030").GetBytes(password);
        _timeout = Math.Max(1, timeout);
        _retries = Math.Max(1, retries);
        _repeats = Math.Max(1, repeats);
        _privateTail = privateTail;
        _logoffFirst = logoffFirst;
        _responseDelay = Math.Max(0, responseDelay);
        _startTimeout = Math.Max(0.5, startTimeout);
        _lockedServer = PrivateTail.NormalizeMac(lockedServer) is { Length: > 0 } locked
            ? locked
            : null;
        var service = string.IsNullOrWhiteSpace(serviceName) ? "" : serviceName.Trim();
        _serviceName = Encoding.GetEncoding("GB18030").GetBytes(service);
        _log = log;
        _error = error;

        var nic = PrivateTail.FindInterface(interfaceName)
            ?? throw new ArgumentException($"interface not found: {interfaceName}", nameof(interfaceName));
        SourceMac = PrivateTail.NormalizeMac(nic.GetPhysicalAddress().ToString());
        if (SourceMac.Length == 0)
        {
            throw new InvalidOperationException("interface has no MAC address");
        }
        _network = network ?? PrivateTail.CollectNetworkInfo(interfaceName);
        if (!string.IsNullOrWhiteSpace(staticId))
        {
            var bytes = Encoding.ASCII.GetBytes(staticId.Trim());
            _network = _network with { StaticId = bytes };
        }
        _sessionNonce = PrivateTail.RandomHex(16);
    }

    public void Stop()
    {
        _cancellation.Cancel();
    }

    private byte[] Tail(byte[] iv, byte[]? nonce = null)
    {
        if (!_privateTail)
        {
            return [];
        }
        var options = new TailOptions { ServiceName = _serviceName };
        if (nonce is not null)
        {
            options = options with { Nonce = nonce };
        }
        return PrivateTail.BuildTail(_identity, _password, SourceMac, _network, iv, options);
    }

    private byte[] ControlTail()
    {
        if (!_privateTail)
        {
            return [];
        }
        var options = new TailOptions
        {
            Nonce = _sessionNonce,
            Fingerprint = new byte[128],
            ClientFlag = [0x01],
            ServiceName = _serviceName,
        };
        return PrivateTail.BuildTail(_identity, _password, SourceMac, _network, new byte[16], options);
    }

    private void Send(byte[] frame)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("pcap device is not open");
        }
        _device.SendBurst(Enumerable.Repeat(frame, _repeats));
    }

    public void SendStart()
    {
        var destination = _lockedServer ?? Eapol.RuijieGroup;
        Send(Eapol.BuildControl(SourceMac, destination, 1, ControlTail()));
        _log?.Invoke(_lockedServer is null
            ? "正在寻找认证服务器..."
            : $"正在请求锁定的认证服务器 {_lockedServer} ...");
    }

    public void Logoff()
    {
        var destination = Eapol.StandardGroup;
        Send(Eapol.BuildControl(SourceMac, destination, 2, ControlTail()));
        _log?.Invoke("已发送带私有尾的 EAPOL-Logoff");
    }

    public bool Authenticate()
    {
        Connected = false;
        ConnectedAt = null;
        ServerMac = null;
        LastPrivateProperties = null;
        ServerReauthIntervalSeconds = null;
        var received = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        _device = new PcapDevice(_interface);
        _device.PacketReceived += packet =>
        {
            try
            {
                if (!received.IsAddingCompleted)
                {
                    received.Add(packet);
                }
            }
            catch (InvalidOperationException)
            {
                // The authentication attempt is finishing.
            }
        };
        try
        {
            Thread.Sleep(300);
            if (_cancellation.IsCancellationRequested)
            {
                _log?.Invoke("认证已取消");
                return false;
            }
            if (_logoffFirst)
            {
                _log?.Invoke("正在清理可能存在的旧会话...");
                Logoff();
                if (_cancellation.Token.WaitHandle.WaitOne(100))
                {
                    _log?.Invoke("认证已取消");
                    return false;
                }
            }
            SendStart();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_timeout);
            var startsSent = 1;
            var nextRetry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_startTimeout);
            var handled = new Dictionary<string, byte[]>();
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    _log?.Invoke("认证已取消");
                    return false;
                }
                if (ServerMac is null && DateTimeOffset.UtcNow >= nextRetry)
                {
                    if (startsSent >= _retries)
                    {
                        break;
                    }
                    SendStart();
                    startsSent++;
                    nextRetry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_startTimeout);
                }
                if (!received.TryTake(out var packet, 500))
                {
                    continue;
                }
                if (packet.Length < 18)
                {
                    continue;
                }
                var source = FormatMac(packet.AsSpan(6, 6));
                if (string.Equals(source, SourceMac, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (_lockedServer is not null &&
                    !string.Equals(source, _lockedServer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!Eapol.TryParseEap(packet, out var code, out var identifier, out var eap))
                {
                    continue;
                }
                Connected = true;
                ConnectedAt = DateTimeOffset.UtcNow;
                ServerMac = source;
                if (code == Eapol.EapSuccess)
                {
                    CaptureServerProperties(eap);
                    foreach (var message in Eapol.ExtractServerMessages(eap))
                    {
                        _log?.Invoke($"服务器消息: {message}");
                    }
                    _log?.Invoke("认证成功");
                    return true;
                }
                if (code == Eapol.EapFailure)
                {
                    var messages = Eapol.ExtractServerMessages(eap);
                    if (messages.Count > 0)
                    {
                        foreach (var message in messages)
                        {
                            _error?.Invoke($"服务器消息: {message}");
                        }
                    }
                    else
                    {
                        _error?.Invoke("认证失败（服务器未返回可读原因）");
                    }
                    return false;
                }
                if (code != Eapol.EapRequest || eap.Length < 5)
                {
                    continue;
                }
                var type = eap[4];
                var destination = source;
                var key = $"{identifier}:{type}:{Convert.ToHexString(eap.AsSpan(5))}";
                if (handled.TryGetValue(key, out var existing))
                {
                    continue;
                }
                byte[] response;
                if (type == Eapol.EapIdentity)
                {
                    _log?.Invoke("已连接认证服务器，提交用户身份...");
                    response = [.. Eapol.IdentityResponse(identifier, _identity), .. Tail(new byte[16], _sessionNonce)];
                }
                else if (type == Eapol.EapMd5)
                {
                    if (eap.Length < 6 || eap[5] != 16 || eap.Length < 6 + 16)
                    {
                        _error?.Invoke("服务器 MD5 Challenge 格式不完整");
                        return false;
                    }
                    _log?.Invoke("正在进行 EAP-MD5 认证...");
                    var challenge = eap.AsSpan(6, 16).ToArray();
                    response = [.. Eapol.Md5Response(identifier, _password, challenge, _identity), .. Tail(challenge)];
                }
                else
                {
                    _error?.Invoke($"服务器请求了未实现的 EAP 类型: {type}");
                    return false;
                }
                handled[key] = response;
                if (_responseDelay > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(_responseDelay));
                }
                Send(Eapol.BuildFrame(SourceMac, destination, response, []));
            }
            if (ServerMac is null)
            {
                _error?.Invoke(_lockedServer is null
                    ? "网卡连接失败或未找到认证服务器"
                    : $"锁定的认证服务器 {_lockedServer} 未响应");
            }
            else
            {
                _error?.Invoke("认证超时，服务器未返回最终结果");
            }
            return false;
        }
        finally
        {
            received.CompleteAdding();
            _device.Dispose();
            _device = null;
        }
    }

    private void CaptureServerProperties(byte[] eap)
    {
        if (eap.Length <= 4)
        {
            return;
        }
        try
        {
            var parsed = PrivateProperties.Parse(eap.AsSpan(4));
            LastPrivateProperties = parsed;
            foreach (var property in parsed.Properties)
            {
                switch (property.Type)
                {
                    case (byte)PrivatePropertyType.SuReauthInterval:
                        if (property.Number is > 0 and <= int.MaxValue)
                        {
                            ServerReauthIntervalSeconds = (int)property.Number.Value;
                        }
                        break;
                    case (byte)PrivatePropertyType.SuUpgradeUrl:
                    case (byte)PrivatePropertyType.AccountInfo:
                    case (byte)PrivatePropertyType.PersonalInfo:
                    case (byte)PrivatePropertyType.BroadcastInfo:
                    case (byte)PrivatePropertyType.RejectReason:
                        _log?.Invoke($"服务器属性: {property.Name}={property.DisplayValue}");
                        break;
                    case (byte)PrivatePropertyType.HelloInterval:
                    case (byte)PrivatePropertyType.IndicatePort:
                    case (byte)PrivatePropertyType.IndicateServerIp:
                    case (byte)PrivatePropertyType.MessageClientPort:
                    case (byte)PrivatePropertyType.ClientMessagePeriod:
                        _log?.Invoke($"服务器属性: {property.Name}={property.DisplayValue}");
                        break;
                }
            }
            if (parsed.Errors.Count > 0)
            {
                _log?.Invoke(
                    $"服务器私有属性解析告警: {string.Join("; ", parsed.Errors)}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"服务器私有属性解析失败: {ex.Message}");
        }
    }

    private static string FormatMac(ReadOnlySpan<byte> bytes)
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = bytes[i].ToString("x2");
        }
        return string.Join(":", parts);
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();
        _device = null;
        _cancellation.Dispose();
    }
}
