# OpenWrt 软路由移植可行性评估

日期：2026-11-18

> **历史文档**：本文是移植前的可行性评估，保留当时对早期原型、UA3F、
> https-dns-proxy、full-buildroot 等方案的权衡记录。最终认证器仍为
> `ruijie-auth` 纯用户态 ipk；固件构建后来恢复 UA3F/https-dns-proxy
> 作为整机功能。当前架构、包名与验证结果见 `docs/OPENWRT_PORT.md` 与
> `openwrt/README.md`。

范围：`RuijieCampusAuth.Core`、`.Service`、`.Cli`、`.App` 与 `python/` 参考实现。

目标：在 OpenWrt 软路由上完成锐捷校园网 802.1X 认证，并保留账号池、失败切换、
服务器学习/锁定、发现缓存、状态/日志、家庭网络暂停等管理能力。

## 0. R3G 专项复核（2026-11-18）

### 0.1 硬件与 OpenWrt 支持

| 项目 | 原版 R3G | R3Gv2 / 4A 千兆版 |
| --- | --- | --- |
| SoC | MT7621A，MIPS 1004Kc，2C/4T，880MHz | 同 |
| RAM | 256MB DDR3 | 128MB |
| Flash | 128MB SLC NAND | 16MB SPI |
| 网口 | 1 WAN + 2 LAN，GbE | 同 |
| USB | USB 3.0 | 无 |
| Wi-Fi | MT7603 2.4G + MT7612 5G | 同/4A 版本有差异 |
| OpenWrt 24.10 profile | `ramips/mt7621` → `xiaomi_mi-router-3g` | `xiaomi_mi-router-3g-v2` 或 `xiaomi_mi-router-4a-gigabit` |
| 24.10 sysupgrade | 7,312,008 B | 7,209,603 B |
| 24.10 rootfs0 | 5,242,880 B | 同量级 |
| 架构 | `mipsel_24kc`，kernel 6.6.73 | 同 |

先确认板子：

```sh
ubus call system board
cat /proc/mtd
free -m
df -h /overlay
opkg print-architecture
```

`board_name` 为 `xiaomi,mi-router-3g` 且 mtd 里有 `nand`/`ubi`，才是 128MB NAND
原版。`xiaomi,mi-router-3g-v2`/`xiaomi,mi-router-4a-gigabit` + `spi` 是 16MB SPI 版。

### 0.2 运行时选择

- .NET 10 没有 linux-mips32 运行时，R3G 上 C# 方案直接排除。
- Go 支持 `linux/mipsle`。交叉编译：
  `GOOS=linux GOARCH=mipsle GOMIPS=softfloat CGO_ENABLED=0 go build -trimpath
  -ldflags "-s -w"`。预计 7–10MB（含 `x/text` + `net/http`）；原版 128MB NAND
  足够。
- Python + scapy 能跑，但 20–40MB、CPU/启动开销大，只适合 PoC。
- C 静态二进制约 100–300KB，适合 16MB SPI 版；代价是 GB18030 要带
  `libiconv-full`，管理面开发更慢。

### 0.3 UA3F 与 MIPS 32 的硬限制

UA3F v3.7.0 提供 `ua3f_3.7.0-r1_mipsel_24kc.ipk`（5.43MiB），依赖
`kmod-nft-queue`、`kmod-nft-tproxy`。OpenWrt 24.10 的 6.6.73 `mipsel_24kc`
kmods 仓库里两者都有。

L3 改写能力：

| 字段 | 行为 |
| --- | --- |
| TTL | 固定值，1–255，默认 64 |
| IPID | 置 0 |
| TCP Timestamp | 删除 |
| TCP Initial Window | SYN 窗口置 65535 |
| QUIC | 丢弃 UDP/443 |

**硬限制：UA3F 的 eBPF 加速要求 kernel ≥ 5.15 且 CPU 架构支持；官方明确
MIPS 32 不支持。** R3G 只能走 netfilter/NFQUEUE 用户态路径。UA3F 文档给出的
NFQUEUE 基准（测试机）是 409.83 Mbps / 44.87% CPU；MT7621 比测试机弱，
实际吞吐必须实测。TTL 精确改写还要求关闭 flow offload（UA3F 文档明确说明
offload 路径的 TTL 不能保证精确：`ttl-value: 255` 会变成 254；要求所有出包
TTL 一致时应关闭 flow offload），NAT 吞吐会进一步下降。

结论：

- 校园链路 100Mbps：R3G + NFQUEUE 大概率够用，先实测。
- 校园链路 1Gbps：NFQUEUE 很可能成为瓶颈；把 L3 改写限制在必要字段，
  或改用 C/nftables 内核快路径。
- 只要 TTL + UA：nftables 改 TTL/MSS + UA2F（267KB）改 UA，避免 UA3F
  的全量 NFQUEUE。
- 需要 IPID/TCP timestamp/window：只能用 UA3F 的 NFQUEUE 路径，接受性能代价。

### 0.4 定制 OpenWrt 的必要性

原版 R3G（128MB NAND）不需要 full buildroot：

- 官方 24.10 sysupgrade + `opkg install ua3f kmod-nft-queue kmod-nft-tproxy`
  + 交叉编译的 `ruijie-auth` + 配置/init/hotplug 即可。
- 想要可复现/可恢复，用 **Image Builder** 把包和 `files/` 烤进镜像；
  不需要编译整个 OpenWrt。
- 只有以下情况才需要 full buildroot：
  - 要改 kernel config（某个 netfilter 选项没编进官方 kernel）。
  - 要打 UA3F/内核补丁（MIPS 32 eBPF 不在支持列表，不建议）。
  - 要给 16MB SPI 版做裁剪镜像，或把 C daemon 做成包。
- 16MB SPI 版：OpenWrt rootfs 约 5.2MB + kernel 约 1.8MB，overlay 只剩约
  8MB；UA3F 5.4MB + Go daemon 8MB 放不下。需要 C daemon + UA2F（267KB）
  + nftables，或者换原版 R3G。
- 版本建议：24.10（opkg）。25.12 改用 apk，UA3F 两种包都有；23.05 的
  kmods 版本不同，需确认兼容或自行编译。

Image Builder 形态（原版 R3G）：

