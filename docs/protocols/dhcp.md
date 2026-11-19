# DHCP（原版实现）

状态：P3 已实现。结论：**原版不自己构造 DHCP 报文**，调用 Windows DHCP 服务 API。

## 1. 证据

`8021x.exe` 导入表（IPHLPAPI.DLL）：

- `GetInterfaceInfo`
- `IpReleaseAddress`
- `IpRenewAddress`
- `GetAdaptersInfo` / `GetPerAdapterInfo`（读租约）
- `NotifyAddrChange`（地址变化通知）

`CDhcpThread` 相关函数（0x4a04b0 起）：

| 函数 | 原版行为 |
|---|---|
| `CDhcpThread::RenewDhcpAddr` (0x4a04b0) | `GetInterfaceInfo` → 按适配器名匹配 → `IpRenewAddress` |
| `CDhcpThread::ReleaseDHCP` (0x4a1130 → 0x4a0780) | `IpReleaseAddress` |
| `CDhcpThread::ReleaseAndRenew` (0x4a1190 → 0x4a0950) | release + renew |
| `CDhcpThread::OnDoingDhcp` (0x4a1205 附近) | 按参数选择 Init/Renew/Release/ReleaseAndRenew，PostThreadMessage 回结果 |

没有 DHCP 报文构造特征：无 magic cookie（`63 82 53 63`）、无 UDP 67/68 常量、无 `dhcpcsvc.dll` 导入。

## 2. Option 82

- 配置键 `IsSupportOption82` 只出现在配置序列化/读取路径（`fcn.0056a8f0`），
  未出现在 DHCP 调用链。
- 本引擎版本（5.0.2014.108）**没有主动向 DHCP 报文注入 Option 82**；
  Windows DHCP 客户端本身也不默认发送 Option 82。
- C# 侧保留 `dhcp_option82` 配置与调试页开关，并在结果中明确标注
  “原版该引擎版本未主动注入 Option 82，仅保存配置”。

## 3. 时序

- `InitDhcp` → `IpRenewAddress`。
- `Release` → `IpReleaseAddress`。
- `ReleaseAndRenew` → release，再 renew。
- 由 `CContextControlThread` 按配置的 DHCP 阶段（认证前/后/不使用）触发；
  结果通过线程消息回传。
- 超时来自配置 `DHCP 服务器响应时间`（默认 30 秒）与认证流程超时。

## 4. C# 实现

- `Core/Protocols/DhcpControl.cs`
  - `GetInterfaceInfo` 枚举适配器，按 `NetworkInterface.Name`/`Description` 匹配。
  - `IpReleaseAddress` / `IpRenewAddress`。
  - renew 后轮询 `NetworkInterface`，等待有效 IPv4（超时）。
  - `DhcpOperation.Renew | Release | ReleaseRenew`。
- `Core/Debug/DhcpClientDebugFeature.cs`
  - 参数：`interface`、`operation`、`timeout`、`option82`。
  - 返回：操作结果、耗时、适配器 index/name、IP/掩码/网关。
  - `requires_service=true`（SYSTEM 权限下执行）。
- 测试：未知网卡失败路径；不执行真实 release/renew，避免中断网络。

## 5. 未确认

1. 原版 renew 后等待租约的具体轮询间隔与判定（是否要求 IP 变化）。
2. `IsSupportOption82` 是否在其他引擎版本或自定义 DHCP 驱动中生效。
3. DHCP 阶段与认证失败/抢占的精确交互（需抓包对照）。
