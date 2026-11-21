# Agent Context

> 跨会话交接文档。描述项目当前状态、架构、验证结果与下一步，不记录完整对话历史。
> 最后更新：2026-11-20

## 0. 仓库位置

- 源码仓库：`D:\Files\Develop\Cross-Platform\RuijieCampusAuth`
- 迁移前的旧仓库路径已废弃，文档与工具中的旧路径引用已同步更新。
- 部署目录仍为 `D:\Storageredirect\Program\RuijieCampusAuth`；源码迁移不影响
  已安装的 Windows 服务。

## 1. 项目是什么

通用的锐捷校园网第三方认证客户端（“锐捷认证器 / 锐捷校园网开放认证器”）。
使用官方协议做兼容认证，不依赖官方进程；学校差异通过 `profiles/*.json` 导入。
软件本体不写死学校参数；仓库附带一份已核对的正式档案。

三套实现：

- **根目录 C# / .NET 10**：Core + CLI + Windows 服务/Linux systemd + Avalonia GUI。
- **`python/`**：完整协议转译 + 数字菜单 CLI，无 GUI；协议模块与 C# 一一
  对应，`pytest` 60 项通过。
- **`openwrt/`**：Go 守护进程 + LuCI（2026-11-20 完成移植，见
  `docs/OPENWRT_PORT.md`）。

Python 通用化仍延期；OpenWrt 已按 `docs/GENERICIZATION.md` 完成改名、
档案化与零 kmod 改造。

## 2. 仓库结构

```
RuijieCampusAuth.Core/       协议、私有尾、EAPOL、PEAP/TLS、配置、服务安装器
  Protocols/                 PrivateProperties, Rc4, MschapV2, EapTlsStream,
                             PeapSession, PcapEapTransport, DhcpControl,
                             PortalClient, HttpJsonClient, ClientCenterClient,
                             WlanProfileManager, SamUpgradeClients, PeapVendorSegments
  Diagnostics/               OuiDetect, NicInventory, ArpMonitor, NetworkProbe,
                             ProxyDetect, DoctorPipe
  Debug/                     DebugSettings, IDebugFeature, registry, audit,
                             gateway/pipe client, features
  SchoolProfile.cs           profile 模型、导入
  Supplicant.cs              EAP-MD5 状态机
  Eapol.cs                   帧与服务器消息
  PrivateTail.cs             私有尾、CRC、混淆、凭据加密
  UserStore.cs               accounts/settings/discovery/servers
  ServiceConfig.cs           service.json
  ServiceInstaller.cs        服务安装、版本切换、旧服务迁移
  TakeoverPolicy.cs          IFEO 接管/恢复
RuijieCampusAuth.Service/    AuthWorker, Guardian, Takeover, DebugPipeServer
RuijieCampusAuth.App/        Avalonia GUI：账号/认证/服务器/服务/设置/档案/调试
RuijieCampusAuth.Cli/        auth/config/service/debug/profile
RuijieCampusAuth.Core.Tests/ 65 tests
RuijieCampusAuth.App.Tests/  34 tests
RuijieCampusAuth.Service.Tests/ 管道集成测试（需先停止运行中的服务）
profiles/                    学校档案（每校一份 JSON）
tools/re_diff/               Unicorn 原版函数调用器
docs/                        上下文、逆向、适配、协议档案
openwrt/                     锐捷认证器 OpenWrt 版：ruijie-auth（Go）+ luci-app-ruijie-auth
python/                      完整协议转译 + 菜单 CLI（60 项测试，无 GUI）
```

## 3. 构建 / 测试

```bash
dotnet build RuijieCampusAuth.slnx -c Release
dotnet test RuijieCampusAuth.Core.Tests -c Release
dotnet test RuijieCampusAuth.Service.Tests -c Release
```

- SDK：.NET 10.0.302。
- 发布：`build-csharp.ps1 -Runtime win-x64`（framework-dependent）。
- 测试：Core 65 通过（含原版二进制差分向量）；App 34 通过；Python 60 通过。
- Service 管道测试会占用全局命名管道 `RuijieCampusAuth-Debug`。本机有
  运行中的 `RuijieCampusAuth.Service` 时，测试客户端会连到真实服务，
  `DebugPipeTests` 可能误报；跑该测试前先 `Stop-Service RuijieCampusAuth`。

## 4. 部署与数据目录

当前安装目录：`D:\Storageredirect\Program\RuijieCampusAuth`。

| 文件 | 用途 |
|---|---|
| `accounts.json` | 账号池（DPAPI） |
| `settings.json` | GUI/服务共用设置、profile、debug |
| `service.json` | 服务配置（LocalMachine DPAPI） |
| `discovery.json` | 网卡/服务器发现缓存 |
| `servers.json` | 学习到的认证服务器 |
| `service_status.json` | 服务状态 |
| `takeover_state.json` / `takeover.ini` | 接管状态/心跳 |
| `logs/service.log`、`logs/csharp-gui.log`、`logs/debug.log` | 日志 |