```sh
make image PROFILE=xiaomi_mi-router-3g \
  PACKAGES="luci-ssl ua3f kmod-nft-queue kmod-nft-tproxy nftables curl" \
  FILES=files/
```

`files/` 里放：`ruijie-auth` 二进制、`/etc/init.d/ruijie-auth`、
`/etc/config/ruijie-auth`、`/etc/hotplug.d/iface/90-ruijie-auth`、
`/etc/sysctl.d/90-ruijie.conf`、UA3F 配置。UA3F 若不在官方 feed，可以解包
ipk 把 `/usr/bin/ua3f` 放进 `files/`，或刷机后 `opkg install` 本地 ipk。

### 0.5 网络伪装栈（NAT 检测规避）

公开资料没有锐捷“防私接”的完整算法；下表按常见校园网检测面拆，
必须用实测确认哪些真的启用。

| 检测面 | 常见信号 | R3G 方案 | 覆盖度 | 备注 |
| --- | --- | --- | --- | --- |
| TTL | NAT 后 TTL 递减/多值 | UA3F `ttl:true` + `ttl-value:128`；`net.ipv4.ip_default_ttl=128`；nftables 兜底 | 高 | 关 flow offload |
| IPID | 多主机 IPID 跳变 | UA3F `ipid:true` → 0 | 中高 | 0 不是 Windows 顺序，高级指纹可能识别 |
| TCP timestamp | 多主机时钟偏移 | UA3F `tcpts:true` 删除 | 中高 | 先确认校园基线是否 Windows timestamps disabled |
| TCP initial window | 多主机窗口差异 | UA3F `tcpwin:true` → 65535 | 中 | 只改 SYN |
| MSS | MTU/MSS 差异 | nftables `tcp option maxseg size set 1460` | 高 | 内核快路径 |
| TCP options 顺序 | p0f 被动指纹 | 无 | 低 | 需要用户态 TCP 栈/MITM，成本高 |
| DHCP | option 55/60/61、hostname、vendor | udhcpc/netifd 配成 Windows：`MSFT 5.0`、Windows PRL、`DESKTOP-*` | 高 | OpenWrt 默认 `udhcp`/`OpenWrt` 是明显特征 |
| MAC OUI | 小米 OUI | 克隆原 Windows 网卡 MAC | 高 | 同时解决可能的账号 MAC 绑定 |
| HTTP UA | 多设备 UA | UA3F GLOBAL 改写 | 中高 | 仅 HTTP；HTTPS 不可见 |
| TLS/JA3 | ClientHello 指纹 | 无（MITM 需装 CA） | 低 | 残余风险 |
| DNS | 查询模式 | 无 | 低 | 残余风险 |
| IPv6 | 多地址/前缀 | 关闭 IPv6 或 NAT66 | 高 | 建议直接关 |
| 主动探测 | 端口扫描/栈指纹 | WAN drop、关服务、drop 不 reject | 中高 | 路由器自身仍是 Linux 栈 |
| 并发/流量 | 连接数/流量突增 | QoS/连接限制 | 低 | 策略层 |
| UPnP/NAT-PMP | 端口映射 | 关闭 | 高 | |
| QUIC | UDP/443 | UA3F `block-quic` | 高 | 强制 TCP；HTTPS UA 仍需 MITM；可能影响应用 |

关键判断：

- 原版客户端的客户端侧检查（多网卡、ICS、VMware、代理进程）在路由器方案里
  自然消失；剩下的是网络侧被动/主动检测。
- TTL、DHCP 指纹、MAC OUI、IPv6、WAN 服务是最容易暴露的，优先处理。
- IPID/TCP timestamp/window 需要 UA3F 的 NFQUEUE，性能和收益要权衡。
- TLS/JA3、TCP options 顺序、DNS 模式是残余风险；如果校园网做到这一层，
  R3G 方案不能保证不被识别。
- 这不是“一次配置永久隐身”，是持续对抗；校园网侧升级可能让配置失效。

按代价分层，不要一开始就全量 UA3F：

| 层 | 内容 | 代价 | 适用 |
| --- | --- | --- | --- |
| Tier 0 | MAC 克隆、DHCP 指纹、TTL 128、MSS clamp、IPv6 关闭、WAN drop、UPnP 关闭 | 低，内核快路径 | 所有场景，先做 |
| Tier 1 | UA2F 改 HTTP UA（267KB） | 低，NFQUEUE 只处理 HTTP | 校园侧看 UA 时 |
| Tier 2 | UA3F L3 全量（TTL/IPID/TS/window） | 高，NFQUEUE + 关 offload | Tier 0/1 仍被检测时 |
| Tier 3 | MITM/用户态 TCP 栈/TLS 指纹伪装 | 很高，需客户端 CA | 基本不建议，除非明确知道校园侧看 JA3 |

### 0.6 DHCP 前置已确认

用户已确认 DHCP 在认证前可用，之前最大的 Go/No-Go 消除。剩余未知点见第 7 节。

### 0.7 约束更新（2026-11-18）

用户补充：

- 管理面必须在 OpenWrt Web 里有 LuCI。
- WAN 纯 DHCP，没有 PPPoE/VLAN。
- 校园链路 100Mbps。
- 目前没有真机；外观/规格看是原版 R3G（双频 AC1200、千兆）。
- Windows 移动热点共享从未被踢下线，校园网似乎不检测多设备。

影响：

- **LuCI 从可选变硬需求。** 后端最稳的是 `rpcd` + CLI（JSON 输出）：
  LuCI 用 `L.uci` 管非敏感设置，用 `fs.exec` 调 CLI 管状态/账号/服务器/
  日志。守护进程不需要监听 HTTP 端口，也不需要 Go 侧实现 ubus。
- **100Mbps 让 NFQUEUE 风险大幅下降。** MT7621 在 100Mbps 下跑 UA3F
  NFQUEUE 大概率够用；仍要 `iperf3` 实测，但不再是 Go/No-Go。
- **Windows 热点证据说明校园侧检测很弱。** Windows ICS 会 NAT、TTL 递减、
  多设备指纹混杂；这都没被踢，说明 TTL/IPID/UA 检测即使存在也没有强制。
  所以 Tier 0（MAC/DHCP/TTL/MSS/IPv6/WAN）大概率够用，UA3F 降为可选；
  默认 `disguise=basic`，需要时在 LuCI 里切 `full`。
