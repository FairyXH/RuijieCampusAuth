# PEAP / EAP-TLS

状态：P6 已实现（MSCHAPv2、MD4、DES、TLS 双向流、EAPOL 传输、PEAP phase2）。
待现场验证：真实 RADIUS/PEAP 服务器、证书策略、PEAP 私有段字段。

## 0. 当前实现范围

- `Core/Protocols/MschapV2.cs`：MD4、challenge、NT-Response、response 布局。
- `Core/Protocols/EapTlsStream.cs`：SslStream ↔ EAP 分片双向桥。
- `Core/Protocols/PeapSession.cs`：PEAP/EAP-TLS 会话、分片、phase2。
- `Core/Protocols/PcapEapTransport.cs`：EAPOL Start/Identity/Exchange。
- `Core/Debug/PeapDebugFeatures.cs`：`peap` / `eap-tls` 调试功能。
- 测试：MD4/NT-Response RFC 向量、TLS 双向握手；PEAP 端到端需实验 RADIUS。

## 1. 原版证据

`8021x.exe` 字符串：

- `CEapPEAPThread::do_peap_version0` / `do_peap_version1`
- `CEapPEAPThread::peap_do_phase2`
- `CEapPEAPThread::tls_decode_packet`
- `CEapPEAPThread::eapmschap_decode_packet`
- `mschapv2 challenge` / `mschapv2 success`
- `EAPMD5` / `EAPTLS`
- `CertFindCertificateInStore`（客户端证书选择）
- `CRGPrivateProc::EncapRGVerdorSegForPeap`：ClientVersion、DHCPAuthPhase、
  HardDiskSN、IPv4/IPv6、MAC、SecCheckResult、SecDomainName、ServiceName、
  UserPasswd、V3SegmentHash
- TLS 库：`LIBEAY32.dll` / `SSLEAY32.dll`，OpenSSL 0.9.8j

## 2. 标准协议要点（与原版一致的公开部分）

### PEAP

- 外层 EAP type 25。
- PEAP flags：`0x80` Length、`0x40` More、`0x20` Start。
- 分片：首片带 4 字节 TLS Message Length；中间片 M；末片无 M。
- TLS 握手在 PEAP 隧道内；phase2 的 EAP 包作为 TLS application data。
- Phase2：EAP-Identity → EAP-MSCHAPv2 challenge/response → Success。

### EAP-TLS

- 外层 EAP type 13。
- 分片 flags 与 PEAP 相同，无 phase2 EAP 消息。
- 客户端证书来自 Windows 证书库或文件。

### MSCHAPv2

- NT-Response：
  - `PasswordHash = MD4(UTF-16LE(password))`
  - `ZPasswordHash = PasswordHash padded to 24 bytes`
  - 每 7 字节扩展为 8 字节 DES key（pppd `ExpandDesKey` 风格）
  - `NTResponse = DES(ChallengeHash) × 3`
- `ChallengeHash = SHA1(PeerChallenge || AuthChallenge || UserName)[0..8]`
- 已用 RFC 2759 向量验证：
  - `User` / `clientPass`
  - `NTResponse = 82309ECD8D708B5EA08FAA3981CD83544233114A3D85D6DF`
- 注意：NT-Response 使用单次 MD4 的 PasswordHash，不是 PasswordHashHash；
  PasswordHashHash 用于 AuthenticatorResponse/MPPE key，后续再实现。

## 3. C# 实现

| 模块 | 文件 | 状态 |
|---|---|---|
| MD4 | `Core/Protocols/MschapV2.cs` | 完成，RFC 向量 |
| MSCHAPv2 challenge/response | `Core/Protocols/MschapV2.cs` | 完成 |
| DES key 扩展 | `Core/Protocols/MschapV2.cs` | 完成 |
| SslStream ↔ EAP 双向桥 | `Core/Protocols/EapTlsStream.cs` | 完成，本机 TLS 握手测试 |
| PEAP 分片/phase2 | `Core/Protocols/PeapSession.cs` | 完成，待实验服务器 |
| EAPOL 传输 | `Core/Protocols/PcapEapTransport.cs` | 完成，待实验服务器 |
| 调试页 `peap` / `eap-tls` | `Core/Debug/PeapDebugFeatures.cs` | 完成 |
| PEAP 凭据段 0x5F | `Core/Protocols/PeapVendorSegments.cs` | 完成，原版固定 IV 验证 |

### PEAP vendor segments（从原版反编译）

段格式 `[type][len][payload]`：

| type | 内容 | 原版证据 |
|---|---|---|
| `0x11` | client version，36 字节 | `FUN_00518950` |
| `0x2D` | MAC，6 字节 | `FUN_005195b0` |
| `0x39` | service name，0x20/0x40 | `FUN_00519620` |
| `0x5F` | PEAP 凭据密文 | `FUN_00519c70` |
| `0x63` | DHCP phase | `FUN_00518c70` |

`0x5F` 使用硬编码 IV `!jierui9002pmsus`，加密算法与 0x2F 相同
（`FUN_0053c970`，MD5 链式 XOR）。已用原版函数输出锁定测试向量。

调试参数：

| 参数 | 说明 |
|---|---|
| `interface` | NPF 设备或网卡名 |
| `username` / `password` | 缺省取当前账号 |
| `target_host` | 证书主机名，默认空 |
| `verify_cert` | 默认 false（原版客户端不强制校验） |
| `protocols` | `tls12`（默认）/`tls13`/`auto` |
| `max_fragment` | 默认 1200 字节 |
| `timeout` | 默认取 settings.timeout |
| `tail_hex` | 可选私有尾 hex（附加在 EAP 响应后） |
| `cert_path` / `cert_password` / `cert_thumbprint` | EAP-TLS 客户端证书 |

测试：

- `Core.Tests/MschapV2Tests.cs`：MD4、RFC NT-Response、challenge 解析、response 布局。
- `Core.Tests/EapTlsStreamTests.cs`：自签证书 + SslStream 双向握手与数据。

## 4. 待确认

1. PEAP phase2 的 TLS 版本/密码套件与服务器兼容范围（原版 OpenSSL 0.9.8j）。
2. 服务端证书校验策略：是否校验 CA、是否允许自签、主机名规则。
3. PEAP 私有 vendor segment 的字段顺序、类型 ID 和取值（需要抓包/反编译）。
4. 是否在 PEAP 响应后追加私有尾；标准 PEAP 可先不带。
5. EAP-TLS 客户端证书选择规则（证书库/文件/thumbprint）。

## 5. 算法切换设计（为多学校适配）

认证算法按“协议 + 版本”注册，不做覆盖式改写：

```csharp
interface IAuthProtocol {
    string Id { get; }          // eap-md5 / peap-v0 / peap-v1 / eap-tls / portal / sam
    string Version { get; }
    Task<AuthResult> RunAsync(AuthContext context, CancellationToken ct);
}
```

- `SchoolProfile.auth_mode` 选择协议族；`auth_version` 选择同一函数的不同输入/输出实现。
- 新学校适配 = 新增 `IAuthProtocol` 实现 + profile，不修改既有实现。
- 帧级差异（字段顺序/加密方式）放在协议实现内部，通过 profile 参数切换。
