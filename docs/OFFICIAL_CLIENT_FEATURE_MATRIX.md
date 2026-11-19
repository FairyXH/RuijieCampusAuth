# 原版锐捷客户端功能拆解与 C# 落位矩阵

> 目标：把官方客户端里**实际可用的协议与功能**抽离为 C# 实现，先集中放进 GUI/服务的“调试”页，
> 现有认证路径保持不变。纯皮肤、弹窗、公告界面、托盘、安装器、自启动等不抽离。
>
> 分析对象：`C:\Program Files\Ruijie Networks\Ruijie Supplicant`
> 引擎 `8021x.exe` 5.0.2014.108（2019-06-28，MSVC 8.0），UI `RuijieSupplicant.exe` 6.40 分支
> 分析方法：REA 静态分析（PE/Ghidra/RTTI/字符串/日志/皮肤 XML/导出表），未执行样本。
> 日期：2026-11-19

## 1. 现有 C# 基线

| 能力 | 位置 | 状态 |
|---|---|---|
| 有线 EAP-MD5 认证、Start/Logoff、重试、锁定服务器 | `Core/Supplicant.cs`、`Core/Eapol.cs` | 已实现并线上验证 |
| 私有尾包：23 字节头、CRC16、位反转混淆、0x17/0x18/0x2D/0x2F/0x35/0x36/0x38/0x4E/0x4D/0x39/0x54/0x62/0x6B/0x7E/0x70/0x6F/0x79/0x76 | `Core/PrivateTail.cs` | 已实现 |
| 凭据 MD5 链式流密码 | `Core/PrivateTail.cs` | 已实现，569 字节 golden |
| 服务器私有提示文本提取 | `Core/Eapol.cs` | 仅提取中文提示，未做 RC4/TLV 完整解析 |
| 账号池、DPAPI、发现缓存、服务器学习 | `Core/UserStore.cs` | 已实现 |
| 服务模式、接管/IFEO、Guardian 联动 | `Service/*`、`Core/GuardianClient.cs` | 已实现 |
| GUI 五个页签（账号/认证/服务器/服务/设置） | `App/Views/MainWindow.axaml` | 已实现 |
| DHCP 模式/超时/Option82/学校代码 | 设置项 | **仅存储，未真正发 DHCP** |
| PEAP/TLS/MSCHAPv2、Portal、SAM、ARP、升级、无线 | — | **未实现** |

## 2. 原版协议与功能清单

列含义：现状 `已有/部分/缺失`；目标 `调试页` 表示先进调试页，`核心` 表示后续合入主流程，`不做` 表示不抽离。

### G1 认证协议

| ID | 功能 | 原版证据 | 现状 | 目标 | 量级 |
|---|---|---|---|---|---|
| G1-1 | EAP-MD5 有线认证 | `EAPMD5`、`CEapolPacket`、线上验证 | 已有 | 核心 | — |
| G1-2 | PEAP v0/v1 + MSCHAPv2 | `CEapPEAPThread::do_peap_version0/1`、`peap_do_phase2`、`eapmschap_decode_packet`、`mschapv2 challenge/success` | 缺失 | 调试页→核心 | L |
| G1-3 | EAP-TLS（客户端证书） | `EAPTLS`、`tls_decode_packet`、`CertFindCertificateInStore` | 缺失 | 调试页 | L |
| G1-4 | 无线 802.1X（WPA/WPA2-Enterprise） | `CWLanWrap`、`Conf/Wireless/baseXml/userCredential.xml`（EAP type 25）、`CWPAKeyProcess`、PMKID/TKIP | 缺失 | 调试页 | L |
| G1-5 | 服务列表/服务切换 | `servicesCombo` 日志、`DoForSwitchsvrSuccess`、`OnSwitchServiceWaitTimer` | 部分（仅服务名字段） | 调试页 | M |
| G1-6 | 服务器私有属性完整解析（RC4/TLV） | `ReadRGVendorSeg`：ACCOUNT_INFO、BROADCASE_INFO、REJECT_REASON、SERVER_UTC_TIME、SU_*、UTRUST_URL 等 | 部分（仅提示文本） | 调试页→核心 | M |
| G1-7 | 服务器驱动重认证/心跳/指示端口 | `SU_REAUTH_INTERV`、`HELLO_INTERV`、`INDICATE_PORT`、`INDICATE_SERV_IP`、`MSG_CLIENT_PORT` | 缺失 | 调试页→核心 | S/M |
| G1-8 | PEAP 私有段主机字段 | `EncapRGVerdorSegForPeap`：ClientVersion、DHCPAuthPhase、HardDiskSN、IPv4/IPv6、MAC、SecCheckResult、SecDomainName、ServiceName、UserPasswd、V3SegmentHash | 部分（MAC/IP/服务名/静态ID） | 调试页 | M |
| G1-9 | DHCP 认证阶段字段 | `GetDHCPAuthPhase`、`GetDHCPIPInfo` | 缺失 | 调试页 | S |
| G1-10 | 本司设备识别（OUI） | “当前认证设备是我司设备”、MAC 前缀判断 | 缺失 | 调试页 | S |