- **纯 DHCP 简化 WAN 解析。** 不需要 PPPoE/VLAN 处理；重点是 DHCP 指纹和
  DHCP 租约时序。`wan` 的 IPv4 地址由 netifd/udhcpc 拿到后，daemon 再发
  EAPOL。
- **没有真机**：先做单元测试 + veth 假认证器 + OpenWrt x86/QEMU 的 LuCI
  验证；R3G 上的 DSA EAPOL、DHCP 时序、MAC 克隆留到上机。板型（原版 vs
  R3Gv2/4A）也要等 `ubus call system board` 确认。
- **默认按原版 R3G 设计**（256MB RAM/128MB NAND），同时保留 16MB SPI 的
  C + UA2F 降级路径；如果真机是 R3Gv2/4A，再切。

## 1. 结论

**认证算法：可行，风险低。**

- 私有尾、EAPOL 组帧、EAP-MD5 全部是用户态字节处理，不依赖 Windows API。
- 569 字节私有尾有确定性测试向量：
  `RuijieCampusAuth.Core.Tests/PrivateTailTests.cs` 的
  `TailMatchesPythonReference`，SHA256
  `779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5`。
  这个向量可以直接作为 Go/C 移植的 golden test。
- 需要移植的密码学只有 MD5、CRC16/XMODEM、位反转取反、MD5 链式 XOR，
  标准库都能覆盖。
- 唯一字符集依赖是 GB18030。Go 用
  `golang.org/x/text/encoding/simplifiedchinese`，C 需要 `libiconv-full`，
  Python 原生支持。

**管理功能：大部分可移植，约三分之一是 Windows 专属。**

- 可直接移植：账号池、启用/停用、当前账号、多账号失败切换、发现缓存、
  服务器学习/锁定、日志、状态、家庭网络暂停、自动启动。
- 需要替换：服务安装（SCM/systemd → procd）、数据路径（exe 目录 →
  `/etc` + `/var`）、密码保护（DPAPI → 0600/UCI）、Guardian（回环 MCP →
  WAN 绑定探测）、GUI（Avalonia → LuCI/CLI）。
- 直接丢弃：接管模式（IFEO、杀原版进程、`RJSuService`、VBS 启动器）。
  这些在路由器上没有作用对象。

**整体判断：技术可行，工作量中等，真正的风险不在协议，而在网络侧未知点和硬件资源。**

**R3G 结论：原版 R3G（MT7621A、256MB RAM、128MB NAND）是合适的目标。认证 daemon
用 Go mipsle 静态二进制；网络伪装用 UA3F/UA2F + nftables/sysctl。R3Gv2/4A 千兆版
是 16MB SPI，放不下 Go + UA3F，需要 C + UA2F 裁剪方案。MIPS 32 不支持 UA3F eBPF，
NFQUEUE 吞吐是主要风险。**

## 2. 为什么不建议整体搬 .NET/Avalonia

- OpenWrt 是 musl + BusyBox + procd，不是 glibc/systemd 发行版；.NET 10
  官方不支持 OpenWrt。
- .NET 运行时只有 x64/arm/arm64（含 musl），没有 `mips/mipsle`；R3G 是
  MIPS 32，直接没有运行时。自包含发布约 80–120 MB，x86 软路由能放，
  小闪存 ARM 也紧张。
- 还需要额外处理 ICU/globalization invariant、libstdc++/libssl、
  `CodePagesEncodingProvider`；GB18030 在 invariant 模式下是否可用需要实测。
- `Microsoft.Extensions.Hosting.Systemd` 依赖 systemd notify；OpenWrt 用
  procd，`ServiceInstaller` 写 `/etc/systemd/system` 无效。
- Avalonia GUI 需要显示服务/字体，路由器通常无头；即使有 framebuffer，
  也不适合作为管理界面。

结论：.NET 只能是 x86 软路由上的临时验证；R3G 目标机直接排除。

## 3. 可移植性矩阵

| 文件 | 依赖 | 可移植性 | 处理方式 |
| --- | --- | --- | --- |
| `Core/PrivateTail.cs` | BCL + CodePages | 高 | 1:1 重写，保留 569 字节 golden test |
| `Core/Eapol.cs` | BCL + GB18030 | 高 | 1:1 重写，保留服务器消息解析 |
| `Core/Pcap.cs` | wpcap/libpcap P/Invoke | 中 | 可用 libpcap，或改 AF_PACKET（推荐，去掉依赖） |
| `Core/Supplicant.cs` | pcap + 状态机 | 高 | 逻辑可移植；需从“一次性尝试”改为“常驻监听” |
| `Core/ServiceConfig.cs` | JSON + DPAPI | 中 | JSON 可移植；DPAPI 分支删除，Unix 用 0600 |
| `Core/UserStore.cs` | JSON + DPAPI | 中 | 同上；`MigrateLegacy` 删除 |
| `Core/GuardianClient.cs` | 127.0.0.1 MCP | 低 | 路由器上无意义；改为 WAN 绑定探测 |
| `Core/TakeoverPolicy.cs` | 注册表/服务 | 无 | 丢弃 |
| `Core/ServiceInstaller.cs` | SCM/systemd | 低 | 改为 procd init + hotplug |
| `Core/ServiceStatus.cs` | exe 目录 JSON | 中 | 改 `/var/run/ruijie-auth/status.json` |
| `Service/AuthWorker.cs` | Hosting + Guardian | 高 | 业务逻辑可移植；宿主改 Go 循环 |
| `Service/Takeover*.cs` | Windows | 无 | 丢弃 |
| `App/*` | Avalonia | 无 | 丢弃；用 LuCI/CLI |
| `python/src/ruijie_auth/private.py` | 标准库 | 高 | 参考实现；可作 PoC |
| `python/src/ruijie_auth/cli.py` | scapy | 高 | Linux 可运行；PoC 首选 |
| `python/src/ruijie_auth/storage.py` | DPAPI/JSON | 中 | 非 Windows 已回退 `plain:` + 0600 |
| `python/src/ruijie_auth/blocker.py` | Windows | 无 | 丢弃 |
| `python/src/ruijie_auth/system_service.py` | Windows | 无 | 丢弃；用 procd |
| `python/src/ruijie_auth/gui.py` | PySide6 | 无 | 丢弃 |
| `Core.Tests/PrivateTailTests.cs` | — | 高 | 移植为 Go/C 的 golden test |

