# R3G 稳定性监测报告（2026-11-23）

- 监测窗口：2026-11-23 **02:04 – 09:30**，每 15 分钟一次；最后一次实时检查 **09:37**
- 设备：Xiaomi R3G，OpenWrt 24.10.0，内核 6.6.73
- 固件：**0.11.4**（06:16 重刷；`ruijie-auth 0.11.4`，ZeroTier 1.14.1，cfg80211 无线解锁）
- 网络：校园 WAN `<wan-ip>`，认证服务器 `<auth-server-mac>`
- 详细原始记录：`logs/router-stability-2026-11-23.md`

## 结论摘要

**稳定性评级：高。** 06:00 认证恢复成功，重刷后认证连续稳定；ZeroTier 上线并保持；2.4G/5G 未发生掉频、雷达或功率异常。**SAM 直通心跳在本校园不可用**：目标 `172.22.11.1:53` 不可达，0.11.3 默认开启时每约 1 分 36 秒触发一次重认证；已在 06:09 关闭止损，并在 0.11.4 中把默认值改回关闭。09:03 出现一次 4 秒瞬时认证失败，已自愈，未复现。

## 时间线

| 时间 | 事件 |
|---|---|
| 02:04 | 基线：认证 paused（00:00–06:00），ZeroTier OFFLINE，无线 ch13/ch60 正常 |
| 02:08–05:53 | 每 15 分钟检查：认证持续 paused，ZeroTier OFFLINE/REQUESTING，无线/系统稳定 |
| 06:00:04 | 静默窗口结束，开始 EAP-MD5 认证 |
| 06:00:05 | **认证成功**；SAM 心跳启动，目标 `172.22.11.1:53`，每 30s |
| 06:00:41–06:01:53 | 连续 3 次心跳无响应 → 06:01:53 触发重新认证，认证成功 |
| 06:03:41 / 06:05:29 / 06:07:17 / 06:09:06 | 同样的 3 次无响应→重认证循环（约每 1 分 36 秒一次） |
| 06:09:14 | `uci set heartbeat=0` + restart，止损；认证保持 authenticated，UDP 138 消失 |
| 06:16 | 重编并 sysupgrade **0.11.4**，默认心率关闭；认证成功 |
| 06:22–09:22 | 每 15 分钟检查：认证稳定、ZeroTier ONLINE、无线正常 |
| 09:03:46 | 单次认证失败（服务器未返回可读原因），09:03:49 重试，09:03:50 成功 |
| 09:37 | 最后一次检查：authenticated（09:03:50 起），心跳关闭，ZeroTier ONLINE |

## SAM 直通心跳

- **启动条件满足**：认证成功后从服务器属性拿到 `ENCRYPT_KEY/IV`、`INDICATE_SERV_IP=2887125761`（即 `172.22.11.1`）、`INDICATE_PORT=53`、`MSG_CLIENT_PORT=138`，心跳启动，UDP 138 本地端口出现。
- **失败表现**：每 30s 发送，连续 3 次无响应后按官方逻辑“直通心跳超时，触发重新认证”；认证本身每次都能成功，但约 1 分 36 秒循环一次，共 5 次。
- **根因**：`ping 172.22.11.1` 100% 丢包；`nslookup @172.22.11.1:53` 超时；公共 DNS `223.5.5.5` 正常；路由为 `172.22.11.1 via <gateway> dev wan src <wan-ip>`。SAM direct-comm 目标在当前校园网不可达（或 53/UDP 被阻断），不是报文/代码在路由器侧能立即修复的问题。
- **处置**：06:09 关闭心跳止损；06:16 重编 0.11.4，把 Go 默认、UCI 默认、LuCI 开关默认全部改为**关闭**。
- **当前状态**：`heartbeat=0`，无 SAM 日志、无 UDP 138、无重认证循环；LuCI `设置 → SAM 心跳` 开关保留。
- **建议**：本校园保持关闭。只有确认 SAM 目标可达（例如校园网路由/VLAN 调整后能 ping 通 172.22.11.1）再手动开启；开启前先看 `ping`/`nslookup`。后续可考虑给心跳增加“目标不可达时不 arm / 首轮超时自动停用”的保护，避免类似环境再次循环。