### G2 地址、链路与诊断

| ID | 功能 | 原版证据 | 现状 | 目标 | 量级 |
|---|---|---|---|---|---|
| G2-1 | DHCP 客户端：认证前/后、Release/Renew、Option 82 | `CDhcpThread::OnDoingDhcp/ReleaseAndRenew/ReleaseDHCP/RenewDHCP`、`CDHCPPacket`、`DHCP Option82` 配置 | 缺失（仅配置项） | 调试页→核心 | M/L |
| G2-2 | ARP 网关检测（防 ARP 欺骗/网关变更） | `CArpDetectManager/Thread`、`CARPPacket`、`SendARP` | 缺失 | 调试页 | S |
| G2-3 | 代理检测/规避 | `CProxyProcThread`、`IsSocket4AgentServer/IsSocket5AgentServer`、`PROXY_AVOID`、`SetProxyDetect` | 缺失 | 调试页 | M |
| G2-4 | 拨号规避 | `CCheckDialUpThread`、`CDialUpAdvoidProcessor`、`DIALUP_AVOID` | 缺失 | 调试页 | S |
| G2-5 | 网卡监控/热插拔 | `CNetAdapterMon`、`EnumNICDevice`、`InitWiredNicCtrl/InitWirelessNicCtrl` | 部分（一次性解析） | 调试页 | S |
| G2-6 | 网络诊断（网关/DNS/HTTP/医生管道） | `NetworkDetect.exe`、`CPingGateWay`、`CIPv4DnsUtil`、`\\.\PIPE\SU_DOCTOR_INTERFACE` | 部分（仅 TCP 探测） | 调试页 | S |

### G3 Portal / SAM / 客户端中心

| ID | 功能 | 原版证据 | 现状 | 目标 | 量级 |
|---|---|---|---|---|---|
| G3-1 | Portal eportal 认证 login/logout/在线信息 | `PortalAuth.dll` 导出 `StartPortalAuth`/`SendLogout`；`eportal/inferface/authAPI/{login,logout,getOnlineUserInfo,pageInfo}` | 缺失 | 调试页 | M/L |
| G3-2 | Portal UDP 消息 | `OnRecvePortalUDPPacketReturn`、`CPortalMsgDlg` | 缺失 | 调试页 | S |
| G3-3 | SAM 直通（DES/UDP） | `dircomm.log`：协议版本 2、重传 2、超时 2000、DES key/IV、UTC、SU PORT 138、SAM IP/53；`CDirectTranSrv`、`CSuDES`、`OnInit_SAM` | 缺失 | 调试页 | XL |
| G3-4 | 客户端中心上报/查询 | `CClientCenterPeerManager::ProcessConnect` 查询串：mac/ipv4/ipv6/product/mainver/subver/schoolcode/osbits/releasedate | 缺失 | 调试页 | M |
| G3-5 | 自助服务/在线信息（ball JSON） | `SelfSvrURL`、`ParseBallInfoJson`、`RefreshBallInfo`、`收到自助请求返回` | 缺失 | 调试页 | M |
| G3-6 | 余额/套餐/在线时长 | `layout_authdialog.xml`：余额、套餐、在线时长、可用至 | 缺失 | 调试页 | M |
| G3-7 | 修改密码 | `CModifiPassword`、`layout_modifipassword.xml` | 缺失 | 调试页 | M |

### G4 运维、升级与无线分享