Windows 服务：

- 服务名：`RuijieCampusAuth`（显示名 `Ruijie Auth`）。
- 已部署服务与进程位于 `D:\Storageredirect\Program\RuijieCampusAuth`。
- LocalSystem，自动启动，`depend=npcap`，SCM 恢复 5s/10s/30s。
- Linux unit：`ruijie-campus-auth.service`。

Deep Freeze：

- 程序与数据放 D:；环境变量放用户级。
- 不要在冻结的 C: 上部署二进制/配置；C: 只保留冻结基线内容。
- C: 冻结基线：`C:\Program Files\restart_ruijie.vbs`（SHA256
  `D1833A456CAB97FA5EE2F652A92852AC8E5E392ED668B8A93AD6BFAEE77623EB`）、
  登录任务 `RunHXXYNet`、服务注册 `RuijieCampusAuth`。
- 服务镜像固定为
  `"d:\storageredirect\program\ruijiecampusauth\RuijieCampusAuth.Service.exe"`，
  `AUTO_START`/`LocalSystem`/`depend=npcap`，恢复 5s/10s/30s。
- 后续版本只能替换 D: 程序文件；不得改服务名、服务 exe 名、GUI exe 名、
  部署路径、`--tray --auto` 或 `--restore-client` 入口。VBS 同时识别
  `RuijieTakeover` 与旧 `HXXYTakeover`。

## 5. 配置档案（多学校）

- 目录：程序目录 `profiles/` 或当前目录 `profiles/`。
- CLI：`profile list|show|import [--root <安装目录>]`。
- GUI：`档案` 页选择后“导入/切换”。
- 导入只覆盖档案声明字段；账号、网卡、接管、Guardian 不变；自动备份
  `settings.json.bak-<时间戳>`。
- `profiles/` 可放多份 `<学校>.json`，导入只覆盖档案声明字段。
- 已入库档案：`厦门华厦学院.json`（实网核对）+ `厦门工学院.json` /
  `厦门理工学院.json` / `华南农业大学.json`（2026-11-20 从安装包逆向生成，
  method 与未还原项见 `docs/REVERSE_ENGINEERING.md` §3.1；三校 static_id、
  service_name 均需实网 0x54/0x39 属性确认）。注意：SCAU 安装包原文件名
  误标"闽南农业大学"，档案以客户端 OEM 标识华南农业大学（hnny）为准。
- OpenWrt 端用同一份档案：`ruijie-auth profile import --file "<学校>.json"`
  写入 `/etc/config/ruijie-auth`，或 LuCI「学校档案」页上传/粘贴。

档案字段与适配流程见 `profiles/README.md`、`docs/ADAPTATION.md`。

## 6. 功能状态

| 能力 | 状态 |
|---|---|
| EAP-MD5 有线认证 | 已实现并线上验证 |
| 私有尾/CRC/混淆/凭据加密 | 已实现，原版函数差分一致 |
| 服务器私有属性 TLV/RC4 | 已实现；SU_REAUTH_INTERV 驱动重认证 |
| DHCP（Windows API） | 已实现，status/renew/release/release-renew |
| 诊断（OUI/NIC/ARP/探测/代理/拨号/医生管道） | 已实现 |
| Portal eportal | 已实现，待现场 |
| 客户端中心/自助/服务列表/用户消息/Portal UDP | 已实现 |
| PEAP/MSCHAPv2/EAP-TLS | 已实现，待真实 RADIUS |
| 无线 WPA2-Enterprise | 已实现，待真实 AP |
| SAM | UDP/DES 探针，报文布局待抓包 |
| 升级 | GET 检查；下载/安装未实现 |
| 多档案 GUI/CLI 切换 | 已实现 |
| OpenWrt EAP-MD5 + 私有尾 | 已实现，go test + qemu selftest |
| OpenWrt 服务器私有属性 + RC4 | 已实现，真实抓包向量测试 |
| OpenWrt PEAP / EAP-TLS | 已实现，待真实 RADIUS |
| OpenWrt 学校档案导入 | 已实现（CLI + LuCI 上传） |
| OpenWrt LuCI 8 页 + zh-cn | 已实现，ipk 含 .lmo |
| password-change / config-update | 计划态（端点/字段无证据） |
| 小锐 WiFi 分享 | 计划态（驱动级） |

每个功能都有 `debug` 功能名，GUI「调试」页和 CLI `debug` 命令共用。

## 7. 已验证状态（2026-11-19 全量校对轮）

- **核心算法差分（Unicorn 直调原版二进制，全新随机向量）**：CRC16、
  字节混淆、私有头构建、MD5、凭据加密 —— 20/21 一致；唯一"不一致"是
  47 字节向量（原版要求 len%16==0 否则拒绝执行，C# 内部填充，调用契约
  差异而非算法差异）。
