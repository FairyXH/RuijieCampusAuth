# 锐捷官方认证客户端完整逆向报告

> 对象：`C:\Program Files\Ruijie Networks\Ruijie Supplicant`
> 方法：REA / Ghidra 12.1.4 / rizin 0.9.1 静态分析 + Unicorn 32 位函数调用 + 本地假服务抓包
> 日期：2026-11-19
> 配套文档：`docs/protocols/*.md`、`docs/DIFFERENTIAL_TESTING.md`、`docs/ADAPTATION.md`

---

## 1. 文件与版本

| 文件 | 版本/编译 | 作用 |
|---|---|---|
| `8021x.exe` | 5.0.2014.108，2019-06-28，MSVC 8.0 | 认证引擎（EAPOL/PEAP/DHCP/Portal/无线） |
| `RuijieSupplicant.exe` | 1.1.0.1，2015-07-20，MSVC 8.0 | UI 主程序 |
| `suservice.exe` | 1.0.0.3，2016-03-28 | 服务包装 |
| `PortalAuth.dll` | 1.0.0.1，2018/2019 | eportal HTTP 客户端 |
| `RJIX.dll` | 2018 | 安装/环境接口（`CreateContextObject`/`FreeContextObject`） |
| `WirelessCore.dll` | 2018 | 无线封装（`ExportWireless`） |
| `Nic.dll` | 2018 | 网卡事件（`ExportNicEvent`） |
| `W32N55.dll` | 2018 | WinPcap 兼容 NDIS 协议驱动接口 |
| `wpcap.dll` / `Packet.dll` / `npf.sys` | WinPcap 4.x | 抓包/发包 |
| `libeay32.dll` / `ssleay32.dll` | OpenSSL 0.9.8j（2009） | TLS/RC4/DES |
| `DuiLib.dll` | DirectUI | 皮肤 UI |
| `7z.exe` / `7z.dll` | 2018 | 资源解压 |
| `NetworkDetect.exe` | 2018 | 网络状态检测 |
| `SoftwareManager.exe` | 2018 | 软件管理/下载 |
| `SuInstaller.exe` / `SuInstallerInterface.dll` | 2019 | 安装器 |

签名：

- `RuijieSupplicant.exe` / `suservice.exe` / DLL：`FUJIAN RUIJIE NETWORKS CO.,LTD.`
- `8021x.exe`：`RUIJIE NETWORKS CO., LTD, OU=Network Products Research`
- 均带时间戳，签名有效。

PDB 路径泄漏：

- `e:\Workspace\Su_6_40\Su_6_40_UCD\bin\Release\x86\RuijieSupplicant.pdb_new`
- `e:\Su\Branch\Su_6_70\SU_6_85_UCD\Su\release\8021x.pdb`
- `e:\Workspace\Su_6_60\SU_6_60_UCD\PortalAuth\release\PortalAuth.pdb`

说明是锐捷同一代码库的多分支发布（Su_6_40/6_50/6_60/6_70/6_85）。

## 2. 保护与编译评估

| 项 | 结果 |
|---|---|
| 加壳 | 无。段表只有 `.text/.rdata/.data/.rsrc`，`.text` 熵 6.33–6.64 |
| .NET | 无。全部 32 位原生 C++，静态 MFC |
| 反调试 | 仅导入 `IsDebuggerPresent`，无 `NtQueryInformationProcess`、无 anti-VM 框架 |
| 控制流混淆 | 无。RTTI 443 个类名、446 条 `类::方法` 调试日志完整 |
| 字符串加密 | 无。中文提示、URL、密钥明文 |
| ASLR/DEP/CFG | `DllCharacteristics=0x0`，无 ASLR、无 NX_COMPAT、无 CFG |
| 其他缓解 | GS Cookie、SafeSEH |
| 签名 | 锐捷官方 Authenticode |

结论：二进制层逆向难度低；真正的复杂度在协议字段与私有算法，而这些已被还原。

## 3. 学校定制机制（OEM 套壳）

学校名**不在任何二进制中**。差异化全部是数据：

