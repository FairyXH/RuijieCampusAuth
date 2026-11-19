# PEAP / EAP-TLS

状态：P6 核心已实现（MSCHAPv2、MD4、DES、TLS 双向流桥）。
待完成：EAPOL 传输、PEAP phase2 状态机、PEAP 私有段字段、证书策略现场验证。

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
| PEAP 分片/phase2 | 待定 | 未实现 |
| EAPOL 传输 | 待定 | 未实现 |
| 调试页 `peap` / `eap-tls` | 待定 | 计划中 |

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
