# RuijieCampusAuth

锐捷（Ruijie）校园网 802.1X 认证协议的第三方开源实现。项目以锐捷官方
Windows 客户端 `8021x.exe` 的协议行为为参照，提供 C#/.NET 10 参考实现、
Python 协议参考实现，以及 OpenWrt 上的 Go + LuCI 部署形态。学校差异通过
外部 JSON 配置档案注入；仓库附带已核对的正式档案，软件本体不写死
任何学校参数。

**关键词**：IEEE 802.1X；EAP-MD5；PEAP；MSCHAPv2；EAPOL；锐捷私有协议；
OpenWrt；协议互操作

## 摘要

本项目研究并复现锐捷校园网客户端在实际认证过程中使用的 802.1X/EAPOL
协议、569 字节私有尾、服务器私有属性分段格式、PEAP/MSCHAPv2 隧道与
EAP-TLS 握手，并将这些行为实现为可独立运行的客户端。实现遵循“协议兼容、
行为可验证、学校配置外置”的原则：不修改服务器行为，不绕过认证流程，
不把学校代码、服务名、静态 ID 或服务地址编译进二进制。

协议正确性通过三类证据交叉验证：一是用 Unicorn 直接调用原版二进制中的
算法函数做逐字节差分；二是使用真实服务器接受的认证抓包重建私有尾和
私有属性解析结果；三是各实现自身的单元测试、协议向量测试与 OpenWrt
运行验证。仓库同时记录未闭环的验证项，避免把“已实现”表述为“已现场验证”。

## 1. 目标与范围

### 1.1 目标

- 复现锐捷官方客户端在 802.1X 有线认证中的协议行为。
- 提供协议层、状态机和配置模型的清晰边界，便于逐函数对照与移植。
- 支持多学校适配：认证方式、服务名、静态 ID、Portal 地址等由 JSON
  档案提供，不写死在二进制中。
- 在 Windows、Linux 与 OpenWrt 上提供可部署形态，并保持协议语义一致。

### 1.2 非目标

- 不绕过、不破解、不修改校园网认证服务器或其策略。
- 不复制官方客户端的界面皮肤、弹窗、公告、托盘、安装器等非协议组件。
- 不内置任何学校的账号、密码或登录凭据；学校协议参数以独立 JSON 档案
  提供，可替换。
- 不把未经验证的协议行为包装成“已支持”；未知项在文档中显式标注。

## 2. 系统架构

项目由三个实现形态共享同一套协议语义与档案格式：

| 层次 | C# 参考实现 | Python 参考实现 | OpenWrt 实现 |
|---|---|---|---|
| 语言/运行时 | C# / .NET 10 | Python 3.10+ | Go 1.25 |
| 二层传输 | Npcap | scapy | AF_PACKET |
| TLS 载体 | `SslStream` | `ssl` + `MemoryBIO` | `crypto/tls` |
| 配置 | `settings.json` + 档案 | `service.json` + 档案 | UCI + 档案 |
| 界面 | Avalonia GUI、Windows 服务、CLI | 数字菜单 CLI、argparse | LuCI 8 页 |
| 主要平台 | Windows | Windows / Linux / macOS | OpenWrt 24.10 |

### 2.1 协议核心

协议层按职责划分为以下边界，C#、Python 与 Go 实现保持同名或同职责函数：

- EAPOL 组帧、Start/Logoff、Identity 与 MD5 响应。
- 569 字节私有尾：私有头、CRC16/XMODEM、字节位反转混淆、属性集、
  MD5 链式凭据加密。
- 服务器私有属性：GBK 消息段、0x6F 固定块、版本门、15 字节解混淆段、
  TLV 循环与 RC4 字段。
- PEAP v0/v1 与 EAP-TLS：TLS 分片、ACK、phase2 状态机、MSCHAPv2。
- 认证服务器学习、锁定与重认证调度。

### 2.2 传输层

C# 使用 Npcap 收发 Ethernet 帧；Python 使用 scapy；OpenWrt 使用 Linux
AF_PACKET 原始套接字。三者都把私有尾放在 EAPOL 长度字段之外，保持与
官方客户端一致的帧结构。

### 2.3 配置模型

学校差异统一放在 `profiles/*.json`，每校一个 `<学校>.json`，字段包括认证
方式、服务名、静态 ID、Portal/自助/升级地址、DHCP 模式等。仓库附带一份
已核对的正式档案；目录、字段与导入方法见
[`profiles/README.md`](profiles/README.md)。C# 与 OpenWrt 使用同一份
schema，导入时只覆盖档案声明的字段，并保留账号与未知配置项。

### 2.4 部署形态