## 4. 协议移植清单（必须逐字节保留）

1. 私有头：`00001311` + flag + IP + mask + gateway + DHCP + CRC16/XMODEM；
   21 字节整段做位反转 + 取反，输出 23 字节。
2. 前导：`00001311` + `8021x.exe` 补到 32 字节 + `06560000` + `01` +
   `00001311` + 2 字节属性总长。
3. 属性：`1A total_len 00001311 type len payload`；共 18 个属性，顺序不能变。
4. 0x2F 凭据：key = username；password 补零到 16 字节；IV 对
   Start/Logoff/Identity 为全零，对 MD5 为 challenge；
   `cipher = plain XOR MD5(key || previous_cipher/IV)`。
5. 0x39 服务名：GB18030，补到 32 字节。
6. 0x54 静态 ID：ASCII，补到 64 字节；实网观测值用 `<static-id>` 占位。
7. 0x4D 指纹：128 字节十六进制 ASCII；控制帧用全零 128 字节。
8. 0x17 nonce：32 字节十六进制 ASCII；Start/Logoff/Identity 共用一个
   会话 nonce，MD5 响应使用新 nonce。
9. EAPOL 头长度只覆盖标准 EAP 包；私有尾在 EAPOL length 之外。
10. Identity/MD5 的 EAPOL length 按 EAP 长度计算（示例账号为 14/31），
    尾巴不计入。
11. Start 可单播到锁定的认证服务器；Logoff 必须发
    `01:80:c2:00:00:03`。单播 Logoff 不会清除交换机会话，反而导致下一次
    Start 收到 EAP-Failure。
12. 每个请求连发 3 帧；EAP 响应前延迟 40 ms；重复请求按
    `(identifier, type, payload)` 去重。
13. 服务器消息：扫描 `00001311`，type=0，GB18030 解码，中文 + 可打印比例
    阈值。
14. EAP-Success 后保持会话；EAP-Failure 解析原因并退避。

## 5. OpenWrt 运行环境差异

### 5.1 网卡解析

- OpenWrt 的 `wan` 是 UCI 逻辑接口，不一定是 netdev；实际设备可能是
  `eth1`、`wan`、`br-wan`、`pppoe-wan`。
- 用 `ubus call network.interface.wan status` 取 `l3_device`/`device`，
  或让用户显式配置 `device`。
- 若 WAN 是 PPPoE，EAPOL 要发在底层 Ethernet 设备，不是 `pppoe-wan`。
- 若 WAN 在网桥里，`01:80:c2:00:00:03` 可能被 bridge 的 reserved
  multicast 规则影响；优先不要让 WAN 进网桥，必要时检查
  `group_fwd_mask`。

### 5.2 原始套接字

- Linux 用 `AF_PACKET + ETH_P_PAE (0x888E)`，root 权限；不需要 libpcap。
- 发送：`sendto` + `sockaddr_ll`；接收：`recvfrom`；绑定 ifindex。
- 3 帧连发在 Linux 上直接循环 `sendto` 即可，原 Windows 的
  `pcap_sendqueue` 不是必需。
- 接收 EAPOL 组播建议开启 promisc，或显式加入 PAE 组播组
  （`PACKET_MR_PROMISC` / `PACKET_MR_MULTICAST`）。原 C# 代码
  `pcap_open_live(..., promisc=1, ...)` 就是开启 promisc。

### 5.3 数据与配置路径

不能再使用 exe 目录（`/usr/sbin` 在 squashfs 上只读）。建议：

```
/etc/config/ruijie-auth              UCI 配置（含 config account，明文 0600）
/etc/ruijie-auth/servers.json        学习到的服务器 + locked
/etc/ruijie-auth/discovery.json      探测缓存
/var/run/ruijie-auth/status.json     运行状态
/var/run/ruijie-auth.sock            控制 socket（0600）
/var/log/ruijie-auth.log             日志（1MB 轮转）
```

现有 `ServiceConfig.ServiceRoot`、`UserStore.BaseDirectory`、
`FileLoggerProvider` 都假定 exe 目录可写，必须改。

### 5.4 服务与自启动

- `procd` init 脚本 + `respawn`；`/etc/init.d/ruijie-auth enable`。
- `/etc/hotplug.d/iface` 在 WAN up/down 时通知守护进程（SIGHUP 或 ubus）。
- 不要用 systemd unit；`ServiceInstaller` 的 Linux 分支在 OpenWrt 上无效。

### 5.5 密码保护

- DPAPI 在 Linux 不可用；现有 `machine-dpapi:` 账号无法在路由器解密。
- 账号池使用 UCI `config account`，密码明文保存，文件 0600。按当前部署
  要求不做 DPAPI/加密；路由器是单用户 root 环境，加密收益有限。
- 迁移时在路由器 LuCI 账号页重新录入，或从 Windows 导出明文再导入；
  不要把真实凭据写进仓库。

### 5.6 GUI 与管理面

- Avalonia/PySide6 不可用。
- 推荐：CLI + LuCI app；或守护进程自带一个仅 LAN 可访问的小型 HTTP API +
  静态页面。
- LuCI 后端可用 `rpcd` 脚本调用 CLI，避免额外端口和鉴权。

## 6. 管理功能移植矩阵