- `rjixsu.xml`：`ProductCustomName`、`ProductUninstallName`、安装文件清单。
- `SuConfig.txt`：客户名称、用户名、超时、DHCP、自助 URL、网卡名。
- `SuConfig.dat`：XOR/替换混淆的键值（SchoolCode、Service、AuthMode、AutoRun、WiFi、UI 等）。
- `newskin.zip`：logo、背景、DuiLib 布局。
- 运行期服务端可下发学校字段（服务名、静态 ID、升级 URL、学校代码等）。

因此同一引擎可换学校，只需替换配置/皮肤；这也支撑“通用壳 + profiles”方案。

本机观测值（作为档案字段来源）：

| 项 | 值 |
|---|---|
| 学校代码 | `<school-code>` |
| 服务名 | `<service-name>` |
| 静态 ID | `<static-id>` |
| 自助服务 | `http://<school-domain>/selfservice/` |
| 升级 | `http://update.rghall.com.cn/das/update` |
| 引擎 | 5.0.2014.108 |
| 认证模式 | EAPMD5 |

## 4. 进程/模块架构

```
RuijieSupplicant.exe (UI, DuiLib/MFC)
   ├─ suservice.exe (服务)
   ├─ 8021x.exe (认证引擎)
   │    ├─ wpcap/Packet/npf.sys (EAPOL)
   │    ├─ libeay32/ssleay32 (TLS/RC4/DES)
   │    ├─ W32N55.dll (NDIS 查询)
   │    ├─ Duilib.dll (引擎内 UI/对话框)
   │    ├─ PortalAuth.dll (eportal HTTP)
   │    ├─ WirelessCore.dll / Nic.dll (无线/网卡)
   │    └─ RJIX.dll (环境/注册表)
   ├─ NetworkDetect.exe (网络检测)
   ├─ SoftwareManager.exe (软件下载/安装)
   └─ SuInstaller.exe (安装/升级)
```

线程/状态：

- `CStateMachineThread` + `CStateDisconnected/Connecting/Authenticating/Authenticated/Acquired/Hold/LogOff`。
- `CContextControlThread` 负责 DHCP、升级、网卡枚举、无线、SAM、Portal 消息。
- `CRxPacketThread` / `CSendPacketThread` / `CPacketParse` 负责 EAPOL 收发解析。
- `CEapPEAPThread` 负责 PEAP/TLS phase2。
- `CDhcpThread`、`CArpDetectThread`、`CNetAdapterMonThread`、`CProxyProcThread`、`CCheckDialUpThread` 等。

## 5. EAPOL / EAP-MD5

### 5.1 帧结构

- 组播地址：锐捷 `01:d0:f8:00:00:03`，标准 PAE `01:80:c2:00:00:03`。
- EtherType `0x888E`。
- Start/Logoff：587 字节（18 字节 EAPOL + 569 私有尾）。
- Identity 响应：601 字节（EAPOL 长度 14，尾部在长度之外）。
- MD5 响应：618 字节（EAPOL 长度 31，尾部在长度之外）。
- EAP-Success：299 字节（EAP 长度 281，含 277 字节服务器私有属性）。

### 5.2 私有尾（569 字节）

```
[23 字节 header]
  marker 00 00 13 11
  flag (1)
  IPv4 (4) / netmask (4) / gateway (4) / DHCP (4)
  CRC16/XMODEM (2, big-endian)
  整段 23 字节逐字节位反转后取反

[47 字节 preamble]
  marker 00 00 13 11
  "8021x.exe" 填充到 32
  client version 06 56 00 00
  client flag 01
  marker 00 00 13 11
  attributes total length (2)

[attributes]
  1A | total_len | 00 00 13 11 | type | len | payload
  total_len = 8 + payload_len
  len       = 2 + payload_len
```

属性（C# 实现顺序）：

| type | payload |
|---|---|
| `0x17` | 32 字节 nonce |
| `0x18` | 4 字节零 |
| `0x2D` | 6 字节 MAC |
| `0x2F` | 凭据密文（16/32/48 字节） |
| `0x35` | `01` |
| `0x36` | 16 字节零 |
| `0x38` | IPv6 16 字节 |
| `0x4E` | 16 字节零 |
| `0x4D` | 指纹 128 字节 |
| `0x39` | 服务名填充到 32 |
| `0x54` | 静态 ID 填充到 64 |
| `0x62` | `00` |
| `0x6B` | `00` |
| `0x7E` | `00 00 01 00 00 0C 00 00` |
| `0x70` | `40` |
| `0x6F` | `00` |
| `0x79` | `02` |
| `0x76` | IP 字符串 ASCII |