- **完整 569 字节私有尾**：本机真实抓包（live-auth.pcap，服务器接受并
  认证成功）的 Identity 与 MD5 Response 两份尾包，用 C# `BuildTail`
  以捕获的 nonce/fingerprint/MAC/IP/静态 ID/GB10 服务名输入重建，
  **569/569 字节逐字节一致**（两份）。`build/capture/tail-compare.json`。
- **服务器私有属性解析器重写**：原格式是分段结构（GBK 消息段 + 0x6F
  固定块 + 两个版本门 + 15 字节解混淆段 + `[1311][type][len=value+2]`
  TLV 循环），不是平铺 TLV。C# 重写后与原版 `ParsePrivateProperty`
  (0x537ee0) 在真实抓包字节上逐字段一致（消息、hash 0x19594C7A、
  interval 20000ms、全部 10 个 TLV、RC4 key/IV）。
  `build/capture/original-parse-truth.json`。
- **实测互证**：ENCRYPT_KEY=7C73533126403871 / ENCRYPT_IV=4163332331215164
  与 dircomm.log 的 SAM DES key/IV 一致（SAM 复用认证下发的 key/IV）；
  MSG_CLIENT_PORT=138、INDICATE_SERV_IP=<server-ip> 与 dircomm 一致。
- **GetDHCPAuthPhase 差分**：phase=3+首次+DHCP 激活 → `63 01 03`
  len 3，与 C# `DhcpPhase(3)` 一致；非首次 → len 0。
- **GetServiceName 差分**：空配置 → len 0（无段），与原版一致。
- 测试：Core 46/46（含真实抓包 554 字节锁定向量）、Service 1/1。

### OpenWrt 端口（2026-11-20）

- `go test ./...` 全通过（protocol/config/profile/supplicant/servers/network）；
  `go vet` 干净；`gofmt -l` 无输出。
- 私有尾 569 字节、真实抓包私有属性块（10 属性 / hash 0x19594C7A /
  interval 20000 / ENCRYPT_KEY+IV）、RC4、MD4、MSCHAPv2 RFC 向量、
  PEAP 分片布局全部有 Go 测试。
- **2026-11-20 认证链路复核**：用 `live-auth.pcap` 的三份真实尾包
  （EAPOL-Start、Identity、MD5 Response）做 Go `BuildTail` 逐字节向量，
  三份均 569/569 一致；Start 用会话 nonce + 全零指纹，Identity 用会话
  nonce + 随机指纹，MD5 用随机 nonce/指纹 + challenge 作 IV。
- 复核中修复：服务器应答后仍补发 EAPOL-Start；常驻会话重认证时
  `handled` 去重表未清空（成功和主动重认证两处）；服务器主动重认证
  缺少超时兜底（现已重置认证定时器，失联会返回超时）；TLS 桥接关闭与
  并发读写的 channel 竞争；2 字节私有属性按 signed 解码（C# 为
  unsigned）；PEAP TLS 版本窗口收窄到 1.0-1.2（排除 PEAP 不支持的 1.3）。
- amd64 原生与 mipsle（`qemu-mipsel`）`ruijie-auth selftest` 通过。
- ipk 重新构建：`ruijie-auth_0.2.0_mipsel_24kc.ipk` 2,299,694 字节，
  SHA256 `377e8d4e07470e11df84822aa828a3c729460ffc98ed928d8f283319a96a3fe1`。
- **2026-11-21 插件重打**：源码 main `9be8b58`，产物
  `ruijie-auth_0.9.0_mipsel_24kc.ipk` 2,299,758 字节，SHA256
  `8da2e94b34b345f5ff429565b1285304e50f5d003cd0ecc3de9898529e13b3d1`；
  8 个 LuCI 页面与源码 LF blob 逐一一致，zh-cn lmo 未变。已覆盖
  `dist/openwrt/` 与 R3G 工具包。
- **2026-11-21 最终整机固件**：源码 main `8f3d877`，sysupgrade
  14,808,186 字节，SHA256
  `75f3e71a520ad448006b3149b9907883d0bbdb42a5c1120182030bcbbb19f02d`；
  Breed 21,364,736 字节，SHA256
  `4d8a44d5cf998429288fd5e8105991f01a3174832ff03ea8ae02190734543160`；
  kernel1 未变 `f1718d09...`，rootfs0 12,976,128 字节 `778ef9b9...`。
  ipk 2,742,009 字节 `3a95271c...`（log/probe/full/logs2）。
  manifest 含 `luci-app-vantage`、`luci-i18n-vantage-zh-cn`、
  `luci-theme-vantage`、`https-dns-proxy`、`luci-app-https-dns-proxy`、
  `luci-i18n-https-dns-proxy-zh-cn`、防检测 kmod、iptables 模块、ipset、
  luci-compat；UA3F 3.7.0 通过 FILES 内置（`/usr/bin/ua3f`），
  `ruijie-disguise` 默认 `full`，UA3F S98 → disguise S99。DoH 使用自定义
  AdGuardHome 端点；外网探测为 TCP 53 四目标 + HTTP 204 回退。