| 功能 | 现有实现 | OpenWrt 方案 | 工作量 | 备注 |
| --- | --- | --- | --- | --- |
| 账号池/启用停用/当前账号 | `UserStore`/`AccountStore` | UCI list 或 JSON | 低 | 密码 0600 |
| 多账号失败切换 | `AuthWorker.ResolveAccounts` | 守护进程循环 | 低 | 当前 → 其他启用 → 回退 |
| 认证启停/保活 | `Preempt` + `HoldAsync` | 常驻监听 + 定时重认证 | 中 | 不是简单移植 |
| 服务器学习 | `UpsertServer` | 监听 EAP 源 MAC | 低 | 保留 `servers.json` 字段 |
| 服务器锁定 | `locked_server` | 单播 Start | 低 | 保留 |
| 发现缓存 | `DiscoveryCache` | 缓存 device/MAC/server | 低 | `wan` 设备名可能变 |
| 服务安装/状态 | SCM/systemd | procd + ubus/CLI | 中 | 无 systemctl |
| 设置项 | `settings.json` | UCI/LuCI | 中 | 字段一一对应 |
| 日志轮转 | `FileLoggerProvider` | syslog 或 logrotate | 低 | 路径改 `/var/log` |
| 家庭网络暂停 | `AuthWorker.Pause` | 保留；WAN 绑定探测 | 低 | 重要 |
| Guardian 联动 | 回环 MCP | 删除；直接 TCP/HTTP 探测 | 低 | 路由器无法访问 PC 的 127.0.0.1 |
| 接管模式 | IFEO/服务/VBS | 删除 | — | 路由器上无原版客户端 |
| GUI | Avalonia | LuCI/CLI | 中–高 | 取决于 UI 深度 |
| 开机自启 | SCM/systemd | procd enable | 低 | |
| 定时重认证 | 无 | 新增 | 中 | 建议 2–6 小时可配 |
| NAT 伪装 | 无 | UA3F/UA2F + nftables/sysctl | 中 | 独立于认证，见 0.5/8 |

### 6.1 LuCI 设计（硬需求）

**页面与后端：**

| 页面 | 内容 | 后端 |
| --- | --- | --- |
| 概览 | 状态 badge、当前账号、服务器 MAC、WAN IP、最后成功、运行时长；认证/注销/暂停/恢复/重载按钮 | `ruijie-auth status --json`；按钮 `fs.exec` |
| 账号 | 账号列表：用户名、备注、启用、当前；增删改、设当前 | `ruijie-auth accounts ... --json` |
| 服务器 | 学习到的认证服务器：MAC、名称、成功次数、最后成功；锁定/解锁/删除 | `ruijie-auth servers ... --json` |
| 设置 | device、服务名、静态 ID、timeout/retries/repeats/start_timeout、重认证间隔、家庭网络暂停、伪装等级、连接检测 URL、日志级别 | `L.uci` 的 `ruijie-auth` 配置 |
| 日志 | 最近内容，自动刷新 | `fs.read('/var/log/ruijie-auth.log')` |

**配置分层：**

- `/etc/config/ruijie-auth`（UCI，0600）：认证参数、`config account` 账号池
  （明文）、伪装等级、日志级别。
- `/etc/ruijie-auth/servers.json`（daemon/CLI 管理）：学习到的服务器 + locked。
- `/etc/ruijie-auth/discovery.json`：探测缓存。
- `/var/run/ruijie-auth/status.json`：运行状态。
- `/var/run/ruijie-auth.sock`：控制 socket（0600）。
- `/var/log/ruijie-auth.log`：日志（1MB 轮转）。

UCI：

```uci
config auth 'main'
    option enabled '1'
    option device 'wan'
    option service_name '<service-name>'
    option static_id '<static-id>'
    option timeout '20'
    option retries '4'
    option repeats '3'
    option start_timeout '3'
    option reauth_interval '21600'
    option check_interval '2'
    option retry_interval '3'
    option autoguard '1'
    option preempt '1'
    option discovery_cache '1'
    option logoff_on_stop '0'
    option pause_on_home '1'
    option connectivity_url 'http://www.baidu.com/'
    option disguise 'basic'        # off | basic | full
    option log_level 'info'

config account 'a1'
    option username 'S12016004'
    option password '...'
    option note '主账号'
    option enabled '1'
    option active '1'
```

账号池最多 5 个；`active` 的账号优先，失败后按顺序尝试其他 `enabled`
账号。密码明文保存，文件 0600。

`servers.json`：

```json
{
  "version": 1,
  "servers": [
    {"mac": "00:1a:a9:17:ff:ff", "name": "", "note": "", "description": "wan",
     "first_seen": "...", "last_seen": "...", "last_success": "...",
     "success_count": 1, "locked": true}
  ]
}
```

CLI JSON 契约：

```text
ruijie-auth status --json
{
  "state": "authenticated",
  "message": "认证成功",
  "account": "S12016004",
  "interface": "wan",
  "server": "00:1a:a9:17:ff:ff",
  "ip": "<client-ip>",
  "last_success": "2026-11-18T20:57:09+08:00",
  "updated_at": "2026-11-18T21:00:00+08:00",
  "uptime": 12345
}

ruijie-auth servers list --json
[{"mac":"00:1a:a9:17:ff:ff","name":"","description":"wan","success_count":1,"last_success":"...","locked":true}]

ruijie-auth accounts list --json
[{"id":"a1","username":"S12016004","note":"主账号","enabled":true,"active":true}]

# 日志页直接 fs.read('/var/log/ruijie-auth.log')
ruijie-auth auth|logoff|pause|resume|reload
```

LuCI 文件：

```text
htdocs/luci-static/resources/view/ruijie-auth/overview.js
htdocs/luci-static/resources/view/ruijie-auth/accounts.js
htdocs/luci-static/resources/view/ruijie-auth/servers.js
htdocs/luci-static/resources/view/ruijie-auth/settings.js
htdocs/luci-static/resources/view/ruijie-auth/logs.js
root/usr/share/luci/menu.d/luci-app-ruijie-auth.json
root/usr/share/rpcd/acl.d/luci-app-ruijie-auth.json
root/etc/uci-defaults/90-ruijie-auth
```

菜单：

```json
{
  "admin/services/ruijie-auth": {
    "title": "Ruijie Auth",
    "order": 90,
    "action": { "type": "view", "path": "ruijie-auth/overview" },
    "depends": { "acl": ["luci-app-ruijie-auth"] }
  }
}
```

rpcd ACL：

```json
{
  "luci-app-ruijie-auth": {
    "description": "Ruijie Auth management",
    "read": {
      "file": {
        "/usr/bin/ruijie-auth": ["exec"],
        "/var/run/ruijie-auth/status.json": ["read"],
        "/var/log/ruijie-auth.log": ["read"]
      },
      "uci": ["ruijie-auth"]
    },
    "write": {
      "file": { "/usr/bin/ruijie-auth": ["exec"] },
      "uci": ["ruijie-auth"]
    }
  }
}
```

