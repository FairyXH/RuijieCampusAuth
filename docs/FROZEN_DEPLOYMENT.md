# 冻结系统盘部署与更新手册

> 本文按当前服务名/任务名描述；若机器上仍保留迁移前的旧名称，执行命令时
> 替换为实际名称。

本文档适用于当前机器形态：

- Windows 系统盘 `C:` 由 DiskGuard/Laneye 长期冻结。
- 部署盘 `D:` 可写，程序固定放在
  `D:\Storageredirect\Program\RuijieCampusAuth`。
- Windows 服务 `RuijieCampusAuth`、登录任务 `RunHXXYNet` 和
  `C:\Program Files\restart_ruijie.vbs` 已写入 C: 冻结基线。
- GUI 固定启动命令：
  `"D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" --tray --auto`

## 1. 组件分工

### C: 冻结基线中的内容

| 内容 | 位置 | 冻结后的处理 |
| --- | --- | --- |
| 原版启动/接管判断脚本 | `C:\Program Files\restart_ruijie.vbs` | 只能解冻更新 |
| 登录任务 | `C:\Windows\System32\Tasks\RunHXXYNet` | 只能解冻重建/修改 |
| 服务注册、依赖、恢复策略 | `HKLM\SYSTEM\CurrentControlSet\Services\RuijieCampusAuth` | 不要卸载重装 |
| .NET 10 运行时 | `C:\Program Files\dotnet` | 当前程序依赖它 |
| Npcap | `C:\Windows\System32\Npcap`、驱动 `npcap` | 当前程序依赖它 |
| 冻结软件 | `C:\Program Files (x86)\Laneye\DiskGuard` | 不要改 |
| 网络管家 exe | `C:\Windows\Protect\NetworkGuardian.exe` | 其配置在 D: |
| 原版锐捷 | `C:\Program Files\Ruijie Networks\Ruijie Supplicant` | 接管时被 IFEO 阻止 |

### D: 可写内容

所有运行数据在安装目录，直读，不再使用 `%APPDATA%` 运行：

- `service.json` — 服务回退配置和接管开关
- `accounts.json` — 账号池（机器 DPAPI）
- `settings.json` — GUI/服务参数
- `discovery.json` — 认证通道缓存
- `servers.json` — 已学习认证服务器
- `service_status.json` — 服务运行状态
- `takeover_state.json` — 接管前 IFEO/服务原值备份
- `takeover.ini` — 服务心跳
- `logs\service.log` — 服务日志，10 MB 轮转
- `logs\csharp-gui.log` — GUI 日志，5 MB 轮转
- `launcher.log` — VBS 日志，1 MB 轮转

## 2. 冻结前检查

1. 在 DiskGuard 界面确认 `D:` 未受保护；不要只依赖“仅系统盘”的口头配置。
2. 确认 `D:` 是固定盘，且不会在 Windows 服务启动后才解锁（BitLocker 自动解锁要确认）。
3. 检查服务注册：
   ```powershell
   sc.exe qc RuijieCampusAuth
   sc.exe qfailure RuijieCampusAuth
   ```
   期望：`AUTO_START`、`LocalSystem`、`DEPENDENCIES: npcap`，
   `RESTART 5s/10s/30s`。
4. 检查 VBS：
   ```powershell
   Get-FileHash "C:\Program Files\restart_ruijie.vbs" -Algorithm SHA256
   ```
   当前冻结基线 SHA256：
   `D1833A456CAB97FA5EE2F652A92852AC8E5E392ED668B8A93AD6BFAEE77623EB`
5. 检查任务：
   ```powershell
   Export-ScheduledTask -TaskName RunHXXYNet
   ```
   期望：`wscript.exe "C:\Program Files\restart_ruijie.vbs"`，
   `HighestAvailable`，登录触发，无超时。
6. 检查 7 个官方 exe 的 IFEO 标记：
   `8021x.exe`、`RuijieSupplicant.exe`、`NetworkDetect.exe`、`snetcfg.exe`、
   `SoftwareManager.exe`、`SuInstaller.exe`、`suservice.exe`。
