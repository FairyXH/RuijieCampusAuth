# 锐捷校园网开放认证器

通用的锐捷校园网第三方认证客户端，包含两套完整实现。学校差异通过 `profiles/`
配置档案导入，程序本身不内置任何学校配置。

| 目录 | 版本 | 形态 |
| --- | --- | --- |
| 仓库根目录 | **C# / .NET 10** | Core + CLI + Windows service / Linux systemd + Avalonia GUI |
| `python/` | Python 3.10+ | CLI + Python API + PySide6 GUI + 单文件 exe |

两套版本使用相同的 EAP-MD5 + 569 字节锐捷私有尾协议。C# Core 的私有尾已与
Python 版逐字节比对（569 字节，SHA256 一致），并已完成实网认证验证。

## C# 版本

学校适配：配置放在 `profiles/*.json`，通过 CLI `profile import` 或 GUI「档案」
页导入/切换。新增学校/算法时按 `docs/ADAPTATION.md` 做增量 provider，不修改
既有实现。

### 项目结构

```text
RuijieCampusAuth.slnx
RuijieCampusAuth.Core/       协议、私有尾、EAPOL、Npcap/libpcap、配置、服务安装器
RuijieCampusAuth.Cli/        跨平台命令行
RuijieCampusAuth.Service/    Windows 服务 / Linux systemd 的 headless worker
RuijieCampusAuth.App/        Avalonia 跨平台 GUI
RuijieCampusAuth.Core.Tests/ 协议单元测试
build-csharp.ps1             发布/部署脚本
```

### 构建

需要 .NET 10 SDK；运行需要 .NET 10 Desktop Runtime（Windows）或 .NET 10 Runtime
（Linux）。默认发布为 framework-dependent，不带自包含运行时。

```powershell
dotnet build RuijieCampusAuth.slnx -c Release
dotnet test RuijieCampusAuth.Core.Tests/RuijieCampusAuth.Core.Tests.csproj -c Release
.\build-csharp.ps1 -Runtime win-x64
```

发布目录：`publish\win-x64`，包含：

- `RuijieCampusAuth.exe` — Avalonia GUI / 托盘
- `RuijieCampusAuth.Cli.exe` — CLI
- `RuijieCampusAuth.Service.exe` — 系统服务宿主

### 部署

```powershell
.\build-csharp.ps1 -Runtime win-x64 -Deploy
```

部署目录固定为 `D:\Storageredirect\Program\RuijieCampusAuth`。GUI / 托盘启动命令：

```powershell
"D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" --tray --auto
```

### CLI

```powershell
# 直接认证（读取安装目录 accounts.json/settings.json；也可显式传参数）
RuijieCampusAuth.Cli.exe auth --interface "\Device\NPF_{GUID}"

# 初始化服务配置（写入程序目录 service.json）
RuijieCampusAuth.Cli.exe config init --username USER --password PASS --interface "\Device\NPF_{GUID}"

# 接管模式（临时阻止原版锐捷进程/服务）
RuijieCampusAuth.Cli.exe config takeover on
RuijieCampusAuth.Cli.exe config takeover off

# 系统服务
RuijieCampusAuth.Cli.exe service install
RuijieCampusAuth.Cli.exe service status
RuijieCampusAuth.Cli.exe service stop
RuijieCampusAuth.Cli.exe service uninstall
```

### 系统服务

- **Windows**：服务名 `RuijieCampusAuth`，`LocalSystem`，自动启动，登录前运行。
  服务配置、GUI 账号、设置、发现缓存和服务器列表全部在程序目录直读：
  `service.json`、`accounts.json`、`settings.json`、`discovery.json`、
  `servers.json`。服务优先使用 `settings.json` + `accounts.json` 的账号池，
  当前账号失败后自动尝试其他已启用账号；`service.json` 作为回退并把密码用
  LocalMachine DPAPI 加密。
- **Linux**：`service install` 写入
  `/etc/systemd/system/ruijie-campus-auth.service`，`Type=notify`，由
  `Microsoft.Extensions.Hosting.Systemd` 对接，`systemctl enable --now`。
- **macOS**：尚未实现 launchd 包装，CLI/服务 Core 可运行，GUI 使用 Avalonia。

### 家庭网络 / 无认证 LAN

认证失败且完全没有收到认证服务器响应（`Supplicant.Connected == false`）时，
服务会用 Guardian（启用时）或 TCP 连通性检测判断当前网络是否已经在线：

- 在线：进入 `paused`，日志为
  `未检测到认证服务器，当前网络在线，暂停认证`，每 30 秒复查一次，不再持续
  发送 EAPOL。
- 离线：恢复自动认证，回到校园网后会重新探测并认证。
- 暂停期间接管仍然生效，原版锐捷继续被阻止；需要在家庭网络使用原版客户端时，
  先关闭接管或停止服务。