| ID | 功能 | 原版证据 | 现状 | 目标 | 量级 |
|---|---|---|---|---|---|
| G4-1 | 升级检查/下载 | `DoUpgrade`、`update.rghall.com.cn/das/update`、`CDownloadManager`、`CFtpWorkThread` | 缺失 | 调试页 | L |
| G4-2 | 服务器列表更新 | `OnServerlistUpdate`、`OnRcvSvrSwitchResult` | 缺失 | 调试页 | S |
| G4-3 | 配置更新事件 | `RecvConfUpdateEvent`、`OnConfigureUpdate` | 缺失 | 调试页 | M |
| G4-4 | 用户消息/广播 | `CMsgUtil`、`BROADCASE_INFO`、`DoWithRcvSvrMSG` | 部分（提示文本） | 调试页 | S |
| G4-5 | 小锐医生管道 | `CDoctorInterface::SendCMDtoDoctor`、`\\.\PIPE\SU_DOCTOR_INTERFACE` | 缺失 | 调试页 | S |
| G4-6 | 小锐 WiFi 分享/NAT/ICS | `CWiFiInterface`、`AllowXRWifi`、`rjwifi` 驱动、NAT/ICS 设置 | 缺失 | 调试页（低优先） | L |

### 明确不抽离（按“只要协议/功能”原则）

皮肤与 DuiLib 布局、公告/通知弹窗、充值界面、用户协议、WiFi 介绍、托盘菜单、语言切换、安装器与升级 UI、自启动注册、软件管理 UI、崩溃转储、单实例/激活、聊天式消息弹窗。
这些背后的协议若被上面的功能复用（如用户消息），按 G4-4 处理；否则不做。

## 3. 调试页设计

### 3.1 GUI

在 `MainWindow.axaml` 新增 `调试` TabItem，绑定新的 `DebugViewModel`；现有五个页签不动。

分区：

1. **认证协议实验**
   - PEAP/MSCHAPv2：用户名/密码/服务名、TLS 校验模式（严格/仅记录）、超时；运行并显示 EAP 步骤。
   - EAP-TLS：证书选择（Windows 证书库/文件）、运行。
   - 服务切换：下拉服务列表、切换并显示结果。
   - 私有属性查看：最近一次 EAP-Success 的私有段 hex、TLV 表、RC4 解码开关。
2. **地址与链路**
   - DHCP：获取/续租/释放、Option 82、认证前/后模式、超时；显示租约。
   - ARP：网关 MAC 监控、变更告警、当前 ARP 表。
   - 代理检测：SOCKS4/5/HTTP 探测、结果与耗时。
   - 拨号检测：PPP/3G/4G 适配器列表与冲突判断。
   - 设备识别：本机 MAC OUI、是否锐捷设备。
   - 诊断：网关 ping、DNS 解析、HTTP 探测、医生管道 ping。
3. **Portal / SAM / 中心**
   - Portal：URL、登录/登出/在线用户/页面信息；显示请求与响应（脱敏）。
   - SAM：SAM IP/端口、DES key/IV（从配置/手动）、探测与结果。
   - 客户端中心：查询串预览、发送、响应解析。
   - 自助服务：URL、拉取 ball JSON、余额/套餐/在线时长展示。
   - 修改密码：原密码/新密码、执行、结果。
4. **无线**
   - 列出 Windows WLAN 配置、创建/删除 WPA2-Enterprise 配置（SSID、身份、PEAP 参数）、连接/断开。
5. **运维**
   - 升级检查（URL）、服务器列表更新、配置更新、用户消息拉取。
6. **观测台**
   - 原始帧抓取（EAPOL 过滤）、hex 视图、导出 pcap/evidence、调试日志尾随。

所有动作必须**手动触发**；每个分区显示“实验”标记和最后结果；密码/密钥输入框默认掩码。

### 3.2 服务端

- `settings.json` 增加向后兼容的 `debug` 段：

```json
"debug": {
  "enabled": false,
  "features": {
    "peap": false, "eap_tls": false, "dhcp": false, "arp": false,
    "portal": false, "sam": false, "client_center": false,
    "wireless": false, "upgrade": false, "self_service": false,
    "proxy": false, "dialup": false, "doctor": false
  },
  "endpoints": {
    "portal": "", "clientCenter": "", "upgrade": "",
    "selfService": ""
  },
  "tls": { "verify": true, "caPath": "" },
  "logLevel": "info"
}
```