反编译 `FUN_0053a590` 确认前 11 项顺序为：
`0x17,0x18,0x2D,0x2F,0x35,0x36,0x38,0x4E,0x4D,0x39,0x54`。
后续 `0x62/0x6B/0x7E/0x70/0x6F/0x79/0x76` 在原版函数中带条件分支，需完整对象状态做动态全量比对。

### 5.3 凭据加密

```
PasswordHash = MD4(UTF-16LE(password))
plain = password bytes padded to 16-byte boundary
prev = IV (16 bytes; zero for Start/Logoff/Identity, MD5 challenge for MD5 response)
for each block:
    stream = MD5(username || prev)
    cipher = plain XOR stream
    prev = cipher
```

已用原版函数 `0x53c970` 做差分：16/32/48 字节、多块链式全部一致。

## 6. 服务器私有属性

### 6.1 位置与结构

- EAP-Success 数据区（EAP 头之后）。
- 总长 ≤ `0x578`（1400）。
- TLV：`type(1) | len(1) | value(len)`。
- `type==0` 非法；`len>0xFE` 非法；`offset+len>total` 非法。
- 未知 type 跳过。

### 6.2 加密

整块为明文。仅：

| type | 名称 | 处理 |
|---|---|---|
| `0x5C` | `ENCRYPT_KEY` | 8 字节 RC4，key `com.ruijie.www` |
| `0x5D` | `ENCRYPT_IV` | 8 字节 RC4，key `com.ruijie.www` |

### 6.3 类型表

| type | 名称 | 解码 |
|---|---|---|
| `0x05` | SU_NEWEST_VER | 4B 大端 |
| `0x14` | PROXY_AVOID | 数值 |
| `0x15` | DIALUP_AVOID | 数值 |
| `0x37` | SU_UPGRADE_URL | GB18030 字符串 |
| `0x3C` | ACCOUNT_INFO | 字符串 |
| `0x3D` | PERSIONAL_INFO | 字符串 |
| `0x42` | SERVER_LIST_UPDATE | 字符串 |
| `0x52` | BROADCASE_INFO | 字符串 |
| `0x53` | REJECT_REASON | 字符串 |
| `0x56` | SU_REAUTH_INTERV | 4B 大端，秒 |
| `0x59` | HELLO_INTERV | 4B 大端 |
| `0x5A` | INDICATE_PORT | 4B 大端 |
| `0x5B` | INDICATE_SERV_IP | 4B 大端 |
| `0x5C` | ENCRYPT_KEY | 8B RC4 |
| `0x5D` | ENCRYPT_IV | 8B RC4 |
| `0x5E` | SERVER_UTC_TIME | 4B 大端 |
| `0x61` | RADIUS_SERV_TYPE | 数值 |
| `0x66` | SERVER_LIST_UPDATE_LEGACY | 字符串/列表 |
| `0x68` | USER_LOGIN_URL | 字符串 |
| `0x6E` | MSG_CLIENT_PORT | 4B 大端 |
| `0x72` | UTRUST_URL | 字符串 |
| `0x73` | SMP_IPADDRESS | 4B 大端 |
| `0x74` | PROXY_DECTECT_KINDS | 数值/位图 |
| `0x75` | CLIENT_MSG_PERIOD | 4B 大端 |
| `0x77` | AUTO_OPEN_SSO_URL | 1B 开关 |
| `0x79` | DIRECT_COMM_HIGHEST_VERSION | 1B |
| `0x80` | DIRECT_COMM_HEARTBEAT_FLAGS | 4B |
| `0x98` | WIFI_CNNT_NUM_FLAGS | 4B |

## 7. PEAP / EAP-TLS

### 7.1 外层