- **C#**：Avalonia GUI、Windows 服务宿主与 CLI，覆盖 Windows 专有的
  DHCP 控制、WLAN 配置、接管策略与调试管道。
- **Python**：面向协议研究、脚本与嵌入式场景的数字菜单 CLI，保留
  argparse 子命令与 Python API。
- **OpenWrt**：Go 守护进程 + LuCI 管理页，使用 UCI 保存设置与账号，
  不绑定具体机型，WAN 口可自动识别或多选。

## 3. 协议覆盖与实现状态

| 协议/功能 | 实现 | 验证状态 |
|---|---|---|
| EAPOL Start/Logoff、EAP-Identity、EAP-MD5 | C# / Python / OpenWrt | 已实现；私有尾与真实抓包逐字节一致 |
| 569 字节私有尾（私有头、CRC16、混淆、凭据加密、属性集） | C# / Python / OpenWrt | 已实现；原版二进制差分 + 真实抓包三类尾包校验 |
| 服务器私有属性（分段格式、GBK 消息、0x6F 块、版本门、混淆段、TLV、RC4） | C# / Python / OpenWrt | 已实现；原版解析器差分 + 真实抓包字段校验 |
| PEAP v0/v1 + MSCHAPv2（含 MD4、DES、ChallengeHash、NT-Response） | C# / Python / OpenWrt | 已实现；RFC 2759 向量与分片测试；真实 RADIUS 未验证 |
| EAP-TLS | C# / Python / OpenWrt | 协议栈已实现；需客户端证书，OpenWrt 暂未开放证书配置入口 |
| DHCP 续租/释放、Option 82 | C#（Windows API） | 已实现；OpenWrt 仅记录 `dhcp_mode`，不驱动 udhcpc |
| Portal / eportal、自助服务、客户端中心、升级检查 | C# / Python | 已实现；不属于 802.1X 认证核心，OpenWrt 未移植 |
| 无线 WPA2-Enterprise 档案管理 | C#（Windows WLAN API） | 已实现；需要真实 AP 验证 |
| SAM 直通 | C# / Python | 探针级；认证下发的 `ENCRYPT_KEY`/`ENCRYPT_IV` 已实证 |
| 诊断（OUI、ARP、代理、拨号、网卡热插拔） | C# / Python | 已实现；OpenWrt 未移植 |
| 账号池、服务器学习/锁定、守护/抢占、档案导入 | C# / Python / OpenWrt | 已实现 |
| `password-change` / `config-update` 端点 | 未实现 | 缺少协议证据，未做猜测性实现 |

## 4. 实现矩阵

| 能力 | C# | Python | OpenWrt |
|---|---|---|---|
| EAP-MD5 有线认证 | 是 | 是 | 是 |
| 私有尾 / 私有属性 | 是 | 是 | 是 |
| PEAP + MSCHAPv2 | 是 | 是 | 是 |
| EAP-TLS | 是 | 是 | 协议栈是，证书配置否 |
| GUI | Avalonia | 无 | LuCI |
| Windows 服务 | 是 | 实验性 | 不适用 |
| systemd 服务 | 不适用 | 实验性 | procd |
| DHCP 控制 | Windows API | Windows API | 仅记录 |
| 无线档案 | Windows API | netsh | 未移植 |
| Portal/SAM/升级 | 是 | 是 | 未移植 |
| 账号池 | 是 | 是 | 是（最多 5 个） |

## 5. 验证方法

### 5.1 原版二进制差分

`tools/re_diff` 使用 Unicorn 映射原版 `8021x.exe`，按 RVA 直接调用算法函数，
与 C#/Python/Go 实现做同输入、同输出比较。已覆盖 CRC16、字节混淆、私有头
构建、MD5、凭据加密等函数；调用约定、序号导入与 CRT 字符串布局的差异在
`docs/DIFFERENTIAL_TESTING.md` 中记录。

### 5.2 真实抓包向量

使用真实服务器接受的一次认证抓包，提取 EAPOL-Start、Identity、MD5 Response
三类 569 字节私有尾，以及 EAP-Success 中的服务器私有属性块，对实现做逐字节
比对。当前结果为：

- 三类私有尾均 569/569 字节一致。
- 私有属性块的消息、段 2 值、段 5 hash/interval、全部 TLV 与 RC4 key/IV
  与原版解析器一致。
- Start 使用会话 nonce + 全零 fingerprint；Identity 使用会话 nonce +
  随机 fingerprint；MD5 Response 使用随机 nonce/fingerprint 与 challenge IV。

### 5.3 单元测试与集成测试

- C#：`RuijieCampusAuth.Core.Tests`、`App.Tests`、`Service.Tests`，包含
  协议向量、档案导入、服务状态与 UI 命令级测试。
