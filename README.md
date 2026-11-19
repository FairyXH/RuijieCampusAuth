# RuijieCampusAuth

与锐捷（Ruijie）官方认证协议兼容的第三方校园网认证客户端。C# / .NET 10 实现
（Avalonia GUI + Windows 服务/systemd + CLI），协议算法逐函数对照原版
`8021x.exe` 差分校对；学校差异全部走 JSON 配置档案，不内置任何学校数据。
Python 参考实现与 OpenWrt 插件版（Go + LuCI）在仓库内并列维护。

> 定位：协议兼容的"私服式"客户端——不绕过认证、不改服务器行为，
> 只把官方客户端里实际在用的协议与功能还原成开源实现。

## 功能一览

| 能力 | 状态 | 说明 |
|---|---|---|
| 有线 802.1X EAP-MD5 | ✅ 生产验证 | Start/Logoff/重认证/抢占保活/家庭网络暂停 |
| 认证方式分发 | ✅ | `auth_mode` 驱动 EAP-MD5 / PEAP v0 / PEAP v1 / EAP-TLS |
| 569 字节私有尾 | ✅ 原版差分一致 | CRC16 + 位反转混淆 + 18 个属性 + 凭据链式加密 |
| 服务器私有属性 | ✅ 原版差分一致 | 分段格式（GBK 消息/0x6F 块/版本门/15 字节解混淆段/TLV） |
| DHCP | ✅ | 认证前/后自动续租、释放、Option82（Windows API） |
| 账号池 | ✅ | 多账号、DPAPI 加密、失败切换、服务回退 |
| 服务器学习/锁定 | ✅ | 成功自动记录、单播锁定 |
| 诊断 | ✅ | OUI/网卡热插拔/ARP/网关 DNS HTTP 探测/代理/拨号 |
| Portal / 自助 / 客户端中心 / 用户消息 / 升级检查 | ✅ | eportal API + ball JSON |
| 无线 WPA2-Enterprise | ✅ | Windows WLAN API 配置管理 |
| SAM 直通 | 🔧 探针级 | key/IV 已实证复用认证下发的 ENCRYPT_KEY/IV |
| PEAP/TLS 端到端 | ⏳ | 协议栈完成，待真实 RADIUS 验证 |
| OpenWrt 插件 | ✅ | Go 守护进程 + LuCI（见下文） |

## 仓库结构

```text
RuijieCampusAuth.Core/        协议、私有尾、解析器、配置、服务安装、接管
RuijieCampusAuth.Service/     Windows 服务宿主（AuthWorker/日志/调试管道）
RuijieCampusAuth.App/         Avalonia GUI（账户/认证/网络/服务/配置/调试）
RuijieCampusAuth.Cli/         命令行（auth/config/service/debug/profile）
RuijieCampusAuth.Core.Tests/  65 项（含原版二进制差分锁定向量）
RuijieCampusAuth.App.Tests/   33 项（Headless UI + 命令级）
RuijieCampusAuth.Service.Tests/
skills/                       Agent 技能：逆向与学校适配一条龙（见下文）
tools/re_diff/                Unicorn 直调原版二进制的差分工具
tools/csharp_diff/            C# 侧差分对照器（DiffRunner）
profiles/                     学校配置档案（hxxy.json 为示例）
openwrt/                      OpenWrt 插件版源码（Go + LuCI）
python/                       Python 参考实现（协议研究用）
docs/                         逆向报告、协议档案、适配/部署/泛化文档
```

## 构建

依赖 .NET SDK 10（`dotnet --version` ≥ 10.0.1xx）。

```bash
# 全量构建
dotnet build RuijieCampusAuth.slnx -c Release

# 测试（三套件）
dotnet test RuijieCampusAuth.Core.Tests -c Release
dotnet test RuijieCampusAuth.App.Tests -c Release
dotnet test RuijieCampusAuth.Service.Tests -c Release

# 一键发布（Service + Cli + App → publish\win-x64）
powershell -ExecutionPolicy Bypass -File build-csharp.ps1 -Runtime win-x64
```

## 部署

发布脚本自带部署路径（会停止服务与 GUI、复制到目标目录、同步 profiles）：

```powershell
powershell -ExecutionPolicy Bypass -File build-csharp.ps1 -Runtime win-x64 -Deploy `
    [-DeployDir "D:\Storageredirect\Program\RuijieCampusAuth"]