- PEAP：EAP type 25，flags `0x80` L、`0x40` M、`0x20` S，首片带 4 字节 TLS 总长。
- EAP-TLS：EAP type 13，分片 flags 相同。
- TLS 在隧道内；PEAP phase2 的 EAP 包作为 TLS application data。

### 7.2 Phase2

```
EAP-Request/Identity → EAP-Response/Identity
EAP-Request/MSCHAPv2 (opcode 1 challenge)
EAP-Response/MSCHAPv2 (opcode 2 response)
MSCHAPv2 Success → inner EAP-Success → outer EAP-Success
```

### 7.3 MSCHAPv2

- `ChallengeHash = SHA1(PeerChallenge || AuthChallenge || UserName)[0..8]`
- `PasswordHash = MD4(UTF-16LE(password))`
- `ZPasswordHash = PasswordHash + 5 zero bytes`
- 7 字节 slice 经 `ExpandDesKey` 扩为 8 字节 DES key（奇数奇偶校验）
- `NTResponse = DES(ChallengeHash) × 3`
- RFC 2759 向量：`User/clientPass` →
  `82309ECD8D708B5EA08FAA3981CD83544233114A3D85D6DF`

注意：NT-Response 使用**单次 MD4** 的 PasswordHash，不是 PasswordHashHash；
PasswordHashHash 用于 AuthenticatorResponse/MPPE key。

### 7.4 PEAP vendor segments

段格式 `[type][len][payload]`：

| type | 内容 | 原版函数 |
|---|---|---|
| `0x11` | client version，0x24 字节 | `0x518950` |
| `0x2D` | MAC，6 字节 | `0x5195B0` |
| `0x2F` | EAP-MD5 凭据密文 | `0x519C70` 系列 |
| `0x39` | service name，0x20/0x40 | `0x519620` |
| `0x5F` | PEAP 凭据密文 | `0x519C70` |
| `0x63` | DHCP phase，`63 01 phase` | `0x518C70` |

`0x5F` 使用固定 IV `!jierui9002pmsus`，加密与 0x2F 相同。

## 8. DHCP / 链路

原版不构造 DHCP 报文，调用 Windows DHCP API：

- `GetInterfaceInfo`
- `IpReleaseAddress`
- `IpRenewAddress`
- `GetAdaptersInfo` / `GetPerAdapterInfo`
- `NotifyAddrChange`

行为：

- `InitDhcp` → `IpRenewAddress`
- `ReleaseDHCP` → `IpReleaseAddress`
- `ReleaseAndRenew` → 先 release 再 renew

Option 82：

- 配置键 `IsSupportOption82` 只出现在配置读写路径；
- 本引擎版本不主动注入 Option 82；Windows DHCP 客户端默认也不发送。

## 9. Portal / 客户端中心 / 自助 / 消息

### 9.1 Portal（`PortalAuth.dll`）

- POST JSON，WinINet；`Accept/Content-Type: application/json`。
- 端点：
  - `eportal/inferface/authAPI/login`
  - `eportal/inferface/authAPI/logout`
  - `eportal/inferface/authAPI/getOnlineUserInfo`
  - `eportal/inferface/authAPI/pageInfo`
- 登录字段：`userId`、`password`、`service`、`queryString`、`userAgent`、`userip`。
- service 值追加 `PC` 后缀。
- logout/online/pageInfo：`userip`、`userIndex`（可扩展）。
- 响应字段：`success`、`message`、`keepaliveInterval`、`announcement`、`result`。

### 9.2 客户端中心

`CClientCenterPeerManager` 查询串：

```
mac=...&ipv4=...&ipv61..4=0&product=...&mainver=...&subver=...
&schoolcode=...&osbits=32&releasedate=2019.07.24
```

endpoint 未在二进制中固定，需现场配置/抓包。

### 9.3 自助服务

- 配置键 `SelfSvrURL`。
- 返回 “ball JSON”，包含余额、套餐、在线时长等；字段由学校自助系统决定。

### 9.4 服务列表 / 用户消息

- 服务列表：私有属性 `0x42/0x66/0x68/0x77`。
- 用户消息：`0x3C/0x3D/0x52/0x53`，GB18030 文本。

## 10. SAM 直通

`dircomm.log` 观测参数：