- （历史，2026-11-20 构建）R3G 整机镜像：9,155,706 字节（8.7MiB），SHA256
  `91f3520ac8837f3a40530813de0c488e00c0edecb94e96efe26f97954d0e1e5f`；
  rootfs 含 `ruijie-auth` 包、8 个 LuCI 视图、`ruijie-auth.zh-cn.lmo`，
  已在 squashfs 内逐项核对。当前产物以本文件上方的最终构建为准。
- 产物整理到 `dist/openwrt/`（gitignored）：ipk、sysupgrade、Breed
  镜像、kernel1/rootfs0、manifest、mipsle/amd64 daemon、zh-cn lmo、
  `SHA256SUMS.txt`、`BUILD_INFO.txt`。刷机包
  `D:\Files\Develop\OpenWrt\MiR3G_V1-Openwrt` 已同步新镜像/新 ipk，
  删除旧 `ruijie-auth` ipk，并更新 checksums、README、刷写教程、自启示例
  和智能检测脚本（移除 DoH 守护）。
- `scripts/po2lmo.py` 生成的 `.lmo` 与 24.10 官方
  `https-dns-proxy.zh-cn.lmo` 逐键值一致（154/154）。
- **中文 i18n 100%**：185 个 JS `_()` 词条全部有译文（0 缺失、0 空、
  0 fuzzy）；`.po` 187 条编译为 182 条 LMO（5 条与 msgid 相同的协议名
  按 LuCI 规则省略）；x86_64 QEMU 运行时目录按 hash 逐条验证 8 个菜单
  标题和 8 个页面全部命中，含账号池/WAN 多选/自检三条长说明。CLI 的
  `selftest`、`status`、`interfaces`、`accounts` 与日志级别标签也已中文化。
- x86_64 OpenWrt QEMU 冒烟：`selftest` 通过；`interfaces` 将 br-lan 标为
  WAN；daemon 在 br-lan 上完成 Logoff→4×Start→paused；QEMU 演示档案
  `profile import` 写入 8 字段并生成 backup，reload 后状态显示 profile；
  LuCI 页面 200、`lang_zh-cn`、translations 端点全量中文命中。
- 仓库附带的正式档案已用 C# CLI 与 OpenWrt Go CLI 分别 `profile show`
  验证，并用 Go CLI 导入临时 UCI 配置，应用 13 个字段。

### 真实校园网端到端（2026-11-20）

- **Ubuntu 桥接**：VMware 改为桥接后，Ubuntu `ens33`（`172.18.28.21/17`）
  作为真实 802.1X 客户端。Go 守护进程与最终 C# 0.9.1 tar 包分别完成
  EAP-MD5 认证，服务器 `00:1a:a9:17:ff:ff`，收到私有属性
  `INDICATE_PORT=53`、`INDICATE_SERV_IP=2887125761`、
  `MSG_CLIENT_PORT=138` 与欢迎消息；C# CLI 输出 `认证成功` /
  `RESULT SUCCESS`。
- **OpenWrt 桥接**：x86_64 QEMU 使用 `macvtap0`（挂在 `ens33`）获得
  `172.18.28.23/17`，`ruijie-auth 0.9.0` 在 `br-lan` 上完成真实
  EAP-MD5 认证；LuCI 登录 200，概览/账号/档案/设置四页均渲染，
  运行时中文目录 79,255 字节且包含“锐捷认证器”。
- **C# Linux 崩溃修复**：`struct pcap_pkthdr` 的 `timeval` 在 64 位
  Unix 为 16 字节，旧代码按 Windows 8 字节布局读取 `caplen`，导致
  `pcap-reader` 越界复制并崩溃。现按平台取偏移（Windows 8 / 64 位
  Unix 16 / 32 位 Unix 8），提交 `d45e671`。
- **账号单会话限制**：同一账号在 OpenWrt、Ubuntu、Windows 多端同时
  认证时，后认证端会顶掉先认证端；表现为“服务器未返回可读原因”。
  测试必须先在旧客户端执行 Logoff，或等待会话释放。
- **发布**：`v0.9.0` 已发布；因 Linux 包早于上述修复，补发
  `v0.9.1`（Windows/Linux 重新构建，OpenWrt ipk 代码未变，继续使用
  0.9.0 包）。`v0.9.1` 已设为 Latest，5 个资产全部上传，GitHub 记录的
  SHA256 与本地 `SHA256SUMS.txt` 一致；Ubuntu 应用菜单中的已安装版本
  已同步为 0.9.1。
- 仍未验证：真实 RADIUS 上的 PEAP/EAP-TLS。

### 2026-11-20 重启后网络管家误报离线

- 重启后 WLAN（`ZhangAndroid`）先取得默认路由：WLAN metric 10，校园网卡
  `NG-TMP-0000` metric 60。NetworkGuardian 的 Npcap 原始探测实际沿默认
  路由发包，导致源地址为 `172.18.28.6` 的探测从 WLAN 发出、收不到回应，
  校园网卡被判离线；服务因此每约 15 秒 DHCP + Logoff + 重认证。