7. 决定冻结基线策略：
   - **长期开启接管**：保持 `RJSuService` Disabled、IFEO 标记存在。
     当前基线即此状态。
   - **需要随时关闭接管**：冻结前先关闭接管并确认 IFEO 标记清除；冻结后
     由服务在开机时重新应用。注意 `RJSuService` 的冻结基线仍可能是
     Disabled，若要在重启后长期使用原版客户端，需要解冻一次改回 Automatic。

## 3. 冻结后验收

按以下项逐条确认：

```powershell
Get-Service RuijieCampusAuth,RJSuService,npcap
Get-Process RuijieCampusAuth,RuijieCampusAuth.Service
sc.exe qc RuijieCampusAuth
sc.exe qfailure RuijieCampusAuth
Get-FileHash "C:\Program Files\restart_ruijie.vbs" -Algorithm SHA256
Get-ScheduledTaskInfo -TaskName RunHXXYNet
```

再检查运行数据：

```powershell
Get-Content "D:\Storageredirect\Program\RuijieCampusAuth\service_status.json"
Get-Content "D:\Storageredirect\Program\RuijieCampusAuth\launcher.log" -Tail 10
Get-Content "D:\Storageredirect\Program\RuijieCampusAuth\logs\service.log" -Tail 30
```

预期：

- `RuijieCampusAuth` Running / Automatic，`npcap` Running。
- `RJSuService` Stopped / Disabled（长期接管模式）。
- `service_status.json` 为 `authenticated`，服务器为 `00:1a:a9:17:ff:ff`。
- `launcher.log` 出现 `healthy takeover; skip original`。
- 7 个 IFEO 标记存在。
- 浏览器或 `Invoke-WebRequest http://www.baidu.com/` 返回 HTTP 200。

### 2026-11-18 21:04 实测快照（历史）

- 开机时间：`2026-11-18 20:54:42`
- 服务 PID 8580，GUI PID 21820，服务 Running，`npcap` Running。
- 当时 VBS 哈希为 `7E8258...E7C6`，任务结果最终 0；2026-11-20 已升级为
  `D1833A...23EB`（兼容 `RuijieTakeover` 与旧 `HXXYTakeover`）。
- 服务启动 `20:55:07`，认证成功 `20:57:09`。
- `RJSuService` Stopped / Disabled，7 个 IFEO 标记完整。
- `baidu HTTP 200`。

### 2026-11-20 17:00 冻结前复核

- VBS 已更新为双标记兼容版，C: 与仓库源文件 SHA256 均为
  `D1833A456CAB97FA5EE2F652A92852AC8E5E392ED668B8A93AD6BFAEE77623EB`。
- 7 个官方 exe 的 `RuijieTakeover=RuijieCampusAuth` 在 64/32 位视图均存在。
- 服务 PID 93512，GUI PID 92384；服务 Running/Automatic，`npcap` Running，
  `RJSuService` Stopped/Disabled。
- 手动触发 `RunHXXYNet`：LastTaskResult 0，`launcher.log` 新增
  `healthy takeover; skip original`，服务/GUI PID 未变化，官方客户端未启动。
- `takeover.ini` 持续心跳，`enabled=1`，`pid=93512`，指向服务镜像。
- 本机认证在线；`sc qc` 仍为 `AUTO_START`、`LocalSystem`、`DEPENDENCIES: npcap`，
  恢复策略仍为 5s/10s/30s。

## 4. 冻结期间的日常更新（只改 D:）

不要运行 `build-csharp.ps1 -Deploy`：它会改 C: 上的 VBS 和快捷方式，
冻结状态下这些改动重启后会丢失。只复制发布文件到 D:。

1. 停止进程守护，避免它拉起 GUI。
2. 停止 GUI 和服务：
   ```powershell
   Stop-Service RuijieCampusAuth
   Stop-Process -Name RuijieCampusAuth -Force
   ```
3. 备份 D: 配置（至少 `service.json`、`accounts.json`、`settings.json`）：
   ```powershell
   $dir = "D:\Storageredirect\Program\RuijieCampusAuth"
   $bak = Join-Path $dir ("config-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
   New-Item -ItemType Directory -Path $bak | Out-Null
   Copy-Item "$dir\*.json" $bak -Force
   ```
