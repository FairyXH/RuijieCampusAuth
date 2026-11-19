# Agent Context

> 跨会话交接文档。描述项目当前状态、架构、验证结果与下一步，不记录完整对话历史。
> 最后更新：2026-11-20

## 1. 项目是什么

通用的锐捷校园网第三方认证客户端（“锐捷认证器 / 锐捷校园网开放认证器”）。
使用官方协议做兼容认证，不依赖官方进程；学校差异通过 `profiles/*.json` 导入。
默认不内置任何学校配置。

两套实现：

- **根目录 C# / .NET 10**：Core + CLI + Windows 服务/Linux systemd + Avalonia GUI。
- **`python/`**：Python 参考实现（本轮未改动，后续通用化）。
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
RuijieCampusAuth.Core.Tests/ 44 tests
RuijieCampusAuth.Service.Tests/ 管道集成测试
profiles/                    学校档案（hxxy.json 为当前示例）
tools/re_diff/               Unicorn 原版函数调用器
docs/                        上下文、逆向、适配、协议档案
openwrt/                     锐捷认证器 OpenWrt 版：ruijie-auth（Go）+ luci-app-ruijie-auth
python/                      延期，不动
```

## 3. 构建 / 测试

```bash
dotnet build RuijieCampusAuth.slnx -c Release
dotnet test RuijieCampusAuth.Core.Tests -c Release
dotnet test RuijieCampusAuth.Service.Tests -c Release
```

- SDK：.NET 10.0.302。
- 发布：`build-csharp.ps1 -Runtime win-x64`（framework-dependent）。
- 测试：Core 44 通过（含 9 个原版二进制差分向量）；Service 管道 1 通过。

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

- 新代码服务名：`RuijieCampusAuth`。
- 已部署的旧服务仍是 `HXXY-CampusAuth`；**重新 `service install` 后才会迁移**
  （安装时会删除旧服务）。
- LocalSystem，自动启动，`depend=npcap`，SCM 恢复 5s/10s/30s。
- Linux unit：`ruijie-campus-auth.service`。

Deep Freeze：

- 程序与数据放 D:；环境变量放用户级。
- 不要在冻结的 C: 上部署二进制/配置；C: 只保留冻结基线内容。

## 5. 配置档案（多学校）

- 目录：程序目录 `profiles/` 或当前目录 `profiles/`。
- CLI：`profile list|show|import [--root <安装目录>]`。
- GUI：`档案` 页选择后“导入/切换”。
- 导入只覆盖档案声明字段；账号、网卡、接管、Guardian 不变；自动备份
  `settings.json.bak-<时间戳>`。
- 当前部署已导入 `profiles/hxxy.json`：`profile_name=厦门华厦学院`、
  `service_name=电信宽带服务`、`static_id=Static:AB45A862`、`school_code=HPVTC`。
- OpenWrt 端用同一份档案：`ruijie-auth profile import --file hxxy.json`
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
  MSG_CLIENT_PORT=138、INDICATE_SERV_IP=172.22.11.1 与 dircomm 一致。
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
- amd64 原生与 mipsle（`qemu-mipsel`）`ruijie-auth selftest` 通过。
- ipk 构建通过；R3G 整机镜像 8.8MB，SHA256
  `54c0f8981253efabb577079b6b6cb45f97d7dd0a50731e9fac2077167afec5c7`，
  rootfs 含 `ruijie-auth` 包、8 个 LuCI 视图、`ruijie-auth.zh-cn.lmo`。
- `scripts/po2lmo.py` 生成的 `.lmo` 与 24.10 官方
  `https-dns-proxy.zh-cn.lmo` 逐键值一致（154/154）。
- x86_64 OpenWrt QEMU 冒烟：`selftest` 通过；`interfaces` 将 br-lan 标为
  WAN；daemon 在 br-lan 上完成 Logoff→4×Start→paused；`profile import`
  写入 8 字段并生成 backup，reload 后状态显示 profile；LuCI 页面 200、
  `lang_zh-cn`、translations 端点返回「锐捷认证器」等词条。
- 未验证：真实校园网 WAN 上的端到端认证、真实 RADIUS 上的 PEAP。

## 8. 已知问题 / 限制

1. PEAP/EAP-TLS 没有真实 RADIUS 端到端验证；证书策略待确认。
2. WPA2-Enterprise 只验证列表，创建/连接需实验 AP。
3. SAM 报文布局未复现，只有探针/DES 工具；SAM key/IV 来源已实证
   （ENCRYPT_KEY/ENCRYPT_IV 属性）。
4. `password-change`、`config-update` 未实现（无证据）。
5. PEAP vendor segments 中 6 个未还原段函数（FUN_0051a510/00518bc0/
   0051bec0/0051bf40/0051bdb0/0051c020）待补；0x5F 凭据段已锁定。
6. 原版 GetClientVersion/GetMACAddr/GetIPv4Info 需要设备/全局版本状态，
   Unicorn 无法构造（GetServiceName/GetDHCPAuthPhase 已差分）。
7. GUI 单实例互斥锁：若生产 GUI 在运行，开发构建会静默退出（正常）。
8. 新服务名尚未部署；当前机器仍运行旧 `HXXY-CampusAuth` 服务与旧二进制。
9. MSCHAPv2：C# 通过 RFC 2759 官方向量；原版 CEapPEAPThread 内部函数
   依赖 TLS 引擎状态，无法零状态直调，未做二进制差分。
10. C# 缺 MSCHAPv2 AuthenticatorResponse（S=）校验（服务端 Success
    消息验证）——待补。

## 9. 安全/运维注意

- 凭据：账号密码 DPAPI；调试日志对 password/token/key/iv 脱敏。
- 接管：IFEO `RuijieTakeover`（兼容旧 `HXXYTakeover`）；服务名迁移会删除旧服务。
- 协议实验默认关闭，手动触发；DHCP release/无线变更需要确认。
- 账号重试有限制，避免触发服务器锁定。
- 日志轮转：服务 10MB、GUI 5MB、debug 10MB。

## 10. 文档索引

| 文档 | 内容 |
|---|---|
| `docs/REVERSE_ENGINEERING.md` | 官方客户端完整逆向报告 |
| `docs/DIFFERENTIAL_TESTING.md` | Unicorn 直接调用 + 差分方法与结果 |
| `docs/ADAPTATION.md` | 新学校适配、增量 provider、算法切换规则 |
| `docs/GENERICIZATION.md` | 通用化改造与延期项 |
| `docs/protocols/*.md` | 各协议证据、字段、实现、未知项 |
| `docs/OFFICIAL_CLIENT_FEATURE_MATRIX.md` | 功能矩阵与落位 |
| `docs/OPENWRT_PORT.md` | C# → OpenWrt 移植对照与验证 |
| `docs/FROZEN_DEPLOYMENT.md` | 冻结环境部署历史 |
| `profiles/README.md` | 档案字段与导入 |

## 11. Git

- 分支：`feat/debug-protocols`
- 最近提交：

```
dc7f363 docs: record the OpenWrt port mapping and verification
f624c36 feat: rebuild LuCI for the authenticator with a zh-cn catalog
56fa153 feat: port the C# protocol core to the OpenWrt daemon
4ea64d1 test: verify C# algorithms against original binary functions
11efd64 feat: add multi-profile GUI switching and adaptation guide
df1723c feat: add wireless profiles, SAM probe, and upgrade check
8c513dd feat: add PEAP and EAP-TLS transport with phase2
5e91881 feat: implement MSCHAPv2 and EAP TLS stream bridge
22eee8b feat: add portal, client center, self-service, and message features
77ae8d3 feat: add diagnostics debug features
0054921 feat: add Windows DHCP control debug feature
d641329 feat: parse server private properties and honor reauth interval
ce6e31d refactor: genericize product defaults and add school profiles
a8e8b91 feat: add debug feature framework, service pipe, and debug page
```

## 12. 下一步

1. PEAP 完整 vendor segment：补齐 6 个未还原段函数
   （FUN_0051a510/00518bc0/0051bec0/0051bf40/0051bdb0/0051c020）。
2. 实验 RADIUS/AP 验证 PEAP 与 WPA2-Enterprise。
3. 抓取真实 SAM 交互，用实证的 key/IV（ENCRYPT_KEY/ENCRYPT_IV）补
   `SamDirectClient` 报文编解码。
4. 补 MSCHAPv2 AuthenticatorResponse（S=）校验。
5. 部署新构建：停旧服务/GUI → 复制到 D: → `service install`（迁移服务名）。
6. OpenWrt 现场验证：刷入 R3G 镜像，导入学校档案，确认概览页
   properties 与桌面版一致；PEAP 学校补一次隧道抓包。
7. Python 通用化（延期）。

差分校对工具链：`tools/re_diff/full_diff.py`（Unicorn 直调原版）+
`tools/csharp_diff/`（C# DiffRunner）；差分原始数据在
`build/capture/`（live-auth.pcap、original-parse-truth.json、
tail-compare.json、full-diff-report.json）。