- 用四个探测目标（223.5.5.5、119.29.29.29、180.76.76.76、
  114.114.114.114）的临时 `/32` 路由走校园网卡后，NetworkGuardian 立即
  恢复 `InternetVerified online 1/1`（TCP 1/4、ICMP 2/2），随后稳定为
  校园网卡 metric 10、WLAN 12；临时路由已删除。
- 结论：NetworkGuardian 配置未变；触发因素是默认出口/开机顺序，而不是
  认证服务或 VBS 注册。服务侧已加兜底：Guardian 报离线达到阈值时，用
  认证网卡源 IP 对 `connectivity_url` 做 TCP 复核（80/443），成功则保持
  会话，失败才重认证（提交 `8aad631`）。

### R3G 真机核验与备份（2026-11-21）

- 设备：小米路由器 3G 原版（R3G V1），MiWiFi 2.25.124，Linux 3.10.14，
  MT7621A / 253124 KiB RAM / ESMT 128MiB NAND（2048B page、64B OOB、
  HW ECC 4-bit），U-Boot 4.2.S.1，xHCI USB3；确为 V1，不是 4A/R3Gv2。
- 原厂分区编号与 OpenWrt 不同：`mtd0` 是 `ALL` 主设备，`mtd1` 才是
  Bootloader，`mtd4` 才是 factory；`mtd10/11/12` = rootfs0/rootfs1/
  overlay，总边界仍是 `0xA00000 + 0x7580000`。工具包的备份说明、刷写
  教程与 README 已按实测修正。
- factory MAC 实测：`0xE000` = LAN（gmac0），`0xE006` = WAN（gmac1）；
  旧文档标签写反，已修正。
- 兼容性：sysupgrade CONTROL 的 `BOARD=xiaomi_mi-router-3g` 与设备一致；
  Breed 镜像绝对偏移 `0x200000/0x600000/0xA00000` 与原厂 kernel0/
  kernel1/rootfs0 对齐；工具包 Breed 文件 SHA256 与预期一致
  （`05ebb31f...`），设备 mtd1 仍是原厂 bootloader。
- 备份：`D:\Files\Develop\OpenWrt\MiR3G_V1-Openwrt\Backup`，589MB；
  23 个分区 dump + OOB + 17 个文本快照 + Breed 全片 raw
  （`MiR3G_V1_编程器固件模板_raw.bin`，138,412,032 字节 = 128MB 数据
  + 4MB OOB，SHA256 `3c4532d4...`），43 项 SHA256 全通过。
  raw 布局为 4×[512 数据 + 16 OOB]；拆 OOB 后 kernel0/1、rootfs0、
  overlay、config..reserved0 与刷 Breed 前逐字节一致，rootfs1 仅 2 个
  单比特差异；OOB spare[0] 全 0xFF，无坏块标记。
- 风险：备份时 BaiduNetdisk 在运行并在目录内生成了
  `backup.log.baiduyun.uploading.cfg`，需确认该目录不在同步范围并检查
  云端副本；factory/MAC 不应上传。
- 2026-11-21 已把 Breed 写入原厂 Bootloader 分区（mtd1）：
  `mtd write /tmp/MiR3G_V1_Breed.bin Bootloader` 返回 0，回读前 137250
  字节 MD5 `e65d388129a6d1ac39abf99329f1978b` 与源文件一致，剩余部分
  全 0xFF，factory 分区 MD5 未变；随后已进入 Breed（192.168.1.1），
  并完成上条所述全片 raw 备份。
- **2026-11-21 刷入 OpenWrt**：Breed 1.2 的“小米 R3G 原厂 → 固件”
  不接受合并的 15MB Breed 镜像（预处理失败）；实测有效的路径是
  “常规固件 → 闪存布局 小米 R3G OpenWrt”，分别上传 kernel1 与
  rootfs0。落点经刷后 raw 备份逐区确认：kernel1 → `0x600000`，
  rootfs0 → `0xA00000`，kernel0（`0x200000`）保留原厂，bootloader /
  factory / overlay 未动。工具包已补入 kernel1/rootfs0 两个分区文件。
- Breed 环境变量 `autoboot.command = boot flash 0x600000` 已写入，
  41 个原字段（MAC/SN/WiFi 等）全部保留。
- 重启后 OpenWrt 24.10.0 正常启动：board `xiaomi,mi-router-3g`，
  kernel 6.6.73，rootfs squashfs。`ruijie-auth 0.9.0` 守护进程运行，
  设备端 `selftest` 全通过；8 个 LuCI 页面 HTTP 200 且哈希与源码一致；
  中文目录含“锐捷认证器”。刷入时尚未配置账号，WAN 未插线。
- MAC 映射注意：原厂 0xE000=LAN、0xE006=WAN；OpenWrt 24.10 相反
  （wan=0xE000 的 ...33，br-lan=0xE006 的 ...34）。