- Python：`python/tests`，覆盖私有尾、私有属性、RC4、MSCHAPv2、档案、
  账号存储、菜单与 Guardian。
- OpenWrt：`go test ./...`，覆盖协议向量、私有属性、PEAP 分片、状态机、
  TLS 桥接并发关闭与配置解析。

### 5.4 OpenWrt 运行验证

- amd64 与 mipsle 二进制均可运行 `selftest`。
- x86_64 OpenWrt QEMU 中验证守护进程打开 AF_PACKET、完成
  Logoff → Start 重试 → 无服务器暂停的生命周期。
- 构建 ipk 与 R3G sysupgrade/Breed 镜像，校验 rootfs 内含守护进程、8 个
  LuCI 视图与中文 `.lmo`。
- LuCI 运行时目录按 hash 校验菜单标题与 8 个页面词条，应用自身词条
  覆盖率 100%。

### 5.5 未验证项

- 真实校园网 WAN 上的 EAP-MD5 上线（需要现场账号与网络环境）。
- 真实 RADIUS 服务器上的 PEAP/EAP-TLS 端到端握手。
- EAP-TLS 的客户端证书配置与证书链策略。
- PEAP vendor segment（0x5F 凭据段等）在活动会话中的发送行为；当前与
  C#/Python 保持一致，未在活动路径发送。
- 更老内核版本与非常规 OpenWrt 定制内核的兼容性。

## 6. 仓库结构

```text
RuijieCampusAuth.Core/        协议、私有尾、解析器、配置、服务安装、接管
RuijieCampusAuth.Service/     Windows 服务宿主（认证循环/日志/调试管道）
RuijieCampusAuth.App/         Avalonia GUI
RuijieCampusAuth.Cli/         命令行（auth/config/service/debug/profile）
RuijieCampusAuth.Core.Tests/  协议与核心逻辑测试
RuijieCampusAuth.App.Tests/   Headless UI 与命令级测试
RuijieCampusAuth.Service.Tests/
skills/                       Agent 端到端适配作业手册
tools/re_diff/                Unicorn 直调原版二进制的差分工具
tools/csharp_diff/            C# 侧差分对照器
profiles/                     学校配置档案（每校一份 JSON）
openwrt/                      OpenWrt 插件版源码（Go + LuCI）
python/                       Python 参考实现
docs/                         逆向、协议、适配、构建与操作文档
```

## 7. 构建与运行

### 7.1 C# / .NET

依赖 .NET SDK 10。以下命令均在仓库根目录执行：

```bash
dotnet build RuijieCampusAuth.slnx -c Release
dotnet test RuijieCampusAuth.Core.Tests -c Release
dotnet test RuijieCampusAuth.App.Tests -c Release
dotnet test RuijieCampusAuth.Service.Tests -c Release
powershell -ExecutionPolicy Bypass -File build-csharp.ps1 -Runtime win-x64
```

发布脚本将 Service、CLI 与 GUI 输出到 `publish/<runtime>/`。安装目录、
服务名与运行数据路径由使用者配置；程序本身不假定固定盘符或固定机器名。

### 7.2 Python

依赖 Python 3.10+ 与 `scapy`。二层收发需要 Npcap（Windows）或 libpcap
（Linux/macOS）及相应权限。

```bash
cd python
pip install -e .
python -m ruijie_auth          # 数字菜单
ruijie-auth --help             # argparse 子命令
PYTHONPATH=src python -m pytest -q
```

### 7.3 OpenWrt

构建需要 Linux 主机、Go 1.25、Python 3（生成 `.lmo`）以及可选的
OpenWrt Image Builder 24.10。脚本位于 `openwrt/scripts/`：

```sh
cd openwrt
sh scripts/build-go.sh       # 仅编译守护进程
sh scripts/build-ipk.sh      # 生成 ipk（含 LuCI 与中文 .lmo）
sh scripts/build-image.sh    # 生成整机固件（默认 R3G Image Builder）
```

插件本身是静态 Go 二进制，使用 AF_PACKET 与标准库，不依赖专属 kmod。
目标镜像中的 nftables kmod 来自 LuCI/firewall4 基础系统；帧伪装脚本在
缺少 `nft` 时会自动跳过，不影响认证。构建产物默认写入 `openwrt/build/`，
发布整理件不纳入版本控制。

### 7.4 配置与数据

- 学校档案：`profiles/*.json`，每校一个 `<学校>.json`；C# 与 OpenWrt
  共用同一 schema，仓库附带一份已核对的正式档案。
- C#：`settings.json`、`accounts.json`、`service.json`、`servers.json`、
  `discovery.json` 与日志目录位于安装目录。