```

服务管理（服务名保持安装时的名字，脚本不会迁移）：

```powershell
RuijieCampusAuth.Cli.exe service status|start|stop|install|uninstall
```

安装服务（LocalSystem、开机自启、登录前认证）：

```powershell
RuijieCampusAuth.Cli.exe service install
```

**Deep Freeze / 冻结还原机器**：程序与数据放非冻结盘（如 D:），C 盘只保留
冻结基线内容。部署脚本只写 D 盘，不会触碰 C 盘的启动器与服务注册。
运行时配置统一在安装目录：`settings.json` / `accounts.json` / `service.json` /
`discovery.json` / `servers.json` / `logs\`。

## 使用

### GUI（推荐）

窗口固定大小不可缩放，五个主类页签 + 调试页；每页常用功能置顶，
其余收进"高级功能"折叠区：

- **账户**：账号池管理；右侧账密表单下"添加"，"新建"清空表单
- **认证**：开始/停止、抢占保活、实时日志（`[服务]` 前缀行由服务实时同步，
  "详细信息"开关显示心跳类例行输出）
- **网络**：网卡选择、认证服务器学习/锁定、DHCP 高级设置
- **服务**：Windows 服务安装/启停、接管模式（阻止官方客户端运行）、Guardian 联动
- **配置**：学校配置档案多选导入/切换（导入即同步服务配置）、认证方式下拉
  （EAP-MD5 / PEAP / EAP-TLS）、协议参数高级区
- **调试**：21 项实验功能（PEAP/TLS、DHCP、ARP、Portal、SAM、无线、升级……），
  经命名管道在服务进程内执行，GUI 不在时回退本机执行

### CLI

```powershell
RuijieCampusAuth.Cli.exe auth start|stop|status
RuijieCampusAuth.Cli.exe profile list|show|import <json> [--root <安装目录>]
RuijieCampusAuth.Cli.exe debug list|run <feature> [--args '{}']
RuijieCampusAuth.Cli.exe service status|start|stop|install|uninstall
RuijieCampusAuth.Cli.exe config show
```

### 服务模式

安装后以 LocalSystem 在登录前认证；每轮重试重读配置，改动即生效；
支持账号池失败切换、服务器锁定、掉线检测、家庭网络暂停（无认证服务器
但 TCP 在线时暂停，恢复离线后自动续认证）。

## 用 Agent + Skills 完成原版逆向与本校适配（一条龙）

`skills/ruijie-adaptation/SKILL.md` 是给编码 Agent 的完整作业手册，覆盖
从原版客户端分析到本校上线的全流程：

1. **工具链**：REA/Ghidra/rizin 的本仓库用法与已积累的分析产物位置
2. **静态分析**：交叉引用、RTTI/日志锚点、已知核心函数地址表
3. **差分校对**：`tools/re_diff` + `tools/csharp_diff` 的 Unicorn 直调方法、
   调用约定陷阱（thiscall/stdcall/序号导入/CRT 字符串布局）与验收标准
4. **真实抓包验证**：引擎态函数用被动抓包 + pcapng 解析回放
5. **编写学校档案 JSON**：字段语义、取值来源（SuConfig.dat → 认证成功包 → 抓包）
6. **接入验收**：profile 导入、测试全绿、部署脚本、服务日志判定

配套工具都在仓库里：`tools/re_diff/full_diff.py`（Unicorn 批量差分）、
`tools/csharp_diff/DiffRunner`（C# 对照）、`docs/REVERSE_ENGINEERING.md`
（逆向总报告）、`docs/protocols/*.md`（逐协议档案）、`docs/ADAPTATION.md`
（适配规则）。

## OpenWrt 插件版

`openwrt/` 是软路由端实现，与 C#/Python 并列维护：

```text
openwrt/
  hxxy-auth/            Go 守护进程 + CLI（默认目标 mipsle）
  luci-app-hxxy-auth/   LuCI 应用（菜单/ACL/视图）
  files/                Image Builder 覆盖文件（UCI 配置）
  scripts/build-go.sh       交叉编译 Go 二进制
  scripts/build-image.sh    组装 files/ 并调用 OpenWrt Image Builder
```

- 源码不绑定设备：同 target（如 `ramips/mt7621`）只改 `PROFILE`，
  换架构改 `GOARCH` 重编，LuCI/UCI 不动
- 当前验证目标 Xiaomi R3G（R3Gv2 / 4A 千兆版同流程）
- **编译在 Ubuntu VM 进行**（Windows 不装 Go、不跑 Image Builder）：

```sh
git clone <本仓库> && cd RuijieCampusAuth/openwrt
sh scripts/build-go.sh
sh scripts/build-image.sh   # 产出 sysupgrade 镜像
```

细节与 R3G 专项参数见 `openwrt/README.md`、`docs/OPENWRT_FEASIBILITY.md`、
`docs/OPENWRT_BUILD_ENV.md`。

## Python 参考实现

`python/` 是最早的协议参考实现（逆向期的原型，协议行为与 C# 版一致，
569 字节私有尾两版逐字节一致）：

- 用途：协议研究、快速验证新想法、给 C# 版做行为对照
- 入口与 API 说明见 `python/README.md`；打包脚本 `python/build.ps1`
- **生产部署请用 C# 版**（服务模式/GUI/测试覆盖都在 C# 侧）；Python 版
  保留为研究与对照实现，功能更新已延后（见 `docs/GENERICIZATION.md`）

## 文档索引

| 文档 | 内容 |
|---|---|
| `docs/REVERSE_ENGINEERING.md` | 原版客户端完整逆向报告（函数地址表/调用链/差分结果） |
| `docs/DIFFERENTIAL_TESTING.md` | Unicorn 直调 + 差分方法与结果 |
| `docs/protocols/*.md` | 逐协议档案（私有属性/PEAP/DHCP/Portal/SAM/无线） |
| `docs/ADAPTATION.md` | 新学校适配规则（auth_mode + 版本选择 provider） |
| `docs/OPENWRT_FEASIBILITY.md` | OpenWrt 移植评估与 R3G 专项 |
| `docs/FROZEN_DEPLOYMENT.md` | 冻结还原机器的部署历史 |
| `profiles/README.md` | 学校档案字段与导入 |
| `skills/ruijie-adaptation/SKILL.md` | Agent 一条龙作业手册 |

## 许可与声明

本项目与锐捷网络无关联，仅作协议兼容与互操作研究用途。使用本项目
认证请确保你拥有校园网的合法接入权限并遵守所在学校网络管理规定。