交互：

- 概览页 3 秒 `poll` 调 `status --json`；按钮 `fs.exec`。
- 账号页用 `fs.exec` 调 `accounts ...`；列表不返回密码，密码只在添加/修改
  时提交。
- 服务器页 `servers list --json`；锁定/解锁调 CLI，写 `servers.json`。
- 设置页用 `form.Map` + `L.uci`；保存后 `ruijie-auth reload`；伪装等级变化时
  `/etc/init.d/ruijie-disguise restart`。
- 日志页 `log --lines 200`，3 秒刷新；避免整文件读取。
- CLI 错误也返回 JSON（`{"error":"..."}`），LuCI 用 toast 显示。

为什么不用 ubus/HTTP：

- `fs.exec` + rpcd ACL 已经能完成状态/控制/日志；不需要 Go 侧
  libubus/CGO，也不需要额外端口和鉴权。
- 代价是轮询；3 秒一次对 R3G 负载可忽略。

伪装分层：

- `off`：不做伪装。
- `basic`：`ruijie-disguise` 应用 TTL 128、MSS clamp、关 IPv6、WAN drop、
  关 UPnP/NAT-PMP；MAC/DHCP 指纹在首次配置/镜像里写好。
- `full`：在 basic 之上启动 UA3F L3（NFQUEUE），按需开 IPID/TS/window。
- LuCI 设置页只写 UCI；`ruijie-disguise` 读取 UCI 并应用，避免认证 daemon
  耦合网络配置。

## 7. 必须实测的未知点（Go/No-Go）

`DHCP 在认证前可用` 已由用户确认，不再是阻塞项。剩余按优先级：

1. **校园侧实际启用哪些共享/NAT 检测信号**
   - 公开资料没有锐捷“防私接”的完整算法；常见组合是 TTL、IPID、
     TCP timestamp、DHCP 指纹、UA、主动探测。
   - 新增证据：Windows 移动热点长期共享未被踢，说明校园侧即使有检测也
     没有强制。优先级从“Go/No-Go”降为“上线后观察”。
   - 测试：先按 Tier 0 部署，单台 LAN 设备跑 24–72 小时，再逐步增加设备；
     观察是否被踢下线、重定向或自助服务页提示“多设备/代理”。
   - 同时抓 WAN 侧入站 SYN（主动探测）和 EAPOL（重认证），确认校园侧行为。

2. **R3G 上 UA3F NFQUEUE 的实际吞吐**
   - MIPS 32 不支持 UA3F eBPF，只能 NFQUEUE；MT7621 的性能必须实测。
   - 目标已明确为 100Mbps：`iperf3` 能跑满 100Mbps 且 CPU 可接受即可。
   - 测试：关闭 flow offload，开 UA3F L3 全量改写，`iperf3` 跑 LAN→WAN
     和 WAN→LAN；再只开 TTL 对比。
   - 因为校园侧检测很弱，默认 `disguise=basic`，UA3F 只在 `full` 时启用；
     这项测试不再阻塞认证功能。

3. **账号/MAC 绑定**
   - 测试：在 Linux 笔记本上把网卡 MAC 改成 WAN 的 MAC，或改成原 Windows
     USB 网卡 MAC，认证是否成功。
   - 如果绑定，OpenWrt WAN 配 `macaddr` 克隆；测试时先断开 Windows
     客户端，避免同 MAC 冲突。

4. **静态 ID 0x54 是否被服务器校验**
   - 已知值 `<static-id>`。先原样复用；再试随机值。
   - 如果服务器校验，保留原值即可（配置项已存在）。

5. **服务器是否周期性发起重认证**
   - 现状：`Supplicant.Authenticate()` 成功后就关闭 pcap，只靠 TCP 探测
     保活；如果交换机 2–12 小时发 EAP-Request/Identity，当前实现会漏掉。
   - 测试：在已认证会话上抓 `ether proto 0x888e` 2–4 小时。
   - 结论：路由器版本必须常驻监听 + 主动定时重认证；这是相对现有代码的
     结构性改动。

6. **DHCP 指纹与 Option 82 / school code**
   - DHCP 前置可用已确认，但 OpenWrt 默认 `udhcp` 的 option 55/60/61、
     hostname、vendor class 和 Windows 不同，可能本身就被识别为路由器。
   - 测试：抓 WAN DHCP 报文，和 Windows 的 DHCP 请求逐项对比。
   - Option 82 / school code 当前认证路径未使用；如果交换机要求，需要
     在 DHCP 客户端侧补，OpenWrt udhcpc 支持有限。

## 8. 推荐架构（原版 R3G）

```
/usr/sbin/ruijie-auth                 Go 静态二进制（协议 + 守护进程）
/etc/init.d/ruijie-auth               procd 脚本
/etc/hotplug.d/iface/90-ruijie-auth   WAN up/down 通知
/etc/config/ruijie-auth               UCI 配置（含 config account，明文 0600）
/etc/ruijie-auth/servers.json         学习到的服务器（含 locked）
/etc/ruijie-auth/discovery.json       探测缓存
/var/run/ruijie-auth.sock             控制 socket（0600）
/etc/init.d/ruijie-disguise           伪装层（TTL/MSS/IPv6/UA3F）
/var/run/ruijie-auth/status.json      状态
/var/log/ruijie-auth.log              日志（1MB 轮转）
/usr/bin/ua3f                       UA3F（mipsel_24kc，NFQUEUE）
/etc/ua3f/config.yaml               UA3F 配置
/etc/sysctl.d/90-ruijie.conf          TTL/timestamp/IPv6 等
/usr/share/luci/menu.d/luci-app-ruijie-auth.json
/usr/share/rpcd/acl.d/luci-app-ruijie-auth.json
/www/luci-static/resources/view/ruijie-auth/*.js
```

进程模型：

- `ruijie-auth`：单进程；收包 goroutine + 状态机。
- 常驻监听 EAPOL；WAN up + DHCP 拿到 IPv4 后触发 Start；定时重认证；
  失败退避；账号轮换。