- 协议版本 2
- 重传 2 次
- 超时 2000 ms
- DES key `7C73533126403871`
- IV `4163332331215164`
- UTC time 8 字节
- SU PORT 138
- SAM IP 示例 `<server-ip>`，端口 53
- `bUseHandshake2Sam`、`dwTimerToSAM`

相关：`CDirectTranSrv`、`CDirTranThread`、`CSuDES`、`OnInit_SAM`、`ONSAMWantLogOff`。

**未确认**：完整报文头/字段布局、DES 作用范围与填充、序号/重传语义。当前 C# 只提供 UDP 探针 + DES 工具，不伪造握手。

## 11. 升级

- `CContextControlThread::DoUpgrade`
- `update.rghall.com.cn/das/update`
- `CDownloadManager`（HTTP/FTP）、`CFtpWorkThread`
- 服务器私有属性 `SU_NEWEST_VER` / `SU_UPGRADE_URL`

包校验/安装流程未还原；C# 只做 GET 检查。

## 12. 无线

- 模板 `Conf/Wireless/baseXml/base.rg`：WLANProfile，`WPA`/`TKIP`，`useOneX=true`，EAP type 25，AuthorId 29115。
- `userCredential.xml`：`EapHostUserCredentials`，type 25，Identity/Password。
- 类：`CWLanWrap`、`CWiFiInterface`、`CWlanStateThread`、`CRefreshWirelessConfigThread`。
- 通过 Windows WLAN API 写 profile；高级设置支持 WPA2/CCMP、SSID 管理。

## 13. 诊断/运维

| 功能 | 原版证据 |
|---|---|
| ARP 网关检测 | `CArpDetectManager/Thread`、`SendARP`、`GetIpNetTable` |
| 网卡枚举/监控 | `CNetAdapterMon`、`EnumNICDevice`、`InitWired/WirelessNicCtrl` |
| 网络诊断 | `NetworkDetect.exe`、`CPingGateWay`、`CIPv4DnsUtil` |
| 代理检测/规避 | `CProxyProcThread`、`IsSocket4/5AgentServer`、`PROXY_AVOID` |
| 拨号规避 | `CCheckDialUpThread`、`CDialUpAdvoidProcessor` |
| 小锐医生 | `CDoctorInterface`、`\\.\PIPE\SU_DOCTOR_INTERFACE` |
| 本司设备识别 | 认证设备 MAC OUI（例如 `00:1a:a9`） |

## 14. 算法汇总

| 算法 | 位置 | 备注 |
|---|---|---|
| CRC16/XMODEM | `0x53DC20` | 表 `0x7372C0`，初值 0，poly 0x1021 |
| 位反转+取反 | `0x53DE90` | 逐字节 `reverse8(~b)` |
| MD5 | `0x53CB50` | 标准 MD5 |
| 凭据加密 | `0x53C970` | MD5(username‖prev) XOR block |
| RC4 | OpenSSL / `0x53...` | 服务器私有属性 0x5C/0x5D |
| DES | OpenSSL / `CSuDES` | MSCHAPv2、SAM |
| MSCHAPv2 | `CEapPEAPThread` | MD4 + DES + SHA1 |

## 15. 函数地址表（VA，ImageBase 0x400000）

| VA | 功能 |
|---|---|
| `0x53A590` | EAP-MD5 私有尾完整组装 |
| `0x53CBB0` | 头 + 混淆，写 23 字节 |
| `0x53DC80` | 头构建（marker/flag/IP/CRC） |
| `0x53DE90` | 字节混淆 |
| `0x53DC20` | CRC16 |
| `0x53CB50` | MD5 |
| `0x53C970` | 凭据加密 |
| `0x5178F0` | EncapRGVerdorSegForEapHost |
| `0x517F90` | EncapRGVerdorSegForPeap |
| `0x518950` | GetClientVersion |
| `0x518DD0` / `0x5191D0` | GetIPv4Info |
| `0x5195B0` | GetMACAddr |
| `0x518C70` | GetDHCPAuthPhase |
| `0x519F80` | GetIPv6Info |
| `0x519620` | GetServiceName |
| `0x519C70` | GetUserPasswd4Peap |
| `0x518A60` | GetV3SegmentHash4Peap |
| `0x51A670` | GetHardDiskSN |
| `0x51A800` | GetSecDomainName |
| `0x51A830` | GetSecCheckResult |
| `0x51A890` | ReadRGVendorSeg |
| `0x537EE0` | 私有属性解析 |
| `0x5397D0` | EAP/PEAP 主处理 |
| `0x53A590` | 私有尾组装 |