- 2026-11-21 LAN 已改为 `10.0.0.1/24`（原 `192.168.1.1`）；DHCP 池保持
  `100–249 / 12h`，PC 实测拿到 `10.0.0.120`。LuCI 管理地址随之变为
  `http://10.0.0.1`；Breed 仍是 `192.168.1.1`。
- **2026-11-21 真实校园网认证成功**：EAPMD5，profile“厦门华厦学院”，
  服务器 `00:1a:a9:17:ff:ff`，欢迎语“认证成功: 欢迎使用厦门华厦学院网络”；
  守护进程状态 `authenticated`，25 秒后复查仍稳定。账号与密码不入库。
- WAN 由校园网 DHCP 下发 `172.18.28.25/17`，网关 `172.18.0.1`，
  DNS `172.22.22.2/3`。路由器 ping `223.5.5.5` 3/3（约 21ms），
  PC 走 LAN 源地址 ping 也 3/3；DNS 经 `10.0.0.1` 解析正常。
- `servers list` 在会话存活期间为空是预期行为：`record` 在会话结束后
  才写 `servers.json`，不影响认证与联网。
- 2026-11-21 无线已启用：2.4G `ZhangCampus-2.4GHz`（自动信道，实测
  信道 1，HT20）；5G `ZhangCampus`（信道 149，VHT80）；均 WPA2-PSK /
  CCMP，country `CN`，桥接 `lan`。PC 扫描确认两个 SSID、信号 99%。
- 2026-11-21 完整认证循环：`logoff` → `idle`（已注销）→ `auth` →
  认证中（清理旧会话）→ 已认证（欢迎语 + server MAC），随后 ping 3/3。
  下线后交换机仍有几秒放行窗口，短时间内 ping 仍通，属校园网侧策略延迟。
- 2026-11-21 安全加固：root 密码已设置（不记录）；rpcd login 密码改为
  `/etc/shadow` 的 crypt hash（`$p$` 明文格式在该构建不可用，改 hash 后
  LuCI 登录恢复）；dropbear 与 uhttpd 只监听 `10.0.0.1:22/80/443`；
  wan zone input 改为 DROP，并新增 `Block-WAN-SSH/HTTP/HTTPS` 显式
  DROP 规则；旧 `root/root` LuCI 登录已失效，SSH 旧密码被拒。
- 约束边界已纠正：纯用户态/无 kmod 只针对认证器 ipk；固件包含
  UA3F/DoH/防检测 kmod/Vantage 主题。旧“UA3F/DoH 已移除”表述已删除。
- **2026-11-21 最终 sysupgrade（保留配置）**：OpenWrt 24.10 正常；
  Vantage 主题 + app 默认；DoH 单实例 5053；UA3F 3.7.0 运行，
  NFQUEUE 只排队 TCP SYN（`HOOK_TCP_SYN`），TCP timestamp/window 开启；
  IPID 由 nftables `ip id set 0` 内核改写；`disguise=full`。
  `ruijie-auth probe` 实测“在线”（4 个 TCP 53 目标中
  223.5.5.5:53、114.114.114.114:53 可达）；认证稳定，无重认证循环；
  LAN 客户端经路由器 ping/DNS 正常；CPU 95% idle、load 1.06。
- **CPU/IPID 结论（2026-11-21）**：UA3F 原 IPID 规则把
  `ip id != 0` 的所有 TCP 包送入 NFQUEUE（402,838 包 / 324MB，
  UA3F 74% CPU，load 5.37，仅 10% idle），平板和电脑同时在线即卡顿。
  关闭 UA3F IPID 后只剩 SYN（449 包），CPU 95% idle。为不降低防护，
  `ruijie-disguise` 在 nftables 中执行 `ip id set 0`，与 UA3F 原行为
  等价且内核开销接近零。IPID 是 IPv4 分片标识；多设备 NAT 下的 IPID
  模式可被用于指纹共享，写 0 隐藏该模式（分片流量设 0 可能影响重组，
  校园 TCP 普遍 DF，无影响）。
- **外网探测（学习 NetworkGuardian）**：管家默认用 4 个公共 DNS 的
  TCP 53 + ICMP，Npcap 二层直发，任一 TCP 回复（SYN-ACK/RST）或 ICMP
  回复即在线，quorum=1，2s/轮 3.5s；另有 HTTP/HTTPS 回退（NCSI
  connecttest 含 body marker、Baidu/Bing/QQ、gstatic/MIUI
  generate_204），检查状态码、重定向与门户劫持。认证器用等效用户态
  实现：TCP 53 四目标并行主探测 + MIUI 204 回退，`ruijie-auth probe`
  可自检；不再依赖百度。
- **日志页修复**：旧页 `fs.read` 被 ACL 拒绝；新增 `ruijie-auth log`
  与 `logs2.js`（`fs.exec`），菜单 logs 指向 logs2 以绕开旧 JS 缓存。
  实机 `fs.exec` 返回完整中文日志，页面 HTTP 200，翻译含“日志读取失败”。
