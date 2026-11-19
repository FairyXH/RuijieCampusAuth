# 链路诊断（ARP / 网卡 / 探测 / 代理 / 拨号）

状态：P4 已实现。全部为只读探测，不修改网络配置。

## 1. ARP 网关检测

原版证据：

- RTTI：`CArpDetectManager`、`CArpDetectThread`、`CARPPacket`
- 字符串：`SendARP`、`DoWithReportResultTimer`、`UM_SEND_ARPDETECT_RESULT`
- 导入：`IPHLPAPI.DLL SendARP` / `GetIpNetTable`

C# 实现：

- `Core/Diagnostics/ArpMonitor.cs`
- 用 `SendARP` 解析网关 MAC，按间隔采样，记录变化。
- 调试页参数：`interface`、`duration`（1–120s）、`interval`（0.2–10s）。
- 只读；不会改 ARP 表或发送伪造 ARP。

## 2. 网卡枚举与监控

原版证据：`CNetAdapterMon`、`CNetAdapterMonThread`、`CSoftwareConfig::InitWiredNicCtrl` /
`InitWirelessNicCtrl`、`EnumNICDevice`。

C# 实现：

- `Core/Diagnostics/NicInventory.cs`
- 列出 Id、名称、描述、类型、状态、MAC、速率、IPv4/掩码/网关。
- 过滤 NDIS 过滤驱动（Npcap/Kaspersky/WFP/QoS/WiFi Filter）与 WAN Miniport，
  按 MAC+描述去重。
- 调试页为快照；长驻热插拔监控留给服务阶段。

## 3. 网络诊断

原版证据：`NetworkDetect.exe`、`CPingGateWay`、`CIPv4DnsUtil`、`CDoctorInterface`。

C# 实现：

- `Core/Diagnostics/NetworkProbe.cs`
- 网关 ICMP ping、DNS 解析、HTTP GET。
- 调试页参数：`interface`、`url`（默认 `settings.connectivity_url`）。
- 结果：网关可达性/耗时、DNS 结果、HTTP 状态码、总耗时。
- 未绑定源接口（与现有 C# 连通检测一致）；需要接口绑定时再补 `IP_UNICAST_IF`。

## 4. 代理检测

原版证据：`CProxyProcThread`、`IsSocket4AgentServer`、`IsSocket5AgentServer`、
`SetProxyDetect`、`PROXY_AVOID`、`PROXY_DECTECT_KINDS`。

C# 实现：

- `Core/Diagnostics/ProxyDetect.cs`
- 对网关常见端口（1080/3128/8000/8080/8888）做 TCP 连接探测。
- 读取 `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` 环境变量。
- 只报告，不修改系统代理。
- 原版的 SOCKS 握手识别与 PROXY_DECTECT_KINDS 位图语义未完全还原，
  当前版本为保守探测。

## 5. 拨号规避

原版证据：`CCheckDialUpThread`、`CDialUpAdvoidProcessor`、`DIALUP_AVOID`。

C# 实现：

- `ProxyDetect.DetectDialup()`
- 只报告 `OperationalStatus == Up` 的 PPP/WWAN 适配器，以及名称含
  PPP/VPN/4G/5G/拨号的活动适配器。
- 未连接的 `WAN Miniport (PPPOE)` 不报告。
- 不主动断开拨号连接。

## 6. 小锐医生管道

原版证据：`CDoctorInterface::SendCMDtoDoctor`、`\\.\PIPE\SU_DOCTOR_INTERFACE`。

C# 实现：`Core/Diagnostics/DoctorPipe.cs`，用 `WaitNamedPipe` 探测管道是否可用；
不发送命令。

## 7. 调试页

| 功能 | name | 参数 |
|---|---|---|
| 锐捷设备识别 | `oui-detect` | `mac`（可选） |
| 网卡监控 | `nic-monitor` | 无 |
| ARP 网关监控 | `arp-monitor` | `interface`/`duration`/`interval` |
| 网络诊断 | `network-probe` | `interface`/`url` |
| 代理检测 | `proxy-detect` | `gateway`/`ports`/`timeout_ms` |
| 拨号规避 | `dialup-detect` | 无 |
| 小锐医生管道 | `doctor-pipe` | 无 |

## 8. 实测（2026-11-19）

- OUI：`00:1a:a9:17:ff:ff` → Ruijie Networks。
- NIC：13 个物理/虚拟主适配器。
- ARP：WiFi 网关 `192.168.137.1` → `9a:bd:80:04:19:7e`，2 次采样稳定。
- 探测：网关 ping 1ms、DNS `www.baidu.com`、HTTP 200。
- 代理：网关常见端口均关闭。
- 拨号：修正后无活动拨号适配器。
- 医生管道：不存在。