RVA = VA − `0x400000`。

## 16. 调用链

### 16.1 头部

```
0x53CBB0
  → 0x53DC80  构建 23 字节头（marker/flag/4×IP/CRC）
  → 0x53DE90  对 23 字节做位反转+取反
  → memcpy 到输出
```

C# `PrivateTail.Header` + `Obfuscate` 与之一致。

### 16.2 凭据

```
0x53C970(key, 16, username, plaintext, len, out)
  → strlen(username)
  → MD5(username || key/prev)
  → XOR plaintext block
  → prev = cipher block
```

C# `PrivateTail.EncryptCredentials` 与之一致。

### 16.3 私有尾

```
0x53A590
  → 0x53CBB0（头）
  → marker + client name/version/flag + marker + total length
  → 0x17 → 0x18 → 0x2D → 0x2F → 0x35 → 0x36 → 0x38
  → 0x4E → 0x4D → 0x39 → 0x54 → ...（条件分支）
```

前 11 项顺序与 C# `BuildTail` 一致。

## 17. 差分测试（原版二进制直接调用）

`8021x.exe` 无导出，使用 Unicorn 映射镜像按 RVA 调用：

| 函数 | 验证结果 |
|---|---|
| CRC16 | 5 组输入全一致 |
| 混淆 | 1/2/7/23/64/255 字节全一致 |
| 头构建 | 零 IP、真实 IP 全一致 |
| 头+混淆 | 23 字节全一致 |
| MD5 | 空/abc/hello/64B 全一致 |
| 凭据加密 | 16/32/48 字节链式全一致 |

未发现不一致。工具：`tools/re_diff/pe_call.py`。
C# 锁定测试：`RuijieCampusAuth.Core.Tests/OriginalDifferentialTests.cs`。

无法直接调用（需要对象/设备状态）：

- `GetClientVersion 0x518950`
- `GetServiceName 0x519620`
- `GetUserPasswd4Peap 0x519C70`（包装层；核心 0x53C970 已验证）
- `GetMACAddr 0x5195B0`

## 18. 未确认项

1. 完整 569 字节私有尾的动态全量比对（需构造 `this`/全局对象）。
2. PEAP 完整 vendor segment 组装（已还原 0x5F，其余段逐步补）。
3. PEAP 服务端证书校验策略（原版是否校验证书/主机名）。
4. SAM 报文完整布局与 DES 作用范围。
5. Portal 响应字段语义、客户端中心 endpoint。
6. `password-change` / `config-update` 的端点与字段。
7. Option 82 是否在新版本引擎中生效。
8. 无线 `WPA_do_*`/PMKID 逻辑是否参与 802.1X 认证。

## 19. 多学校适配

通用壳 + `profiles/*.json`：

- 配置字段见 `profiles/README.md`。
- 逆向生成档案流程见 `docs/ADAPTATION.md`。
- 算法不一致时新增 provider/版本，不修改既有实现：
  `auth_mode` + `auth_version` 选择。

## 20. 复现

```bash
# 静态分析
rea inspect-artifact "C:\Program Files\Ruijie Networks\Ruijie Supplicant\8021x.exe" --json
rizin -q -c "aaa; pdf @ 0x53a590" 8021x.exe

# 差分
python -m pip install pefile unicorn
python tools/re_diff/pe_call.py "C:\...\8021x.exe" 0x13dc20

# C# 验证
dotnet test RuijieCampusAuth.Core.Tests
```

证据目录（本机）：

- `D:\Storageredirect\Program\_rea_analysis\`：Ghidra 反编译、字符串、PE 解析
- `docs/protocols/`：逐协议档案
- `docs/DIFFERENTIAL_TESTING.md`：差分方法与结果
