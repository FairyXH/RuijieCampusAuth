# HXXY Ruijie Campus Auth

厦门华厦学院锐捷校园网认证工具，包含两套完整实现：

| 目录 | 版本 | 形态 |
| --- | --- | --- |
| 仓库根目录 | **C# / .NET 10** | Core + CLI + Windows service / Linux systemd + Avalonia GUI |
| `python/` | Python 3.10+ | CLI + Python API + PySide6 GUI + 单文件 exe |

两套版本使用相同的 EAP-MD5 + 569 字节锐捷私有尾协议。C# Core 的私有尾已与
Python 版逐字节比对（569 字节，SHA256 一致），并在 HXXY 实网认证成功。

## C# 版本

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

- **Windows**：服务名 `HXXY-CampusAuth`，`LocalSystem`，自动启动，登录前运行。
  服务配置、GUI 账号、设置、发现缓存和服务器列表全部在程序目录直读：
  `service.json`、`accounts.json`、`settings.json`、`discovery.json`、
  `servers.json`。`service.json` 密码使用 LocalMachine DPAPI 加密。
- **Linux**：`service install` 写入
  `/etc/systemd/system/hxxy-campus-auth.service`，`Type=notify`，由
  `Microsoft.Extensions.Hosting.Systemd` 对接，`systemctl enable --now`。
- **macOS**：尚未实现 launchd 包装，CLI/服务 Core 可运行，GUI 使用 Avalonia。

### 接管模式

开启后由服务层临时执行：

- 扫描 `C:\Program Files\Ruijie Networks\Ruijie Supplicant` 下的 `.exe`，按
  **进程名**匹配（不写死路径）；
- 对枚举到的进程名写入 64/32 位 IFEO `Debugger` + `HXXYTakeover` 标记，并持续
  结束同名进程；
- 枚举 `ImagePath` 指向该目录、或显示名含 Ruijie/锐捷的服务，停止并临时设为
  disabled；
- 每秒写 `takeover.ini` 心跳，供 `restart_ruijie.vbs` 判断服务是否健康；
- 关闭接管或服务停止时，恢复原 IFEO 值和服务启动类型。

`7z.exe`、`unins000.exe` 不做阻止，避免误伤通用工具。

### GUI

Avalonia 跨平台 GUI，包含账号、认证、服务、设置四个页签；服务页支持安装/卸载/
启动/停止和接管模式开关。Windows 启动使用 `--tray --auto`，支持托盘。

## Python 版本

完整 Python 实现、GUI、单文件构建和已有文档在 [`python/`](python/README.md)。
Python 版本仍然保留，作为协议参考和回退方案。

## 协议要点

- EAPOL length 只覆盖标准 EAP 包；569 字节私有尾附加在 EAPOL length 之外。
- Start/Logoff 也带私有尾；Logoff 发往标准 PAE 组播，Start 可单播到锁定服务器。
- 0x39 服务名、0x54 静态 ID 可配置；0x2F 凭据使用 MD5 链式 XOR 加密。
- 私有尾长度 569 字节；C# 测试包含与 Python 参考实现的 SHA256 比对。

仓库不包含任何账号或密码。`service.json`、GUI 配置和日志均不入库。
