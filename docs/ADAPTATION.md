# 学校适配指南（逆向 → Profile → 增量实现）

> 目标：同一个通用壳支持多所学校。每所学校一个 `profiles/<school>.json`；
> 算法不一致时**新增实现**，不修改已存在的实现。

## 1. 三条硬规则

1. **加法适配**：新学校/新版本只新增 provider、字段、配置；不删除、不改写旧实现。
2. **同功能多算法用切换**：同一个认证入口按 `auth_mode` + `auth_version` 选择实现，
   不在一个函数里塞 if/else 版本分支。
3. **证据驱动**：先有反编译/抓包/日志证据，再写实现；未确认的行为标 `unknown`，
   不用猜测补齐。

## 2. 现有通用壳

| 层 | 位置 |
|---|---|
| 学校档案 | `profiles/*.json`、`Core/SchoolProfile.cs` |
| 档案导入 | `ProfileStore.Import`、CLI `profile import`、GUI「配置」页 |
| EAP-MD5 | `Core/Supplicant.cs`、`Core/Eapol.cs`、`Core/PrivateTail.cs` |
| PEAP / EAP-TLS | `Core/Protocols/PeapSession.cs`、`PcapEapTransport.cs` |
| 私有属性 | `Core/Protocols/PrivateProperties.cs` |
| DHCP | `Core/Protocols/DhcpControl.cs` |
| Portal / 中心 | `Core/Protocols/PortalClient.cs` 等 |
| SAM / 升级 | `Core/Protocols/SamUpgradeClients.cs` |
| 调试入口 | `Core/Debug/*`、GUI「调试」页、CLI `debug` |

## 3. 逆向一所新学校

### 3.1 采集安装目录

```text
安装目录/
  SuConfig.txt / SuConfig.dat     学校参数（服务名、超时、DHCP、自助 URL）
  rjixsu.xml                      安装模板（学校名称、产品名）
  newskin.zip                     UI 皮肤（品牌，仅参考）
  8021x.exe / RuijieSupplicant.exe
  *.dll, *.sys
  logs/*.log                      运行日志（SAM/中心/错误信息）
```

记录：文件版本资源、PDB 路径、签名主体、编译时间戳。

### 3.2 静态分析

1. 文件类型/架构/熵/导入表：REA `inspect-artifact`、`inspect-pe-imports`。
2. 字符串与 RTTI：确认 `EAPMD5` / `EAPTLS` / `PEAP` / `Portal` / `SAM` / `DHCP`。
3. 关键函数：用 rizin/Ghidra 定位 `ReadRGVendorSeg`、`CEapPEAPThread`、
   `PortalAuth`、`CDirectTranSrv`、`CDhcpThread`。
4. 记录调用链、字段顺序、常量、密钥、超时。

### 3.3 抓包

- EAPOL：Start / Identity / EAP-MD5 / PEAP / Success。
- Portal：HTTP JSON 请求/响应。
- SAM：UDP 138/53 的 DES 报文。
- DHCP：确认是 Windows API 还是自建包。

每个协议至少保存一份 golden 抓包/hex，写进 `docs/protocols/`。

### 3.4 生成学校档案

| 档案字段 | 来源 |
|---|---|
| `name` | `rjixsu.xml` ProductCustomName / 客户名称 |
| `school_code` | `SuConfig.dat` 的 SchoolCode / 客户端中心查询串 |
| `auth_mode` | `SuConfig` 的 AuthMode / 二进制能力 |
| `service_name` | `SuConfig` / EAP 私有属性 0x39 |
| `static_id` | 抓包/日志中的 0x54 |
| `self_service_url` | `SuConfig.txt` |
| `update_url` | 私有属性 `SU_UPGRADE_URL` / 配置 |
| `portal_url` / `sam_host` | 现场配置或抓包 |
| `dhcp_mode/timeout/option82` | `SuConfig.dat` |
| `ad_domain/ad_timeout` | `SuConfig.dat` / AD 统一认证 |
| `engine_version` | 文件版本资源 |

生成后先 `profile show` 检查，再 `profile import` 导入；导入会备份 `settings.json`。

### 3.5 算法不一致时

新增一个 provider，不改旧 provider：

```csharp
public interface IAuthProtocol
{
    string Id { get; }        // eap-md5 / peap-v0 / peap-v1 / eap-tls / portal / sam
    string Version { get; }   // 1 / 2 / ...
    Task<AuthResult> AuthenticateAsync(AuthContext context, CancellationToken ct);
}
```

选择逻辑：

```text
profile.auth_mode + profile.auth_version
    -> AuthProtocolRegistry.Resolve(...)
    -> 对应 provider
```

规则：

- 帧字段顺序不同 → 新 provider 版本，不修改旧字段布局。
- 加密方式不同（XOR/RC4/DES/AES）→ 新 `ICryptoProfile` 实现，按 profile 选择。
- 私有尾结构不同 → 新 `IPrivateTailCodec`，按 `protocol_version` 选择。
- Portal JSON 字段不同 → 新 `PortalRequestBuilder` 版本。
- SAM 版本不同 → 新 `SamCodec` 版本。

已存在的 MD5/PEAP/SAM 实现保持原样；新实现通过注册表并存。

## 4. 多档案导入与切换

- 目录：程序目录 `profiles/` 或当前目录 `profiles/`；可放任意多个 JSON。
- CLI：

```bash
dotnet RuijieCampusAuth.Cli.dll profile list
dotnet RuijieCampusAuth.Cli.dll profile show profiles/school-a.json
dotnet RuijieCampusAuth.Cli.dll profile import profiles/school-a.json --root <安装目录>
```

- GUI：`档案` 页选择档案 → `导入/切换`；显示档案名、认证模式、服务名、路径。
- 服务端读取 `settings.json` 的每个循环，切换后下一轮认证生效；无需重启服务。
- 导入只覆盖档案声明字段；账号、密码、网卡、接管、Guardian 不变。
- 导入前备份 `settings.json.bak-<时间戳>`。

## 5. 新增协议实现模板

1. `Core/Protocols/<Name>.cs`：解析/编码纯函数 + provider。
2. `docs/protocols/<name>.md`：证据、字段、调用链、golden。
3. `Core.Tests/<Name>Tests.cs`：至少一个标准向量 + 一个边界/错误。
4. `Core/Debug/<Name>DebugFeature.cs`：手动触发，默认关闭。
5. 注册到 `DebugFeatureRegistry`，profile 增加 `auth_mode/version`。
6. 回归：现有 MD5/PEAP/Portal/SAM 测试必须全绿。

## 6. 安全与兼容

- 未识别字段保留原文，不丢弃；解析失败记录告警但不影响既有认证。
- 凭据/密钥只走 DPAPI；调试日志脱敏（`DebugAuditLog`）。
- 协议实验必须限次数，避免账号锁定。
- 新版本引擎能力用 `engine_version` 记录；未知版本先探测再启用。

## 7. 当前状态

| 能力 | 状态 |
|---|---|
| EAP-MD5 | 已实现并线上验证 |
| 私有属性 TLV/RC4 | 已实现 |
| DHCP | 已实现（Windows API） |
| Portal/中心/自助/消息 | 已实现，待现场 |
| PEAP/EAP-TLS | 已实现，待 RADIUS 实验 |
| 无线 WPA2-Enterprise | 已实现，待 AP 实验 |
| SAM | 探针/ DES 工具，报文待抓包 |
| 升级 | 检查已实现，安装未实现 |
| 多档案 GUI 切换 | 已实现 |