- UA3F 没有官方 LuCI；详细参数在 `/etc/config/ua3f`，认证器 LuCI 的
  “帧伪装”提供 off/basic/full（full 启停 UA3F）。

## 8. 已知问题 / 限制

1. PEAP/EAP-TLS 没有真实 RADIUS 端到端验证；证书策略待确认。
   EAP-TLS 需要客户端证书，OpenWrt 版尚未提供证书配置入口。
2. WPA2-Enterprise 只验证列表，创建/连接需实验 AP。
3. SAM 报文布局未复现，只有探针/DES 工具；SAM key/IV 来源已实证
   （ENCRYPT_KEY/ENCRYPT_IV 属性）。
4. `password-change`、`config-update` 未实现（无证据）。
5. PEAP vendor segments 中 6 个未还原段函数（FUN_0051a510/00518bc0/
   0051bec0/0051bf40/0051bdb0/0051c020）待补；0x5F 凭据段已锁定。
6. 原版 GetClientVersion/GetMACAddr/GetIPv4Info 需要设备/全局版本状态，
   Unicorn 无法构造（GetServiceName/GetDHCPAuthPhase 已差分）。
7. GUI 单实例互斥锁：若生产 GUI 在运行，开发构建会静默退出（正常）。
8. 服务已以 `RuijieCampusAuth` 名称运行；若重新构建部署，仍按
   “停服务/GUI → 复制 → `service install`”流程执行。
9. MSCHAPv2：C# 通过 RFC 2759 官方向量；原版 CEapPEAPThread 内部函数
   依赖 TLS 引擎状态，无法零状态直调，未做二进制差分。
10. C# 缺 MSCHAPv2 AuthenticatorResponse（S=）校验（服务端 Success
    消息验证）——待补。
11. 校园账号通常只允许一个在线会话；同一账号在多台设备同时认证会
    互相顶下线。交叉测试前必须让旧客户端 Logoff。
12. NetworkGuardian 的 Npcap 原始探测跟随 Windows 默认路由；当 WLAN
    成为默认出口时，对非默认校园网卡的探测会误报离线。服务侧源 IP TCP
    复核已兜底；若要让其自身显示在线，需让校园网卡成为默认出口，或给
    探测目标加经校园网卡的静态路由。

## 9. 安全/运维注意

- 凭据：账号密码 DPAPI；调试日志对 password/token/key/iv 脱敏。
- 接管：IFEO `RuijieTakeover`（兼容旧版本标记）；服务名迁移会删除旧服务。
- 协议实验默认关闭，手动触发；DHCP release/无线变更需要确认。
- 账号重试有限制，避免触发服务器锁定。
- 日志轮转：服务 10MB、GUI 5MB、debug 10MB。

## 10. 文档索引

| 文档 | 内容 |
|---|---|
| `docs/MANUAL_CSHARP.md` | C# 版操作手册 |
| `docs/MANUAL_PYTHON.md` | Python 版操作手册 |
| `docs/MANUAL_OPENWRT.md` | OpenWrt 版操作手册 |
| `docs/REVERSE_ENGINEERING.md` | 官方客户端完整逆向报告 |
| `docs/DIFFERENTIAL_TESTING.md` | Unicorn 直接调用 + 差分方法与结果 |
| `docs/ADAPTATION.md` | 新学校适配、增量 provider、算法切换规则 |
| `docs/GENERICIZATION.md` | 通用化改造与延期项 |
| `docs/protocols/*.md` | 各协议证据、字段、实现、未知项 |
| `docs/OFFICIAL_CLIENT_FEATURE_MATRIX.md` | 功能矩阵与落位 |
| `docs/OPENWRT_PORT.md` | C# → OpenWrt 移植对照与验证 |
| `docs/OPENWRT_BUILD_ENV.md` | OpenWrt 构建、镜像与 QEMU 测试记录 |
| `docs/OPENWRT_FEASIBILITY.md` | OpenWrt 早期可行性评估（历史决策记录） |
| `docs/protocols/README.md` | 逐协议档案索引 |
| `docs/FROZEN_DEPLOYMENT.md` | 部署、升级与运行环境记录 |
| `profiles/README.md` | 档案字段、正式档案与导入 |
| `tools/re_diff/README.md` | Unicorn 原版函数差分工具 |

## 11. Git

- 分支：仅 `main`（本地与远端一致，临时特性分支已合并并删除）
- 默认分支：`main`
- 远端：`origin https://github.com/FairyXH/RuijieCampusAuth.git`
- 当前 HEAD 以 `git log` 为准。
- 主要节点：OpenWrt 认证链复核与三份真实尾包向量；README 论文式重写与
  全文档索引；仓库路径迁移；文档与 Python 包去校名化；真实桥接端到端
  认证与 `v0.9.1` 修复发布。

仓库已从旧路径迁移到
`D:\Files\Develop\Cross-Platform\RuijieCampusAuth`，源码内容与远端历史不变；
`feat/debug-protocols`、`feat/openwrt-r3g`、`feat/ruijie-private-tail`
已并入 `main` 并删除。