- OpenWrt：`/etc/config/ruijie-auth`、`/etc/ruijie-auth/profiles/`、
  `/etc/ruijie-auth/servers.json`、`/var/run/ruijie-auth/status.json`。

## 8. 使用边界与合规

本项目与锐捷网络无隶属或授权关系，仅用于协议互操作研究与合法网络接入。
使用者应确保对目标校园网具有合法接入权限，并遵守所在学校的网络管理规定。
项目不提供绕过认证、伪造身份或规避审计的能力；认证失败原因、账号锁定与
网络策略均由服务器决定。

## 9. 文档目录

### 9.1 总览与适配

| 文档 | 内容 |
|---|---|
| [docs/AGENT_CONTEXT.md](docs/AGENT_CONTEXT.md) | 跨会话工程上下文、架构要点、已验证状态与已知限制 |
| [docs/OFFICIAL_CLIENT_FEATURE_MATRIX.md](docs/OFFICIAL_CLIENT_FEATURE_MATRIX.md) | 原版客户端功能拆解与 C# 落位矩阵 |
| [docs/ADAPTATION.md](docs/ADAPTATION.md) | 新学校适配规则：逆向 → 档案 → 增量实现 |
| [docs/GENERICIZATION.md](docs/GENERICIZATION.md) | 通用化改造与学校配置外置方案 |
| [profiles/README.md](profiles/README.md) | 学校档案字段、正式档案与导入切换 |

### 9.2 实现手册

| 文档 | 内容 |
|---|---|
| [docs/MANUAL_CSHARP.md](docs/MANUAL_CSHARP.md) | C# 版操作手册：安装、GUI、服务、CLI、接管与排错 |
| [docs/MANUAL_PYTHON.md](docs/MANUAL_PYTHON.md) | Python 版操作手册：环境、菜单、CLI、API 与服务 |
| [docs/MANUAL_OPENWRT.md](docs/MANUAL_OPENWRT.md) | OpenWrt 版操作手册：刷机、配置、CLI 与编译 |
| [python/README.md](python/README.md) | Python 实现说明、依赖与模块边界 |
| [openwrt/README.md](openwrt/README.md) | OpenWrt 工程说明、目录结构与设计约束 |

### 9.3 协议与逆向

| 文档 | 内容 |
|---|---|
| [docs/REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) | 原版客户端逆向总报告：函数地址、调用链与证据 |
| [docs/DIFFERENTIAL_TESTING.md](docs/DIFFERENTIAL_TESTING.md) | Unicorn 直调原版二进制与差分方法、结果 |
| [docs/protocols/README.md](docs/protocols/README.md) | 逐协议档案索引与状态 |
| [docs/protocols/private-properties.md](docs/protocols/private-properties.md) | 服务器私有属性分段格式、TLV 与 RC4 |
| [docs/protocols/peap.md](docs/protocols/peap.md) | PEAP/EAP-TLS、MSCHAPv2 与 vendor segment |
| [docs/protocols/dhcp.md](docs/protocols/dhcp.md) | DHCP 控制与 Option 82 |
| [docs/protocols/portal.md](docs/protocols/portal.md) | Portal/eportal、自助服务与客户端中心 |
| [docs/protocols/sam-upgrade.md](docs/protocols/sam-upgrade.md) | SAM 直通与升级检查 |
| [docs/protocols/wireless.md](docs/protocols/wireless.md) | 无线 WPA2-Enterprise 档案管理 |
| [docs/protocols/diagnostics.md](docs/protocols/diagnostics.md) | OUI、ARP、代理、拨号与网络探测 |

### 9.4 OpenWrt 专项

| 文档 | 内容 |
|---|---|
| [docs/OPENWRT_PORT.md](docs/OPENWRT_PORT.md) | C# → OpenWrt 移植对照、行为对齐与验证 |
| [docs/OPENWRT_BUILD_ENV.md](docs/OPENWRT_BUILD_ENV.md) | 构建环境、ipk/镜像打包与 Breed 镜像流程 |
| [docs/OPENWRT_FEASIBILITY.md](docs/OPENWRT_FEASIBILITY.md) | OpenWrt 移植可行性评估与 R3G 专项 |

### 9.5 运维与工具

| 文档 | 内容 |
|---|---|
| [docs/FROZEN_DEPLOYMENT.md](docs/FROZEN_DEPLOYMENT.md) | 部署、升级与运行环境记录 |
| [skills/ruijie-adaptation/SKILL.md](skills/ruijie-adaptation/SKILL.md) | Agent 端到端适配作业手册 |
| [tools/re_diff/README.md](tools/re_diff/README.md) | Unicorn 直调原版函数差分工具的使用 |
