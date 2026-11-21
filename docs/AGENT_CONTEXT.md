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
  `dist/openwrt/` 与 R3G 工具包；两处固件镜像仍是 2026-11-20 构建
  （内含 0.2.0），刷完镜像后需再装新 ipk 才是最新 LuCI。
- R3G 整机镜像重新构建：9,155,706 字节（8.7MiB），SHA256
  `91f3520ac8837f3a40530813de0c488e00c0edecb94e96efe26f97954d0e1e5f`；
  rootfs 含 `ruijie-auth` 包、8 个 LuCI 视图、`ruijie-auth.zh-cn.lmo`，
  已在 squashfs 内逐项核对。
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
- 仍待办：刷 OpenWrt Breed 镜像 → 装 0.9.0 插件。固件镜像内仍是
  0.2.0，刷后需 `opkg install --force-reinstall` 新 ipk。

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