- 状态机：`idle → discovering → identity → md5 → authenticated →
  holding → (re-auth | paused | retrying)`。
- 配置热加载：SIGHUP；不重启进程。
- 状态/日志：JSON 状态文件 + syslog；CLI 读取。

网络伪装层（独立于认证）：

- UA3F `server-mode: NFQUEUE`，`l3-rewrite.bpf-offload: false`（MIPS 32
  不支持 eBPF）。
- `l3-rewrite.ttl: true`，`ttl-value: 128`（模仿 Windows）。
- `l3-rewrite.ipid: true`、`tcpts: true`、`tcpwin: true` 按实测结果启用；
  不需要的关掉，减少 NFQUEUE 开销。
- `block-quic` 可选；只影响 UDP/443，HTTPS UA 仍需 MITM，通常不开。
- 关闭 flow offload：`/etc/config/firewall` 里
  `option flow_offloading '0'`、`option flow_offloading_hw '0'`。
- nftables：`tcp option maxseg size set 1460`；WAN 入站 drop 而不是 reject；
  关闭 UPnP/NAT-PMP。
- sysctl：`net.ipv4.ip_default_ttl=128`、`net.ipv4.tcp_timestamps=0`、
  `net.ipv4.tcp_ecn=0`、`net.ipv6.conf.all.disable_ipv6=1`。
- UA3F 主要处理 forward 链；路由器自身流量（DHCP、DNS、NTP、LuCI）不走
  UA3F，需用 `ip_default_ttl`、关闭 IPv6、限制 WAN 服务单独处理。
- DHCP：`option vendorid 'MSFT 5.0'`、Windows 的 option 55 PRL、
  `option hostname 'DESKTOP-XXXX'`；克隆原 Windows 网卡 MAC。
- 如果只检测 TTL/UA，用 `ua2f`（267KB）替代 UA3F，减少 CPU/空间。

CLI：

```text
ruijie-auth status [--json]
ruijie-auth auth|logoff|pause|resume|reload
ruijie-auth accounts list|add|update|remove|use [--json]
ruijie-auth servers list|lock|unlock|remove [--json]
ruijie-auth discovery show|clear
ruijie-auth config show
```

LuCI（必需）：概览/账号/服务器/设置/日志五页；后端 `rpcd` + `fs.exec`
调 CLI，非敏感设置用 `L.uci`。详细契约见 6.1。

构建：

```sh
# 认证 daemon（开发机交叉编译）
GOOS=linux GOARCH=mipsle GOMIPS=softfloat CGO_ENABLED=0 \
  go build -trimpath -ldflags "-s -w" -o ruijie-auth ./cmd/ruijie-auth

# 固件（Image Builder，不是 full buildroot）
make image PROFILE=xiaomi_mi-router-3g \
  PACKAGES="luci-ssl ua3f kmod-nft-queue kmod-nft-tproxy nftables curl" \
  FILES=files/
```

语言选择：

| 选项 | 体积 | 开发速度 | GB18030 | 适用 |
| --- | --- | --- | --- | --- |
| Go | 约 7–10 MB 静态 | 快 | `x/text` 原生 | 原版 R3G 推荐 |
| C | 约 100–300 KB | 中 | 需 `libiconv-full` | 16MB SPI 版 |
| Rust | 约 3–6 MB | 中 | `encoding_rs` | 可选 |
| Python | 约 20–40 MB + scapy | 最快 PoC | 原生 | 仅 PoC |
| .NET | 无 MIPS32 运行时 | — | — | R3G 排除 |

## 9. 工作量估算

| 阶段 | 内容 | 人日 |
| --- | --- | --- |
| Phase 0 | R3G 板型确认 + 认证 + 检测基线（MAC、static ID、重认证、DHCP 指纹） | 1–2 |
| Phase 1 | Go 协议核心 + 569 字节 golden test + 组帧 | 2–3 |
| Phase 2 | 常驻守护进程（状态机、重认证、账号切换、发现/服务器、暂停） | 3–5 |
| Phase 3 | OpenWrt 集成（procd、hotplug、UCI、Image Builder） | 1–2 |
| Phase 4 | 网络伪装集成（UA3F/nftables/DHCP/sysctl）+ 吞吐/稳定性测试 | 2–4 |
| Phase 5 | CLI（status/auth/accounts/servers/log） | 1–2 |
| Phase 6 | LuCI 五页 + rpcd ACL + UCI | 3–5 |
| 合计 | daemon + CLI + LuCI + 基础伪装 | 13–23 人日 |

MVP（认证 + basic 伪装 + CLI + LuCI 概览/设置/账号）：8–12 人日。
没有真机时，Phase 0 的上机部分延后；先用单元测试 + veth 假认证器 +
OpenWrt x86/QEMU 验证 LuCI。网络伪装调优是开放式的，取决于校园侧实际
检测面。

## 10. 风险清单

| 风险 | 等级 | 说明 | 缓解 |
| --- | --- | --- | --- |
| MIPS 32 无 eBPF → NFQUEUE 吞吐 | 低中 | 100Mbps 目标下 MT7621 大概率够；UA3F full 才有明显开销 | `disguise=basic` 默认；full 时 `iperf3` 实测 |
| 16MB SPI 版空间不足 | 中（仅 v2/4A） | 用户看是原版 R3G，但未上机确认 | 到手先 `ubus call system board`；v2 走 C + UA2F |
| LuCI 后端/ACL | 中 | `fs.exec` 参数、轮询、ACL、密码不泄露 | 先做 6.1 契约；x86/QEMU 验证；密码不进日志 |
| 无真机验证 | 中 | DSA EAPOL、DHCP 时序、MAC 克隆只能上机测 | 单元 + veth 假认证器 + QEMU LuCI；上机补测 |
| 检测面未知/残余指纹 | 低中 | Windows 热点未踢，说明校园侧检测很弱 | Tier 0 默认；24–72h 观察；UA3F 可选 |
| flow offload 与 TTL 冲突 | 低中 | 只有 `disguise=full` 需要精确 TTL | 关 offload；100Mbps 下吞吐足够 |
| DHCP 参数指纹 | 中 | OpenWrt 默认 `udhcp`/`OpenWrt` hostname | 配成 Windows：vendor/PRL/hostname |
| 账号/MAC 绑定 | 中 | 换设备被拒 | 克隆 WAN MAC；先 logoff 原设备 |
| 周期性重认证 | 中 | 现有一次性设计会漏 | 常驻监听 + 定时重认证 |
| 静态 ID 校验 | 低中 | 服务器可能校验 0x54 | 先复用 `<static-id>`，再试随机 |
| Option 82/school code | 低 | 当前未实现；纯 DHCP 环境未必需要 | 上机抓 DHCP 报文确认 |
| Go mipsle 非 first-class | 低中 | 需实测二进制 | softfloat；交叉编译 + 设备 smoke test |
| 配置文件路径 | 低 | exe 目录只读 | 改 `/etc` + `/var` |
| 密码迁移 | 低 | DPAPI 不能跨平台 | 路由器重新录入 |
| 桥接/DSA 过滤 | 低中 | EAPOL 可能被 bridge 过滤 | 不桥接 WAN；上机检查 `group_fwd_mask` |
| .NET 路径 | 高 | 无 MIPS32 运行时 | 排除 |