4. 将新 `publish\win-x64\*` 复制到 D:，不要覆盖或删除上述 JSON。
5. 启动服务：
   ```powershell
   Start-Service RuijieCampusAuth
   ```
6. 按固定命令启动 GUI：
   ```powershell
   Start-Process "D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" -ArgumentList '--tray','--auto'
   ```
7. 重复第 3 节的验收检查。

### 4.1 冻结基线兼容契约（后续版本必须保持不变）

| 项目 | 固定值 |
| --- | --- |
| 服务名 | `RuijieCampusAuth` |
| 显示名 | `Ruijie Auth` |
| 服务镜像 | `"d:\storageredirect\program\ruijiecampusauth\RuijieCampusAuth.Service.exe"` |
| 服务类型/启动 | `WIN32_OWN_PROCESS`、`AUTO_START`、`LocalSystem` |
| 依赖 | `npcap` |
| 失败恢复 | 5s / 10s / 30s，重置周期 86400s |
| GUI | `D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe --tray --auto` |
| 接管恢复入口 | `D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe --restore-client` |
| 登录任务 | `RunHXXYNet`（`C:\Windows\System32\Tasks\RunHXXYNet`） |
| 任务动作 | `C:\Windows\System32\wscript.exe "C:\Program Files\restart_ruijie.vbs"` |
| VBS | `C:\Program Files\restart_ruijie.vbs` |
| VBS SHA256 | `D1833A456CAB97FA5EE2F652A92852AC8E5E392ED668B8A93AD6BFAEE77623EB` |
| IFEO 标记 | `RuijieTakeover=RuijieCampusAuth`（VBS 同时兼容旧 `HXXYTakeover`） |

更新时只替换 D: 下的程序文件；C: 上的服务注册、VBS、计划任务和运行时
一律不动。不得改服务名、服务 exe 名、GUI exe 名、部署路径或 GUI 启动参数。
冻结后不要执行 `service uninstall` / `service install`，否则可能丢失
`depend=npcap` 和恢复策略。

更新约束：

- 不要改服务名、服务 exe 名、GUI exe 名或部署路径。
- 必须保留 `RuijieCampusAuth.exe --restore-client`，冻结的 VBS 会调用它。
- 新版本必须继续使用 .NET 10 框架依赖发布；如果必须升级运行时，需解冻；
  或改为自包含发布到 D:。
- 不要卸载重装服务；重装会丢失当前的 `depend=npcap` 和恢复策略。
- 如果更新失败，停服务/GUI，把备份文件复制回 D:，再启动。

## 5. 故障排查

### 服务没有启动

1. 检查 `npcap` 是否为 Running；服务依赖它。
2. 检查 D: 是否可访问。
3. 查看 `logs\service.log` 和 `service_status.json`。
4. `sc.exe qc RuijieCampusAuth` 确认 ImagePath 指向 D:。
5. 必要时手动 `Start-Service RuijieCampusAuth`。

### VBS 没有跳过原版

查看 `launcher.log`：

- `healthy takeover; skip original`：正常。
- `takeover exe missing; using direct IFEO cleanup`：D: 程序缺失，VBS
  正在直接清 IFEO。
- `restore incomplete; clearing IFEO directly`：VBS 兜底清理 IFEO。
- `launching original supplicant`：VBS 判定接管不健康，已走原版回退。

### 家庭网络下服务状态为 paused

这是预期行为：没有收到认证服务器响应，但 Guardian/TCP 检测到外网在线。
服务每 30 秒复查；回到需要认证的校园网后，外网离线会自动恢复认证。
需要强制立即重试时，重启服务即可。

### 需要在家使用原版锐捷

关闭接管（GUI 服务页或 CLI `config takeover off`）。注意冻结基线是
“长期接管”配置，重启后 IFEO 和 `RJSuService` 的基线策略会恢复；如果
需要重启后仍使用原版，必须解冻调整基线。

## 6. 相关文件

- `docs/AGENT_CONTEXT.md` — 跨会话工程交接状态
- `README.md` — 构建、CLI、GUI、服务概览
- `python/packaging/restart_ruijie.vbs` — VBS 源文件
- `build-csharp.ps1` — 完整发布/部署脚本（冻结期间不要运行 `-Deploy`）