## 12. 下一步

1. PEAP 完整 vendor segment：补齐 6 个未还原段函数
   （FUN_0051a510/00518bc0/0051bec0/0051bf40/0051bdb0/0051c020）。
2. 实验 RADIUS/AP 验证 PEAP 与 WPA2-Enterprise。
3. 抓取真实 SAM 交互，用实证的 key/IV（ENCRYPT_KEY/ENCRYPT_IV）补
   `SamDirectClient` 报文编解码。
4. 补 MSCHAPv2 AuthenticatorResponse（S=）校验。
5. 部署新构建：停旧服务/GUI → 复制到 D: → `service install`（迁移服务名）。
6. OpenWrt 现场验证：R3G 实机刷入镜像，导入学校档案；x86_64 QEMU
   桥接真实校园网已完成认证与 LuCI 验证，PEAP 学校补一次隧道抓包。
7. Python 服务宿主打包（`python/build.ps1` + PyInstaller）与 Linux systemd
   包装待实机验证。

差分校对工具链：`tools/re_diff/full_diff.py`（Unicorn 直调原版）+
`tools/csharp_diff/`（C# DiffRunner）；差分原始数据在
`build/capture/`（live-auth.pcap、original-parse-truth.json、
tail-compare.json、full-diff-report.json）。

## 13. 2026-11-21 整机扩展构建（0.10.0）

目标：把 R3G 做成刷入即用的整机固件，同时保持认证器 ipk 纯用户态。

- 包集：目标 feed 全部 1034 个 kmod（`scripts/kmods-all.txt`），排除 8 个
  互斥/异构变体：ath10k 三个替代、hsdma-mtk、mwifiex-sdio、r8125-rss、
  r8126-rss、sdhci-mt7620。完整 rootfs 89.3MB，UBI 117.4MiB，overlay
  约 24MiB。
- 上行：mwan3 2.11.16，校园 WAN → `usbwan` → `wwan`，httping 204 探测、
  5 秒周期、连续 2 次失败切换。`90-ruijie-usbwan` 支持 RNDIS/NCM/ECM/
  ipheth；`91-usb-wifi` 自动注册后插入的 USB 无线网卡。
- QoS：SQM 1.6.0 + CAKE，默认 200/200 Mbit，`dual-srchost`/`dual-dsthost`
  按主机隔离。
- 统计：nlbwmon + vnstat2；`/var` 是 tmpfs，`zz-r3g-defaults` 把
  vnstat DB 改到 `/etc/vnstat`（SaveInterval 15），nlbwmon 改到
  `/etc/nlbwmon`（默认 24h commit）。vnstat 监控 wan/br-lan。
- 代理/穿透：OpenClash 0.48.002 + mihomo 1.19.34 mipsle softfloat
  （61MB 二进制，`small_flash_memory=0` 才会用 /etc 核心），默认
  redir-host、不劫持 DNS、停止；frpc 0.51.3 预配置停止。
- UPnP：miniupnpd-nftables，LAN 侧 secure mode，启用。
- 首次启动：`zz-r3g-defaults` 必须最后运行（zz- 前缀），负责启用
  UA3F/DoH/mwan3/SQM/统计/UPnP，关闭 OpenClash/frpc 占位服务，并把
  `firewall.openclash` include 删除；该 include 指向只在 OpenClash 运行时
  存在的 `/var/etc/openclash.include`，保留会让 fw4 启动失败。
- 探针：`connectivity.TCPOn/HTTPOn` 用 SO_BINDTODEVICE 绑定校园网出口，
  避免 USB/Wi-Fi 默认路由把校园网掉线误判为在线；CLI `probe --device`。
- 构建：`vendor/` 固定 UA3F/OpenClash/mihomo/Vantage 的 ipk 与哈希；
  `fetch-imagebuilder.sh` 固定 24.10.0 IB 与 SHA256；Image Builder 缓存
  完整后一次构建约 8-10 分钟。
- 验证：`go test`/`gofmt` 干净；MIPS chroot 中 `ruijie-auth 0.10.0
  selftest` 通过，mihomo `-v` 正常，`zz-r3g-defaults` 语法通过；
  UCI 解析 mwan3/sqm/vnstat 正常。
- 产物：sysupgrade 92,509,306（8c4edddd...）、kernel1 3,181,020
  （f1718d09...）、rootfs0 93,192,192（92572686...）、Breed 合并
  101,580,800（143a60be...）、ipk 2,741,830（6f04f52f...）。
- 刷写约束：sysupgrade 超过 R3G 的 /tmp 与 overlay，必须走 Breed
  kernel1 + rootfs0；Breed 1.2 的“原厂→固件”也不接受 97MB 合并镜像。
- 当前状态：构建、校验、离线验收完成；等待 Breed 实机刷入后验证
  USB/Wi-Fi STA/mwan3/SQM/OpenClash/统计并更新本文件。