- 新增 `Core/Debug/DebugFeatureRegistry.cs`：每个功能实现 `IDebugFeature`（`Name/IsEnabled/StatusAsync/RunAsync/StopAsync`），统一注册、统一审计日志。
- 服务新增 `DebugPipeServer`：命名管道 `\\.\pipe\HXXY-CampusAuth-Debug`，仅本机、ACL 限制 SYSTEM + 当前用户；JSON 行协议 `{id,feature,action,args}` → `{id,ok,data,error}`。
- 后台类功能（ARP、代理、DHCP 监听、WLAN 监控）只有显式 `start` 后才运行，`stop`/服务停止即释放；不做常驻。
- GUI 在服务运行时优先走管道；服务未运行时可在 GUI 进程内直接调用（便于调试），但 DHCP/ARP/WLAN 等需要 SYSTEM 权限的功能提示“需服务模式”。

### 3.3 代码落位

```
Core/Debug/            DebugSettings, DebugFeatureRegistry, IDebugFeature, DebugLog
Core/Protocols/        Peap.cs, EapTls.cs, PrivateProperties.cs, DhcpClient.cs,
                       PortalClient.cs, SamDirect.cs, ClientCenterClient.cs,
                       SelfServiceClient.cs, UpdateClient.cs, PasswordClient.cs
Core/Diagnostics/      ArpMonitor.cs, NetworkProbe.cs, ProxyDetect.cs,
                       DialupDetect.cs, OuiDetect.cs, DoctorPipe.cs
Core/Wireless/         WlanProfileManager.cs
Service/               DebugPipeServer.cs
App/ViewModels/        DebugViewModel.cs（+ 子 ViewModel）
App/Views/             DebugView.axaml，或直接在 MainWindow 增加 TabItem
Core.Tests/            PeapTests, PrivatePropertiesTests, DhcpTests, PortalTests ...
```

现有 `Supplicant.cs`、`Eapol.cs`、`PrivateTail.cs` 不改动；新协议作为独立模块，避免回归。

## 4. 实施波次

| 波次 | 内容 | 预计 | 验收 |
|---|---|---|---|
| P0 | 调试页骨架、DebugSettings、FeatureRegistry、日志与管道桩、GUI 页签 | 1–2 天 | 现有认证回归通过；调试页可开关、无副作用 |
| P1 | 私有属性完整解析/查看器、服务器重认证/心跳、OUI、NIC 监控、ARP、网络诊断、DHCP 客户端+Option82 | 1–2 周 | 抓包对比原版；DHCP 租约字段一致；ARP 变更可复现 |
| P2 | Portal eportal、客户端中心、自助信息/余额、修改密码、服务器列表/配置更新 | 1–2 周 | 抓包/回放对比；请求字段与原版一致 |
| P3 | PEAP/MSCHAPv2、EAP-TLS、无线 WPA2-Enterprise 配置 | 2–4 周 | 实验 RADIUS/hostapd 全流程；证书策略可配置 |
| P4 | SAM 直通、升级检查/下载、代理/拨号检测 | 2–4 周（SAM 不确定） | SAM 报文与原版逐字段对比；升级包校验通过 |

每波次独立提交、独立验收；P0–P2 不触碰现有认证主路径。

## 5. 需要反编译/抓包补的未知项

1. PEAP 的服务端证书校验策略：是否校验证书、是否允许自签、SNI/主机名规则。
2. SAM 直通报文结构：DES 作用范围、版本 2 字段、UTC/重传/端口语义。
3. Portal eportal 请求体与签名：是否带 token/时间戳/设备指纹。
4. 客户端中心的实际 endpoint 与响应字段（查询串已从日志获得）。
5. 私有属性 TLV 类型表：目前从日志得到字段名，需与抓包逐项对应。
6. 修改密码的实际协议（eportal / SAM / 其他）。
7. 升级包的下载与校验方式（HTTP/FTP、哈希/签名）。

## 6. 风险

- 账号锁定：PEAP/Portal 失败重试会触发服务器锁定，调试页必须限制尝试次数并默认单次执行。
- 网络中断：DHCP Release 会短暂断网，必须二次确认。
- 无线配置：写 Windows WLAN 配置会影响系统网络，需可回滚。
- 凭据泄漏：调试日志、请求导出必须脱敏；沿用 DPAPI。
- 版本漂移：原版为 2018–2019 老版本；若学校升级客户端，协议需重新核对。