## 11. 建议下一步

1. 无真机阶段先把代码骨架搭起来：
   - Go module + 协议核心（PrivateTail/Eapol/Supplicant）+ 569 字节 golden
     test；当前这台机器没有 `go`，先装用户级 Go 或用 WSL/Docker。
   - LuCI app 骨架 + 6.1 的 UCI/ACL/CLI JSON 契约。
   - veth 假认证器：Start → Identity → MD5 → Success，验证状态机/去重/超时/
     账号切换；不依赖 R3G。
   - OpenWrt x86/QEMU 里装 LuCI app，验证菜单/权限/页面/轮询。

2. 真机到手后的 Phase 0：
   - `ubus call system board` + `cat /proc/mtd` 确认原版 vs v2；原版走
     Go + UA3F，v2 走 C + UA2F。
   - 刷 24.10，确认 `wan` 上 AF_PACKET 能收 EAPOL；跑真实认证。
   - 抓 2–4 小时 `ether proto 0x888e`，确认服务器是否主动重认证。
   - 测 MAC 克隆、static ID 随机值、DHCP 报文对比。
   - `iperf3` 测 100Mbps 下 basic/full 的吞吐。

3. 伪装基线：
   - 默认 `disguise=basic`：TTL 128、MSS clamp、IPv6 关闭、WAN drop、
     UPnP 关闭、DHCP 指纹/MAC 克隆。
   - 校园侧有异常再切 `full`（UA3F L3）；100Mbps 下性能足够。
   - 单台 LAN 设备跑 24–72 小时，再逐步加设备。

4. LuCI 是 MVP 的一部分，不是后置项；先做概览/账号/设置，服务器/日志随后。

5. 不建议在 R3G 上尝试 .NET/Avalonia；MIPS 32 没有运行时。Python + scapy
   只用于 PoC。

## 12. 需要确认的前提

- 真机何时到手？板型要等 `ubus call system board` 确认；当前按原版 R3G
  （256MB/128MB NAND）设计。
- 这台 Windows 有没有 WSL2 或 Docker？Image Builder、OpenWrt x86/QEMU、
  Go 交叉编译在 Linux 环境里最顺。
- 是否接受“先写代码 + 单元/veth/QEMU 验证，真机到了再做 live 认证”的节奏？
- 校园侧除了“没被踢”，有没有自助服务页的在线设备数/代理提示？没有也不
  阻塞，Tier 0 先上。

## 13. 资料来源

- OpenWrt R3G 设备页：https://openwrt.org/toh/xiaomi/mir3g
  （MT7621、256MB RAM、128MB NAND、R3Gv2/4A 区别、刷机路径）
- OpenWrt 24.10 mt7621 profiles：
  https://downloads.openwrt.org/releases/24.10.0/targets/ramips/mt7621/profiles.json
  （`xiaomi_mi-router-3g`、`xiaomi_mi-router-3g-v2`、
  `xiaomi_mi-router-4a-gigabit`，`mipsel_24kc`，kernel 6.6.73）
- OpenWrt 24.10 R3G 镜像：
  https://downloads.openwrt.org/releases/24.10.0/targets/ramips/mt7621/
  （sysupgrade 7,312,008 B；rootfs0 5,242,880 B）
- UA3F：https://github.com/SunBK201/UA3F
  - L3 总览：`docs/en/l3/overview.md`
  - TTL：`docs/en/l3/ttl.md`（offload 下 TTL 254 的限制）
  - IPID：`docs/en/l3/ipid.md`（置 0）
  - TCP timestamp：`docs/en/l3/tcp-timestamp.md`（删除）
  - TCP initial window：`docs/en/l3/tcp-initial-window.md`（65535）
  - eBPF：`docs/en/ebpf/l3-rewrite.md`（MIPS 32 不支持）
  - 配置：`docs/en/guide/configuration.md`
- UA3F v3.7.0 release：
  https://github.com/SunBK201/UA3F/releases/tag/v3.7.0
  （`ua3f_3.7.0-r1_mipsel_24kc.ipk` 5.43MiB；依赖
  `kmod-nft-queue`、`kmod-nft-tproxy`）
- UA2F：https://github.com/Zxilly/UA2F
  （`ua2f_4.10.2-r1_mipsel_24kc-24.10.0.ipk` 约 267KB；UA-only）
- Go 端口列表：https://go.dev/doc/install/source
  （`linux/mipsle`；`GOMIPS=softfloat`）
- 小米 R3G 规格：https://www.mi.com/miwifi3g/specs
  （MT7621A、256MB DDR3、128MB SLC NAND、1WAN+2LAN GbE、USB 3.0）
- LuCI JS 开发：https://openwrt.org/docs/guide-developer/toolchain/luci_js
- rpcd ACL：https://openwrt.org/docs/guide-developer/rpcd
- procd init：https://openwrt.org/docs/guide-developer/procd-init-scripts

说明：锐捷“防私接”的完整检测算法没有公开文档；第 0.5 节是按常见校园网
检测面和 UA2F/UA3F 的对抗能力整理的，必须用现场测试确认。Windows 热点
未被踢是弱检测的现场证据，但不等于校园侧永远不启用检测。