## 认证稳定性

- 06:00:05 首次认证成功；心跳开启期间每次重认证也都成功（服务端没有拒绝）。
- 06:16:34 / 06:16:42 重刷 0.11.4 后认证成功；此后心跳关闭，连续稳定 **2 小时 47 分**（06:16:42–09:03:46）。
- **09:03:46 单次失败**：日志“认证失败（服务器未返回可读原因）”，09:03:49 重试，09:03:50 成功；仅 4 秒。服务器属性 `SU_REAUTH_INTERV=0`，不像定时重认证，更像一次网络/服务器瞬时抖动。之后到 09:37 未复现。
- 最终状态：`authenticated`，账号 `<account>`，server `<auth-server-mac>`。

## ZeroTier

- 00:00–06:00 无外网，`OFFLINE` / `REQUESTING_CONFIGURATION`（预期）。
- 06:00 认证/外网恢复后转为 **ONLINE**；网络 `<zerotier-network>`（`<zerotier-network-name>`），状态 `OK`，分配 IP **`<zerotier-ip>/24`**。
- 之后全程保持 ONLINE，包括 09:03 认证抖动期间未掉线。
- 你的 `/etc/config/zerotier`（secret、网络 ID、enabled）在 sysupgrade 中保留；LuCI 入口 **VPN → ZeroTier**。

## 无线

- 2.4G：**ch13 / HT20 / 26 dBm**，全程未变。
- 5G：**ch60 / VHT80 / center58 / 23 dBm**，全程未变。
- 未出现 radar/DFS 日志、接口 down、txpower 异常；监测期间两个 AP 均无客户端连接。
- 无线解锁 `cfg80211.ko` 始终为 `5c5bd4…`。

## 系统

- 最终 uptime 3 小时 21 分（06:16 重刷后），load 0.65，内存 48MB free / 108MB available，overlay 46.5M 空闲。
- 无 OOM、无重启、无 overlay 耗尽。
- 非 UA3F 告警：dnsmasq 每约 4 分钟报 `possible DNS-rebind attack detected: ntp.cloud.aliyuncs.com`，属 DNS rebind 保护误报，不影响认证/网络/时间。
- 无外网期间 UA3F 的 `[::1]:53 timeout` 日志是预期噪音；认证恢复后不再出现。

## 本次修复与产物

- **0.11.4**：SAM 心跳默认关闭（Go `Heartbeat: false`、UCI `heartbeat '0'`、LuCI 默认关闭）。
- 06:16 sysupgrade 到 0.11.4；认证成功、心跳关闭、ZeroTier ONLINE、无线保持。
- 产物：
  - sysupgrade `3f679b73612480a3e0d5a2251db86e9336c63d0c2625620d80f5327a8e424b18`
  - Breed `40007c4194969458ca47d77d6b4ba0f31e187720ded54b6b199abb810aba682f`
  - rootfs0 `c2c737bdcda62c3fb9e75f9848b0ea9554660fb618a35def5c4fb4a95cd59c38`
  - 认证器 ipk `781d1b8d5a7c98bc643cd0fd24d4fc4a816c9ab6dae127ecfc2cba4777161a2c`
  - `checksums.txt` 全部通过；README、刷写教程、AGENT_CONTEXT 已更新。

## 建议

1. **心跳保持关闭**；本校园 SAM `172.22.11.1` 不可达，开启只会周期性重认证。
2. 若未来校园网调整路由/端口后 SAM 可达，先在路由器上 `ping 172.22.11.1` 和 `nslookup @172.22.11.1:53` 验证，再在 LuCI 开启心跳并观察 1–2 个周期。
3. ZeroTier 工作正常，保持现状；如需固定 IP/路由，在 ZeroTier 控制台配置。
4. dnsmasq rebind 误报可在 LuCI DHCP/DNS 里把 `ntp.cloud.aliyuncs.com` 加到 rebind 白名单，减少日志噪音（可选）。
5. 09:03 的瞬时认证失败属于单次抖动；若后续频繁出现，优先检查校园网服务器负载/账号抢占。