- USB 网卡被拔掉导致的网卡异常也会被捕获，不会让服务退出。

### 冰点还原与更新

- 当前已完成一次“C: 冻结 + 重启”验收：VBS 哈希、`RuijieCampusAuth` 计划任务、服务
  `depend=npcap`/恢复策略、7 个 IFEO 标记、`RJSuService` 状态和 D: 配置均保留；
  服务/GUI 自动启动并认证成功。
- C: 冻结、D: 可写时，程序更新只需替换
  `D:\Storageredirect\Program\RuijieCampusAuth` 下的文件；先停服务和 GUI，
  再启动即可，不需要解冻。
- `C:\Program Files\restart_ruijie.vbs`、计划任务 `RuijieCampusAuth`、服务注册、
  .NET 10、Npcap 和快捷方式都在 C: 上；修改这些内容需要解冻，或在冻结前
  写入基线。
- 冻结期间不要运行 `build-csharp.ps1 -Deploy`（它会改写 C: 上的 VBS 和
  快捷方式）；只复制 `publish` 目录到 D:。
- 若长期开启接管，建议冻结前把 `RJSuService` 的基线启动类型设为 Disabled，
  避免开机时它先于 `RuijieCampusAuth` 启动。
- 服务已配置 `depend= npcap` 和崩溃恢复（5s/10s/30s）；服务日志 10MB、
  GUI 日志 5MB、VBS `launcher.log` 1MB 自动轮转。
- 冻结前建议做一次重启验收：在 D: 和 C: 各放一个标记文件，冻结重启后确认
  D: 文件保留、C: 文件消失，服务/GUI 正常，`launcher.log` 有
  `healthy takeover; skip original`。同时在 DiskGuard 界面确认 D: 未被保护。

### 接管模式

开启后由服务层临时执行：

- 扫描 `C:\Program Files\Ruijie Networks\Ruijie Supplicant` 下的 `.exe`，按
  **进程名**匹配（不写死路径）；
- 对枚举到的进程名写入 64/32 位 IFEO `Debugger` + `RuijieTakeover` 标记（兼容旧版本标记），并持续
  结束同名进程；
- 枚举 `ImagePath` 指向该目录、或显示名含 Ruijie/锐捷的服务，停止并临时设为
  disabled；
- 每秒写 `takeover.ini` 心跳，供 `restart_ruijie.vbs` 判断服务是否健康；
- 关闭接管或服务停止时，恢复原 IFEO 值和服务启动类型。

`7z.exe`、`unins000.exe` 不做阻止，避免误伤通用工具。

### GUI

Avalonia 跨平台 GUI，包含五个页签：

- **账号**：账号池、添加/更新/删除、启用/停用、设为当前账号。
- **认证**：开始/停止、抢占保活、实时日志。
- **服务器**：自动学习的认证服务器、成功后计数、锁定/解锁、删除。
- **服务**：接管开关、安装/启动/停止/卸载、应用服务配置。
- **设置**：网卡、服务名、静态 ID、Start 参数、DHCP/学校代码、服务器锁定、
  发现缓存、Guardian URL/间隔/离线确认。

Windows 启动使用 `--tray --auto`，支持托盘显示/退出。

### 运行数据

GUI、CLI 和服务都直读安装目录，不再使用 `%APPDATA%` 运行：

- `accounts.json` — 账号池，密码使用机器 DPAPI，服务可解密
- `settings.json` — GUI/服务运行参数
- `service.json` — 服务回退配置和接管开关
- `discovery.json`、`servers.json` — 探测缓存和服务器列表
- `service_status.json`、`takeover.ini`、`takeover_state.json`
- `logs\service.log`、`logs\csharp-gui.log`、`launcher.log`

### 冻结部署手册

`C:` 冻结、只更新 `D:` 的完整流程、冻结前检查、重启验收、更新步骤和故障
排查见 [`docs/FROZEN_DEPLOYMENT.md`](docs/FROZEN_DEPLOYMENT.md)。

## Python 版本

完整 Python 实现、GUI、单文件构建和已有文档在 [`python/`](python/README.md)。
Python 版本仍然保留，作为协议参考和回退方案。

## 协议要点

- EAPOL length 只覆盖标准 EAP 包；569 字节私有尾附加在 EAPOL length 之外。
- Start/Logoff 也带私有尾；Logoff 发往标准 PAE 组播，Start 可单播到锁定服务器。
- 0x39 服务名、0x54 静态 ID 可配置；0x2F 凭据使用 MD5 链式 XOR 加密。
- 私有尾长度 569 字节；C# 测试包含与 Python 参考实现的 SHA256 比对。

仓库不包含任何账号或密码。`service.json`、GUI 配置和日志均不入库。
