# Agent Context

> 跨会话交接文档。描述项目当前状态、架构、验证结果与下一步，不记录完整对话历史。
> 最后更新：2026-11-19

## 1. 项目是什么

通用的锐捷校园网第三方认证客户端（“锐捷认证器 / 锐捷校园网开放认证器”）。
使用官方协议做兼容认证，不依赖官方进程；学校差异通过 `profiles/*.json` 导入。
默认不内置任何学校配置。

两套实现：

- **根目录 C# / .NET 10**：Core + CLI + Windows 服务/Linux systemd + Avalonia GUI。
- **`python/`**：Python 参考实现（本轮未改动，后续通用化）。

Python 与 OpenWrt 的通用化延期；OpenWrt 当前不动代码，方案见
`docs/GENERICIZATION.md`。

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
openwrt/                     延期，不动
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
| password-change / config-update | 计划态（端点/字段无证据） |
| 小锐 WiFi 分享 | 计划态（驱动级） |

每个功能都有 `debug` 功能名，GUI「调试」页和 CLI `debug` 命令共用。

## 7. 已验证状态（2026-11-19）

- 私有属性解析：TLV/字段表；`ACCOUNT_INFO`、`INDICATE_PORT` 实测。
- DHCP：`GetInterfaceInfo` 适配器 index 14，租约读取成功。
- 诊断：OUI `00:1a:a9`；13 个主网卡；ARP 网关稳定；网关/DNS/HTTP 200；
  无常见代理端口；无活动拨号；医生管道不存在。
- Portal：本地 TCP 假服务器验证路径/JSON/Content-Type。
- SAM：本机 UDP echo `pong:00001311`，15ms。
- 升级：本机 HTTP 假服务 JSON 解析成功。
- 无线：WLAN 列表 1 接口 5 配置；未做 create/connect。
- 原版差分：CRC、混淆、头、头+混淆、MD5、凭据加密全部一致。
- PEAP/MSCHAPv2：RFC 2759 NT-Response 向量通过；TLS 流桥本机握手通过。

## 8. 已知问题 / 限制

1. PEAP/EAP-TLS 没有真实 RADIUS 端到端验证；证书策略待确认。
2. WPA2-Enterprise 只验证列表，创建/连接需实验 AP。
3. SAM 报文布局未复现，只有探针/DES 工具。
4. `password-change`、`config-update` 未实现（无证据）。
5. 完整 569 字节私有尾的动态全量比对未做；前 11 个属性顺序已确认。
6. 原版 `GetClientVersion/GetServiceName/GetMACAddr` 需要对象/设备状态，
   不能零状态直接调用。
7. GUI 单实例互斥锁：若生产 GUI 在运行，开发构建会静默退出（正常）。
8. 新服务名尚未部署；当前机器仍运行旧 `HXXY-CampusAuth` 服务与旧二进制。

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
| `docs/FROZEN_DEPLOYMENT.md` | 冻结环境部署历史 |
| `profiles/README.md` | 档案字段与导入 |

## 11. Git

- 分支：`feat/debug-protocols`
- 最近提交：

```
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

1. 完整私有尾动态比对：构造原版 `this` + 全局网络对象，调用 `0x53A590`，
   与 C# `BuildTail` 逐字节比较。
2. PEAP 完整 vendor segment：补齐 0x11/0x39/0x54 之外的 V3 hash、HDD SN、
   SecDomain、SecCheck 等字段。
3. 实验 RADIUS/AP 验证 PEAP 与 WPA2-Enterprise。
4. 抓取真实 SAM 交互，补 `SamDirectClient` 报文编解码。
5. 部署新构建：停旧服务/GUI → 复制到 D: → `service install`（迁移服务名）。
6. Python/OpenWrt 通用化（延期）。
