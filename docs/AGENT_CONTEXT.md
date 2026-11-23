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

### R3G 无线解锁固件（2026-11-23）

- 需求：禁用 DFS、任意国家码下 2.4G/5G 全信道可用、发射功率解除监管
  上限。无线栈是 **backports-6.12.6**（`kmod-cfg80211`），不是内核树，
  所以只改 cfg80211，内核镜像不动。
- 补丁在 `D:\Files\Develop\OpenWrt\MiR3G_V1-Openwrt\source-patches\wireless-unlock\`：
  - `350`：`map_regdom_flags()` / `handle_channel()` 去掉 DFS / NO_IR 标志；
  - `351`：`cfg80211_chandef_dfs_required()` 恒 0；辅助函数标
    `__maybe_unused` 过 `-Werror`；
  - `352`：`freq_reg_info_regd()` 无规则时返回兜底
    `2402-2482@40`、`2474-2494@20`（ch14 802.11b）、`5150-5925@160`；
  - `353`：`chan->max_power` 恢复驱动 `orig_mpwr`（mt76 每信道 30 dBm），
    不再被国家码 max EIRP 压低；EEPROM/固件仍做硬件限幅。
- 构建环境：源码树在 Ubuntu 26.04 上直接 `make` 会因新 glibc/GCC 编不过
  老 host `m4 1.4.19` / `cpio 2.15`；改用官方 **SDK 24.10.0
  mt7621**（内核 6.6.73）编译 `kmod-cfg80211`。最终模块 `5c5bd4…`，
  vermagic `6.6.73 SMP mod_unload MIPS32_R2 32BIT`，
  `CONFIG_MODVERSIONS` 未开，`compat.ko` 与官方字节一致。
- 组镜像用 Image Builder + `EXTRA_FILES` 覆盖（包安装后覆盖
  `/lib/modules/6.6.73/cfg80211.ko`）；`build-image.sh` 新增
  `EXTRA_FILES` 可选覆盖层。全量镜像 507 包，kernel1 哈希仍为
  `f1718d09…`，证明内核未变。
- 固件使用正常命名（不再带 `_DFS` 后缀）。2026-11-23 0.11.4：
  `ruijie-auth 0.11.4`（commit `9f1c6ad-hb-off`，SAM 心跳默认关闭），
  内置官方 `zerotier 1.14.1-r3` + 本机 LuCI 页面（VPN → ZeroTier），
  sysupgrade `3f679b…`，Breed `40007c…`，rootfs0 `c2c737…`，认证器
  ipk `781d1b…`，kmod ipk `0a1016…`。旧固件与旧 0.10.0 ipk 在
  `MiR3G_V1-Openwrt\Backup\firmware-2026-11-21\`。
- 06:00 认证恢复成功，但 SAM 心跳目标 `172.22.11.1:53` 不可达
  （ping 100% loss、DNS 超时；公共 DNS 正常），心跳每 3 次无响应
  触发重认证，约每 1 分 36 秒循环一次。06:09 已 `heartbeat=0` 止损；
  0.11.4 把 Go/UCI/LuCI 默认全部改回关闭。仅当 SAM 可达时手动开启。
- ZeroTier：`/etc/config/zerotier` 是 conffile，sysupgrade 保留 secret/
  网络 ID/enabled；LuCI 页面由固件 FILES 内置（`www/.../zerotier/*.js`、
  menu/ACL/rpcd/lmo、`/etc/init.d/luci-zerotier`），daemon 由 opkg 管理。
- 非内置应用保留：`/etc/custom-packages/` 或
  `/mnt/*/custom-packages/` 放 ipk + install.sh；首启
  `99-custom-packages` 调 `/usr/sbin/custom-packages-restore`，
  `/lib/upgrade/keep.d/custom-packages` 保留缓存。natfrp 安装约 41MB，
  overlay 约 53MB，大包建议放 U 盘。
- 0.11.2 LuCI 补全：设置页新增“SAM 心跳”页签（开关 + 间隔）；Debug
  页新增 `probe`（可选出口设备）与 `tail` 私有尾生成器；学校档案页
  新增 `profile show` 的“查看”弹窗；PO 新增 20 条中文，LMO 9120 字节。
  CLI 里只剩 `daemon`/`help` 没有 UI，其余全部有入口。
- LuCI 静态资源 `?v=` 来自 `/usr/lib/opkg/status` 的 mtime。可复现镜像
  把它固定成 2025-02-03，升级后浏览器会继续用旧 view。2026-11-23
  01:32 已 `touch /usr/lib/opkg/status` 并清 `/tmp/luci-indexcache.*`，
  主页面 `luci.js?v` 变为 `...-1795368721`；新增
  `files/etc/uci-defaults/99-luci-cache-bust`，下次镜像首次启动自动
  刷新资源版本。
- 0.11.1 曾默认开启心跳；0.11.4 因 SAM `172.22.11.1` 不可达、
  每约 1 分 36 秒触发重认证循环，已改回默认关闭；仅当 SAM 可达时
  在设置页手动开启。
  构建时 VM 不是 git checkout，用 `git archive` 同步 `ruijie-auth` 和
  `files`，并做了 CRLF→LF 归一化。
- 2026-11-23 01:00 已刷 0.11.0 并验证；01:07 打开 `heartbeat=1`；
  01:24 刷到 0.11.2；01:52 刷到 0.11.3。06:00 认证成功，但 SAM
  `172.22.11.1:53` 不可达，心跳每 3 次无响应触发重认证，约每
  1 分 36 秒循环；06:09 `heartbeat=0` 止损；06:16 刷到 0.11.4
  （默认心跳关闭），认证成功、ZeroTier ONLINE <zerotier-ip>/24、
  无线 ch13/ch60 保持、overlay 46.8M 空闲。
- 非内置包恢复机制已就位：`/etc/custom-packages` 或
  `/mnt/*/custom-packages` + `install.sh`，首启
  `99-custom-packages` / 手动 `/usr/sbin/custom-packages-restore`；
  keep.d 保留缓存。SakuraFrp/雷神这次未装回，`/etc/config/natfrp`
  等配置仍保留；natfrp 安装约 41MB，和大包雷神一起放 overlay 不现实，
  建议 U 盘缓存。
- 2026-11-23 02:04 起稳定性监测：每 15 分钟（02:15–09:30 的
  :00/:15/:30/:45），日志 `logs/router-stability-2026-11-23.md`，
  最终报告 `docs/ROUTER_STABILITY_2026-11-23.md`；自动化
  `automation-87ecf198-7b77-441d-8aa5-43c33442c8f4`，09:30 出报告后暂停。
  重点看 06:00 认证恢复、SAM 心跳是否稳定、ZeroTier 是否上线。
- 最终报告已生成（`docs/ROUTER_STABILITY_2026-11-23.md`，覆盖到
  09:37）：认证稳定，ZeroTier ONLINE <zerotier-ip>/24，无线未变；
  SAM 心跳因目标不可达已默认关闭；09:03 一次 4 秒瞬时认证失败已自愈。
  监测任务已暂停。
- 实机调优（2026-11-23）：2.4G **ch13/HT20/26 dBm**（配置曾写 HT40，
  ch13 实际回落 20MHz，已把配置对齐 HT20）；5G **ch60/VHT80/23 dBm**，
  客户端协商 866 Mbps；ch165 VHT80 中心 171（5855）越界，客户端不吃
  80MHz；5G 空闲块 36/52/60 均可。
- 注意：这是非标准监管行为；实际 PA 输出仍受 EEPROM 限幅，ch14 只到
  802.11b。

### 真实校园网端到端（2026-11-20）

- **Ubuntu 桥接**：VMware 改为桥接后，Ubuntu `ens33`（`172.18.28.21/17`）
  作为真实 802.1X 客户端。Go 守护进程与最终 C# 0.9.1 tar 包分别完成
  EAP-MD5 认证，服务器 `<auth-server-mac>`，收到私有属性
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
  服务器 `<auth-server-mac>`，欢迎语“认证成功: 欢迎使用厦门华厦学院网络”；
  守护进程状态 `authenticated`，25 秒后复查仍稳定。账号与密码不入库。
- WAN 由校园网 DHCP 下发 `<wan-ip>/17`，网关 `<gateway>`，
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
- **desync reorder 破坏移动端 TCP（2026-11-22）**：UA3F `desync_reorder=1`
  会把 80/443 新连接前 8 个包（总量 <1500 字节）重排来对抗 DPI。Windows
  的 TCP 栈靠重传能恢复（有线与 Wi-Fi 都实测 HTTP 200），但 Android/iOS
  直接超时：一加 Ace 5 与平板所有网页 `ERR_CONNECTION_TIMED_OUT`，同一个
  SSID 上的 Windows 笔记本却完全正常；`/etc/init.d/ua3f stop` 后移动端
  立即恢复。默认改为 `desync_reorder='0'`，只保留 TTL / TCP timestamp /
  TCP window 这类单包字段改写。另外 USB/4G 出口不做流量检测，UA3F 在该
  出口没有意义，4G 模式下可直接 `ua3f.enabled=0`。
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

## 13. 2026-11-21 精简全功能固件（0.10.0）

目标：USB 全功能 + R3G 板载 + 插件依赖 + 打印机，砍掉无关 kmod，保持
插件功能完整；刷入后远程 sysupgrade，并做真机回归。

- 包集：237 个 kmod（依赖闭包，`scripts/kmods-r3g.txt`），只保留
  USB 全功能、ext4/f2fs/exfat/NTFS3/Btrfs/XFS、USB Wi-Fi、串口/网卡/
  音频/视频/HID/打印机、R3G 板载和插件依赖。完整 feed 清单保留在
  `kmods-all.txt` 对照。rootfs squashfs 53,279,744；rootfs0 55,967,744；
  sysupgrade 56,474,746；overlay 首次启动约 49.5MiB。
- 上行：mwan3 校园 WAN → `usbwan` → `wwan`。实测一加 Ace 5 RNDIS
  识别为 `eth1`、取得 192.168.87.x；`ip link set wan down` 后 mwan3
  自动切到 usbwan 并保持上网，wan 恢复后切回并重新认证。
- mwan3 rule 必须写 `use_policy`，写成 `policy` 会让整个 failover 静默
  失效：`mwan3_rules` 链为空，流量不打 0x200 标记，全部落 netifd 主表。
  主表里未设 metric 的 wan 默认路由（metric 0）压过 `usbwan` 的
  metric 100，于是 wan 链路层仍 up、只是对端不响应（ping/ARP 全失败）时，
  客户端收到 `Reply from <wan 地址>: Destination host unreachable`。
  `zz-r3g-defaults` 已强制 `delete policy` + `set use_policy=failover`，
  修复旧 overlay 里遗留的错误字段。
- 认证器：默认 `device=wan`。`auto` 在手机插上后会把 `eth1` 当校园
  WAN，导致状态变“暂停”；首次启动脚本用 UCI 钉死 wan。
- QoS：SQM/CAKE 95/95 Mbit（校园线 100Mbps 的 95%），
  dual-srchost/dual-dsthost。SQM rc.d 链接在 sysupgrade 后可能丢失，
  `r3g-defaults` init 会按 `sqm.wan.enabled=1` 恢复。
- 统计：vnstat DB 在 `/etc/vnstat`（SaveInterval 5），nlbwmon 在
  `/etc/nlbwmon`（commit 1h）。vnstatd 首启可能先于 DB 可写而退出，
  `r3g-defaults` 每次启动重新 add wan/br-lan 并 restart。
- 打印：`kmod-usb-printer` + `p910nd` + LuCI，TCP 9100；
  `20-p910nd` 在 USB 打印机 add 后等待 `/dev/usb/lp0` 并重启服务。
  无打印机时服务为 “active with no instances”；模拟 lp0 时 9100 监听通过。
- 自动重启：watchcat，`ping_reboot`，6 小时无 ping、pinghosts
  223.5.5.5/119.29.29.29，forcedelay 30，避免抖动重启循环。
- USB/IP：usbipd 3240 + usbip/usbip-server/client + `usbip-share`。
  手机绑定会触发 USB 重新枚举（bind 完成但随后 disconnect），
  ADB/存储导出需设备不重置；功能与 CLI 已装好。
- USB Wi-Fi：MT7921U（0e8d:7961）识别为 `radio2`（5G），实开
  `USBTEST-5G` AP 成功，随后恢复 disabled。
- 防检测：UA3F desync reorder=1（1500/8，80/443）实测 HTTPS 正常、
  CPU 0%/空闲 93%；inject=0，TTL 1/2/3 均导致 TLS EOF，不能默认开。
- OpenClash：核心/GeoSite 只在 rootfs；`luci-openclash` 的 keep.d
  被 `r3g-defaults` 改为只保留 config/custom/overwrite/rule_provider/
  proxy_provider/history，避免 sysupgrade 把 72MB 复制进 overlay。
- uhttpd：监听 0.0.0.0:80/443；绑定 10.0.0.1 会在 LAN 地址晚就绪时
  崩溃循环。WAN 侧仍由防火墙拒绝。
- 首次启动兜底：`/etc/init.d/r3g-defaults`（S99）在
  `/etc/uci-defaults/zz-r3g-defaults` 仍存在时执行并删除；解决
  sysupgrade 保留 overlay 时 uci-defaults 不执行的问题。
- 中文：luci.main.lang=zh_cn；补 `luci-i18n-firewall-zh-cn`、
  `luci-i18n-package-manager-zh-cn`；OpenClash/UA3F 的 lmo 随包。
- 产物：breed 64,356,352（561d3590...）、sysupgrade 56,474,746
  （5e9215d5...）、kernel1 3,181,020（f1718d09...）、rootfs0
  55,967,744（65e3bfb0...）、ipk 2,741,830（6f04f52f...）、manifest
  15,633（ca9d039b...）。
- 回归：构建/校验/离线 chroot 通过；真机多次 sysupgrade；冷启动后
  认证、SQM 95M、UA3F reorder、uhttpd、usbipd、watchcat、统计和
  overlay 余量全部保持。未接真实打印机；USB/IP 手机绑定受设备重置
  限制，需换稳定设备复测。

### 13.1 2026-11-21 最终修订

- mwan3 原用 `connect.rom.miui.com/generate_204` 跟踪，实测每约 30 秒
  online→offline→connecting→online 抖动；`flush_conntrack=1` 每次清空
  所有连接，多设备时大面积卡顿。改为 ICMP 223.5.5.5/119.29.29.29，
  down/up=3/2，实测连续 6 分钟 online 无切换。
- 认证器关闭成功后主动重认证：`reauth_interval=0`、
  `proactive_reauth=0`；服务器发起的重认证仍应答。
- 新增 00:00-06:00 认证关闭时段 `quiet_window/quiet_start/quiet_end`。
  实测 20:06-20:08 临时窗口：状态显示“校园网认证关闭时段”，窗口结束
  自动重新认证成功。
- 探测改为四台公共 DNS 并行真实 DNS 查询（默认 www.baidu.com），
  任一成功即在线；TCP 53 兜底；再用百度/Bing/QQ HTTPS 二次确认。
- Go 二进制内嵌 `time/tzdata`，procd 设置 `TZ=Asia/Shanghai`；修复
  之前日志/quiet window 按 UTC 运行的问题。
- NetworkGuardian 显示有线离线的根因是 Windows 有线网卡被我改成静态
  10.0.0.120 且无默认网关；恢复 DHCP 后 Guardian 的 per-interface
  探测为 online、outletMatchesPolicy=true，默认出口回到有线
  （effective metric 11，WiFi 13）。
- 最终产物：breed 64,487,424（9d6f01c9...）、sysupgrade 56,546,426
  （243621d6...）、kernel1 3,181,020（f1718d09...）、rootfs0
  56,098,816（d64f735a...）、ipk 2,848,622（adf4bad1...）、manifest
  15,633（ca9d039b...）、mipsle 8,061,143（9849e0c2...）、amd64
  7,352,504（7f4bf5c9...）、lmo 8,076（8b0ac639...）。
- 最终镜像已在 R3G 刷入并验证：认证、DNS 探测、SQM 95M、mwan3 稳定、
  HTTPS、UA3F reorder、uhttpd、usbipd、overlay 49.5MiB 均正常。

## 14. 2026-11-22 校园网掉线+反复重连诊断（账号互顶）

现象：R3G 上会话每隔 0.5-1.5 小时被静默踢掉（无 EAP-Failure、无服务器
重认证请求，流量直接黑洞），守护进程探测到掉线后重连成功；掉线时出现
6-7 秒一轮的连续互踢「风暴」（每场 7-13 轮、持续 40-90 秒），风暴尾常
伴随 1-2 次「认证失败（服务器未返回可读原因）」。

- **根因是账号共享互顶，不是协议缺失。** 关键证据：Identity 提交后服务
  器不进 MD5 直接回 EAP-Failure（12:45:57、16:30:39、20:52:46 各一次）
  ——这是「账号已在别处在线」的服务器拒绝签名，与 2026-11-20 多端互顶
  记录一致。风暴=对方客户端与本端守护进程互相顶下线后各自秒级重连。
  本端会话无任何 keepalive/心跳也稳定持有 79-91 分钟（13:23→14:49、
  14:50→16:19、17:52→19:19、19:21→20:52），若缺必需心跳会按服务器
  固定周期被回收且重认证不会被拒，故排除。
- **对方节奏**：约每 90 分钟主动重认证一次（对方客户端的
  SU_REAUTH_INTERV 或刷新周期），每次触发一场战争；上午为 16-18 分钟
  一次（对方活跃使用中）。
- **排除本机 Windows 服务**：`RuijieCampusAuth` 服务全天在路由器 LAN
  （10.0.0.120→10.0.1.120）后运行，NG-TMP-0000 上无认证服务器，自
  11-21 00:22 后零成功认证，踢不到人；但其 Guardian 离线误报触发空转
  重认证循环（建议停用或改配置，见下）。
- **探测节奏实测**（nftables output 计数）：当前会话每 ~33s 一波探测
  （DNS A/AAAA×4 目标并行 + TCP53/HTTPS 兜底），check_interval=30 已
  生效；6-7s 风暴周期是旧会话以默认 check_interval=2s 运行（当时 UCI
  尚未写入该键）+ 黑洞探测耗时 4s 叠加而成。17:52 写入
  check_interval=30 后生效（当前会话实测确认）。
- **构建不一致（待修）**：`dist/openwrt/ruijie-auth_0.10.0_amd64` 由
  旧源码树构建（二进制内无 check_interval/quiet_window/
  proactive_reauth 解析 case），却因 COMMIT ldflags 覆盖打着
  0.10.0/4310534 版本号；mipsle（部署用）正常、字符串齐全。amd64 需
  从 HEAD 重建；版本号被 COMMIT 覆盖时不可作为源码依据，以二进制
  字符串指纹为准。
- **协议遗留（非根因）**：官方客户端有 DirComm 心跳体系（HELLO_INTERV
  0x59 / CLIENT_MSG_PERIOD 0x75 / DIRECT_COMM_HEARTBEAT_FLAGS 0x80 /
  MSG_CLIENT_PORT 0x6E=138），本项目未实现（功能矩阵 G1-7 缺失）。本校
  服务器未下发 HELLO_INTERV，非必需；若要还原官方行为或提高互顶竞争
  胜率（服务器可能优先保留有心跳的会话，未证实），可逆向 8021x.exe 的
  DirComm 心跳报文后补实现。
- **缓解**：根本解决=对方改用自己账号或错峰使用；本端 check_interval=30
  降低战争烈度但拉长掉线感知（最坏 ~34s），可按需折中到 10s；preempt=0
  保持（本端不主动挑起战争）。16:19 曾出现 /var/run tmpfs 满（
  status.json 写失败），当时有其他进程占用 /tmp，未复现，观察即可。

### 14.1 自助服务明细交叉定位（2026-11-22 晚补充）

用 SAM 自助明细（上线/下线时间+IP）与路由器日志交叉验证，修正根因结构
（路由器开机后 NTP 校时前日志慢约 25 分钟，已按明细校正）：

- **<wan-ip> = R3G**（MAC <router-wan-mac>，wan）。
- **10.80.2.86 与 192.168.137.112 = 同一台外部有线客户端（战争主犯）**。
  五次"同秒击杀"：10:57:34、12:34:59、16:25:44、16:30:28、21:17:15，
  每次该客户端上线瞬间路由器有线会话被顶掉；本端刷机离线窗口
  （10:17-10:55）它独占 38 分钟。192.168.137.112 是该客户端插线首次
  认证时私有尾上报的残留 ICS 地址（Windows 热点网段），拿到 DHCP 后
  变为 10.80.2.86；无线 PEAP 不上报 IP，故为有线设备。
- **10.80.5.145 = 外部无线设备①**（MAC `<external-client-mac>`），与有线
  合法共存（16:30-21:08 与路由器重叠 4h38m，证实 1 有线+2 无线政策）。
  其无线会话被 WLC 精确 88 分钟一踢（多次 1h28m±10s），重认证的
  60-90s 窗口内本端数据面中断但 SAM 会话记录不断——即下午
  14:49/17:49/19:19 三场探测风暴的真身（当时 check_interval=2s 连打
  8-10 轮重认证，SAM 将快速重认证合并进同一会话记录）。
- **10.80.6.160 = 外部无线设备②**：仅 20:03-20:10 出现，与 10.80.5.145
  每 15-25s 交替互踢（争抢 2 个无线名额）。
- 本机 Windows/手机/平板均不在明细中（全挂在本端路由器 LAN，DHCP 租约
  为证；Windows 的 HXXY-WiFi 档案当日未使用）。
- 结论修正：掉线=外部**有线**客户端抢有线槽（硬踢，同秒）+外部无线设备
  88 分钟 WLC 重认证引发账号级 ACL 抖动（软中断）。协议实现无缺失。

### 14.2 无线 STA 实验、Windows 热点与信道勘察（2026-11-22 深夜）

- **R3G 存在 STA 配置**：`wireless.wifinet2`（radio1/5G，HXXY-WiFi，
  PEAP+EAP-MSCHAPv2，identity=<account>，当前 disabled=1）。21:08:56
  启用→21:09:04 PEAP 认证成功（对应明细 21:09:15 无 IP 0 秒会话，STA
  未挂 network 无 DHCP）→21:10:52 移除。两次"5G AP 弹跳"（21:08:56/
  21:10:53 三客户端同秒掉线）=AP+STA 同 radio 重配置，非 DFS 雷达。
  STA 每次关联即用本账号做一次无线认证，在 1+2 政策下自耗名额。
  21:17:15 的 10.80.2.86 攻击发生在 STA 移除且 wpa_supplicant 静默
  之后，外部有线竞争者 X 结论不变。
- **本机 Windows 移动热点 "ZhangWindows" 在广播**（BSSID 76:13:EA:
  82:60:4F=本地连接*10/ICS 192.168.137.1，2.4G ch1，-13dBm 贴脸），
  与路由器 2.4G 同信道互踩；也是 192.168.137.x 网段实体的来源。
- **信道勘察（路由器权威扫描）**：5G 仅 7 AP——ch36 华为不卡5g(-76)、
  ch48 ZCF(-59)、ch149×4（校园 wy_5G+隐藏）、ch157 HXXY-WiFi(-67)；
  **52-64 与 100-140 DFS 块全空**。2.4G：ch1=6 AP（校园 Ruijie
  多 SSID 全家桶+自家热点）、ch4=1、ch6=2、ch11=6（另一台校园 AP）、
  ch13=1（华为不卡-60）。5G 维持 52（VHT80）；2.4G 最佳 ch13，次选
  ch6；DFS 运行时 mac80211 拒绝离信道扫描（勘察需临时挪非 DFS）。
- CN regdb 无 5470-5725（ch100-144）规则，非"空"而是"不允许用"；
  5G 真实选项仅 36-48 / 52-64(DFS) / 149-165。计划改 wireless-regdb
  源码去掉 CN 的 DFS 标志（OpenWrt 内核不校验 regdb 签名，自制
  regulatory.db 经 FILES 覆盖即可，重启生效）。

### 14.3 光遇"服务器繁忙"根因：ua3f-scope 强制开 desync_reorder（2026-11-22 深夜）

- 现象：换路由器+UA3F+DoH 后光遇（sky.exe）一直"服务器繁忙"。
- 根因：41a6bdf 的 `ua3f-scope`（cron 每分钟）在校园口有地址时**强制
  `ua3f.main.desync_reorder=1`**，把 2292e4c（reorder 破坏 Android/iOS
  的修复）整个短路。nft `DESYNC_REORDER_QUEUE` 实测 57k+ 包被重排
  （80/443 新连接前 8 包）。光遇登录的 HTTPS 服务器丢弃乱序 TLS 记录
  而非重传 → 登录失败。sysupgrade 保留的旧 overlay 配置叠加该动态
  逻辑，使"默认 0"的修复从未真正生效。
- 修复（提交 a36f31f）：ua3f-scope 不再强制开启 reorder，仅保留
  "校园口无地址时自动关闭"的方向；显式设 `desync_reorder=1` 才会在
  校园口保留。已部署 R3G（scp 覆盖 /usr/sbin/ua3f-scope，md5 一致），
  cron 恢复，实测 reorder=0 稳定、DESYNC 链消失、出口 scope 照常。
  关闭后光遇登录恢复正常。下次 ipk/固件构建自动带上。
- 遗留：`ip id set 0`（内核 postrouting）对分片流量仍是重组杀手，
  TCP 因 DF 无碍；若未来有 UDP 大包游戏/应用异常，优先查这条。

### 14.4 手动插件装回：SakuraFrp + 雷神，本地备份/恢复（2026-11-23 11:20）

- R3G 已装回两个非内置插件，均开机自启并运行：
  - **SakuraFrp Launcher 3.1.9**（`luci-app-natfrp`，`natfrp-service` +
    `natfrp-frpc`），监听由服务自己管理；LuCI 入口“服务 → SakuraFrp”。
  - **雷神加速器**（官方 `acc-gw.router.mipsel`，安装目录
    `/usr/sbin/leigod`），tproxy 模式，监听 **TCP 5588/10001**。
- overlay 实测：52.8MB 总，25.5MB 已用，24.6MB 可用。natfrp 安装体积
  42MB，UBIFS 压缩后约 14MB；雷神目录约 8.8MB。两个同时装得下。
- **SakuraFrp token 丢失**：旧 `/etc/natfrp/config.json` 不在 sysupgrade
  保留列表，`/etc/config/natfrp` 里的 token 早被 init 脚本清空；当前
  `config.json` 的 token 为空。服务能启动但没有隧道，需在 LuCI 重新
  填访问密钥（或从 SakuraFrp 控制台取），保存后重启服务。填好后必须
  再跑一次 `backup.sh` 把 token 收进本地备份。
- 雷神官方安装器把 fw4 误判成 iptables：它只检查 `/usr/sbin/fw4`，
  而本固件在 `/sbin/fw4`。补 `ln -sf /sbin/fw4 /usr/sbin/fw4` 后安装
  成功；`acc_firewall.ini` 仍为 `backend=iptables`，运行时通过
  `iptables-nft` 兼容层注入 nft 规则。已补
  `/etc/hotplug.d/firewall/99-leigodacc-restart`（fw4 reload 后重启 acc
  重新注入）；并恢复了 fw4 管理器的
  `/usr/sbin/leigod/leigod-fw4.sh fix-tproxy` 和每 5 分钟 cron。
  该 watcher 不是废弃脚本：官方引擎会把 `tproxy_ip` 硬编码成
  `10.20.30.40`，watcher 把 `/etc/config/acc_firewall.ini` 和
  `/tmp/acc/acc_core_conf.json` 拉回 LAN IP。当前已改为
  `tproxy_ip=10.0.1.1`，daemon/web 均以 `-l 10.0.1.1` 运行，
  `LeigodAcc` LAN→TCP 5588 放行规则保留。
- 新增 PC 端工作流（不把大包长期放路由器）：
  `D:\Files\Develop\OpenWrt\MiR3G_V1-Openwrt\tools\manual-apps\`
  - `backup.sh`：打包 `/usr/bin/natfrp-*`、`/etc/natfrp`（含 token）、
    `/usr/sbin/leigod`、`/etc/init.d/acc`、acc 配置和 fw4 hotplug，
    拉到 `Backup/manual-apps/<时间戳>/`，并保存包列表/哈希。
  - `restore.sh`：sysupgrade 后推回、装本地 ipk/依赖、解包、补防火墙
    规则、enable/restart 两个服务并验证端口。
  - 已实测 backup → restore 闭环通过；`natfrp.ipk`、`libpcap1.ipk`、
    `tc-full.ipk`、`leigod-official-install.sh` 放在
    `tools/manual-apps/packages/`。
  - 备份含 natfrp token，`Backup/` 和备份目录都不得上传网盘/公开仓库。
- 文档已更新：`tools/manual-apps/README.md`、工具包 `README.md`、
  `固件刷写教程.txt`、`openwrt/files/etc/custom-packages/README`
  （后者说明 PC 端备份优先，路由器缓存可选）。
- 2026-11-23 11:46 生成正式备份
  `Backup/manual-apps/20261123-1146/`（natfrp 14MB、雷神 3.4MB），
  并用它完整跑过 `restore.sh`：natfrp/雷神均 running、开机自启，
  `tproxy_ip=10.0.1.1`、`fix-tproxy` cron 与 fw4 hotplug 都在。
  唯一未完成项：natfrp token 为空，待用户填入访问密钥后重启服务并
  重新跑 `backup.sh` 把 token 收进本地备份。






