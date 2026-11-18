# Ruijie Campus Auth HXxy

> 这是 **Python 版本**，完整保留在 `python/`。仓库根目录是正在迁移的
> **C# / .NET 10** 版本（Core + CLI + Windows service / Linux systemd +
> Avalonia GUI）。两套版本的协议实现均已实网验证。

一个面向 Windows 和 Linux 的锐捷校园网认证客户端，同时提供命令行和 Python API。

> **当前状态：已在厦门华厦学院实网验证通过。** 2026-11-17 使用真实测试账号完成了
> 原版客户端抓包、私有尾逐字节重建和 Python 客户端登录。Python 客户端完成标准
> EAP-MD5 后会追加 569 字节锐捷私有尾，服务器返回 EAP-Success，日志显示
> “认证成功 / 欢迎使用厦门华厦学院网络”。桌面端和单文件 exe 见“GUI 应用”和
> “构建单文件 exe”。

## 解决的问题

学校提供的锐捷认证客户端只有 Windows 版本，Linux 用户无法使用同一套校园网账号
完成 802.1X 认证。本项目根据 Windows 锐捷客户端的真实网络行为实现兼容客户端，
保留学校服务器原有的账号密码认证方式，不绕过认证，也不修改认证结果。

当前网络实际使用以下流程：

1. 发送带私有尾的 EAPOL-Logoff，清理旧会话；
2. 向锐捷组播地址发送带私有尾的 EAPOL-Start；
3. 响应服务器的 EAP Identity 请求，并在标准 EAP 包后附加私有尾；
4. 使用原账号密码完成 EAP-MD5 Challenge，并在标准 EAP 包后附加私有尾；
5. 输出服务器返回的认证成功、认证失败、非认证时段等中文消息；
6. 将完整运行过程同时写入控制台和滚动日志。

## 锐捷私有尾

原版客户端在每个 EAPOL-Start / Logoff / Identity / MD5 应答帧后附加 569 字节
（IP 字符串为 11 字节时）的私有数据。标准 EAPOL 长度字段只覆盖标准 EAP 包，
私有尾位于 EAPOL 长度之外，服务器通过以太网帧总长度读取它。

私有尾结构：

- 23 字节私有头：`00 00 13 11` + 标志 + IPv4/掩码/网关/DHCP 四组地址 +
  CRC-16/XMODEM；整段再逐字节取反并翻转比特；
- 47 字节前导：`00 00 13 11` + `8021x.exe` + 版本 + 标志 + `00 00 13 11` +
  属性总长度；
- 0x17：32 字节随机十六进制 nonce；Start/Logoff/Identity 共用会话 nonce；
- 0x18：4 字节版本/状态；
- 0x2D：6 字节客户端 MAC；
- 0x2F：加密后的账号凭据；
- 0x35/0x36/0x38/0x4E/0x4D：系统、IPv6、指纹等字段；
- 0x39：服务器下发的服务名（本网络为“电信宽带服务”）；
- 0x54：静态机器标识（本机为 `Static:AB45A862`）；
- 0x62/0x6B/0x7E/0x70/0x6F/0x79/0x76：版本、策略、本机 IP 等字段。

0x2F 凭据块的加密方式是客户端自定义的 MD5 链式 XOR：

```text
cipher_block = plain_block XOR MD5(username || previous_block)
previous_block = IV（首块）或上一块密文
```

其中：

- key 为用户名；
- 明文为密码，按 16 字节零填充；
- Start / Logoff / Identity 使用 16 字节零 IV；
- MD5 应答使用服务器下发的 16 字节 challenge 作为 IV。

控制帧（Start/Logoff）同样携带私有尾，指纹字段为全零，前导标志为 `0x01`；
Identity / MD5 应答使用随机会话指纹。

## 特性

- Windows、Linux 共用同一套 Python 实现；
- 默认主动探测锐捷认证服务器，自动选择真正连接校园网的网卡；
- 支持按名称、描述或 Windows NPF 名称指定网卡；
- 完整复现锐捷 569 字节私有尾和自定义凭据加密；
- EAPOL 长度字段只覆盖标准 EAP 包，私有尾附加在长度之外；
- Windows 使用 Npcap 发送队列把控制帧/应答帧按原客户端的紧凑节奏发送，
  其他平台回退到 Scapy；
- 默认复现原客户端的三次应答行为和约 40ms 应答延迟；
- 原样显示锐捷扩展 Success/Failure 中的 GB18030 中文通知；
- 密码默认使用隐藏输入，也支持通过命令行参数传入；
- 账号密码不会写入本工具日志；
- `--no-private` 可关闭私有尾，仅发送标准 EAP-MD5，用于协议对比调试。

## 安装

需要 Python 3.10 或更高版本。

### Windows

安装 Npcap，并启用 WinPcap API 兼容模式。然后在管理员 PowerShell 中运行：

```powershell
cd D:\Files\Develop\HXXY\ruijie-campus-auth-hxxy
python -m pip install -e .
```

### Linux

先安装 libpcap。Debian/Ubuntu 示例：

```bash
sudo apt install python3 python3-pip libpcap-dev
cd /path/to/ruijie-campus-auth
python3 -m pip install --user .
```

也可以直接从 GitHub 安装当前源码：

```bash
python3 -m pip install "git+https://github.com/FairyXH/ruijie-campus-auth-hxxy.git"
```

## 快速使用

安装后直接执行：

```text
ruijie-auth
```

程序会逐个探测物理网卡，并自动选择收到锐捷 Identity 请求的校园网网卡，然后提示
输入账号和密码：

```text
校园网用户名: YOUR_ID
校园网密码:
```

也可以提前指定用户名，密码仍采用隐藏输入：

```text
ruijie-auth --username YOUR_ID
```

账号密码都通过参数传入，适合启动脚本或无人值守认证：

```text
ruijie-auth --username YOUR_ID --password YOUR_PASSWORD
ruijie-auth -u YOUR_ID -p YOUR_PASSWORD
```

命令行参数可能被系统进程列表或 Shell 历史记录保存；在多人共用环境中建议继续使用
默认的隐藏输入方式。

只有需要覆盖自动选择结果时才指定网卡：

```text
ruijie-auth --interface enp3s0 --username YOUR_ID
ruijie-auth --interface "Realtek USB GbE" --username YOUR_ID
```

查看可用网卡：

```text
ruijie-auth --list-interfaces
```

注销当前认证：

```text
ruijie-auth --logoff
```

仅发送标准 EAP-MD5、不附加私有尾（调试用）：

```text
ruijie-auth --no-private --username YOUR_ID
```

直接通过 Python 模块运行也可以：

```text
python -m ruijie_auth
```

## GUI 应用

项目附带一个 Qt（PySide6）桌面端，面向日常使用和校园网调试：

- 紧凑的 1/4 屏窗口（约屏幕宽高各 50%），iOS 白色风格；
- 左侧选项卡配置栏（账号 / 认证 / 连接 / 调试），右侧独立运行日志；
- 多账号/密码管理器，支持启用、备注、测试登录和快速切换；
- 抢占模式：认证成功后持续检测连通性，发现掉线立即发送 Logoff/Start 重新认证，
  失败则无限重试；
- 系统托盘：关闭窗口可最小化到托盘，支持托盘菜单开始/停止/退出；
- 调试工具：网卡探测、发送 Logoff/Start、手动认证一次、原始以太网帧发送、
  私有尾预览、源 MAC 覆盖、私有尾开关；
- 数据持久化：账号和设置保存在 `%APPDATA%\RuijieCampusAuth`，账号密码使用
  Windows DPAPI 加密，日志滚动保存在 `%APPDATA%\RuijieCampusAuth\logs`；
- 托管模式：开启后持续结束 `8021x.exe`，并写入临时 IFEO 策略阻止其启动；
  与认证任务绑定，停止认证或退出程序时自动恢复；
- 与 `C:\Program Files\restart_ruijie.vbs` 联动：只有“状态已启用 + 心跳新鲜 +
  记录的 PID 仍存活 + IFEO 标记存在”四项全部满足时才不拉起原客户端；否则自动
  清理陈旧策略并回退原版；
- Windows 下使用 Npcap 发送队列复现原客户端的紧凑三次发送。

安装 GUI 依赖并以源码运行：

```powershell
python -m pip install -e ".[gui]"
ruijie-auth-gui
# 或
python -m ruijie_auth.gui
```

带参数启动到托盘并自动开始认证任务：

```powershell
ruijie-auth-gui --tray --auto
ruijie-auth-gui --tray --auto --account YOUR_ID
ruijie-auth-gui --auto --interface "Realtek USB GbE"
```

参数说明：

- `--tray`：启动后不显示主窗口，直接驻留托盘；
- `--auto`：启动后自动开始认证任务（使用当前选中账号）；
- `--account USERNAME`：按用户名选择账号；
- `--interface NAME`：覆盖网卡（名称、描述或 NPF 设备名）；
- `--verbose`：详细协议日志；
- `--version`：显示版本后退出；
- `--restore-client`：清除托管模式写入的 IFEO 策略/心跳并退出（需要管理员）。

### 托管模式与网络管家

GUI 的“托管模式”开关会同时启动抢占认证任务和原客户端阻止器。运行时写入：

- `%ProgramData%\RuijieCampusAuth\takeover.ini`：`enabled=1`、GUI PID、程序路径，
  每秒刷新一次心跳；
- IFEO 键 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution
  Options\8021x.exe` 下的 `Debugger` 和 `HXXYTakeover=RuijieCampusAuth` 标记
  （同时写 64/32 位视图）；
- `%ProgramData%\RuijieCampusAuth\blocker_state.json`：原策略备份，供崩溃恢复。

`restart_ruijie.vbs` 只有以下四项全部成立时才跳过原版：

1. `takeover.ini` 中 `enabled=1`；
2. 文件修改时间距今不超过 12 秒（GUI 每秒刷新）；
3. 文件中记录的 PID 仍以 `RuijieCampusAuth.exe` 运行；
4. 64 位或 32 位注册表视图存在 `HXXYTakeover` 标记。

任一项不满足时，VBS 视为陈旧/异常：结束残留 GUI、执行
`RuijieCampusAuth.exe --restore-client` 清除 IFEO/心跳，然后按原逻辑结束
8021x 并启动原客户端。旧版本没有 `HXXYTakeover` 标记的 IFEO 策略也会被识别和
清理。手动恢复命令：

```powershell
& "D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" --restore-client
```

### 认证通道缓存

自动探测模式不再每次都扫描网卡和认证服务器：

1. 首次使用或点击“强制重新探测”时，执行一次完整网卡探测；
2. 只要服务器返回任意 EAP 响应（Success、Failure 或 Identity/MD5 Request），
   就把网卡、描述、MAC 和服务器 MAC 写入
   `%APPDATA%\RuijieCampusAuth\discovery.json`；
3. 之后直接用缓存的网卡发起认证，跳过网卡探测；
4. 如果缓存的认证通道完全没有响应（没有收到任何服务器 EAP 包），清除缓存，
   下一次自动重新探测网卡和认证服务器；
5. 手动指定网卡时不使用自动探测缓存。

“连接”选项卡中的“强制重新探测”按钮会立即清除缓存；如果认证任务正在运行，
会让当前认证线程中断并按新探测结果重新开始。

GUI 的抢占模式默认开启：认证成功后每 2 秒做一次 TCP 连通性检测（默认目标
`www.baidu.com:80`），掉线后先发送带私有尾的 Logoff，再立即重新 Start/认证；
失败按设定的重试间隔无限重试。检测地址、间隔、超时、Start 次数和应答次数都可
在界面中调整。

## 构建单文件 exe

在 Windows 上执行：

```powershell
.\build.ps1
```

脚本会安装 `gui`/`build` 依赖，生成图标（如缺失），用 PyInstaller 构建单文件
窗口程序：

```text
dist\RuijieCampusAuth.exe
```

- 单文件、无控制台、请求管理员权限（UAC）；
- 内置图标和版本信息；
- 仍需本机安装 Npcap（WinPcap API 兼容模式）；
- 构建日志在 `logs\build.out.log` / `logs\build.err.log`；
- `.\build.ps1 -Clean` 会先清理 `build`/`dist`；
- `.\build.ps1 -SkipInstall` 跳过依赖安装，适合已配置好的构建机；
- `.\build.ps1 -Deploy` 在构建后部署到
  `D:\Storageredirect\Program\RuijieCampusAuth`：
  - 复制 `RuijieCampusAuth.exe`；
  - 备份并更新 `C:\Program Files\restart_ruijie.vbs`；
  - 在部署目录和桌面创建 `HXXY-CampusAuth.lnk`；
  - 需要管理员 PowerShell。

部署后的快捷方式、网络管家引用和 `--restore-client` 都从以下路径启动：

```text
D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe
```

已验证构建产物可用 `--tray --auto` 启动并完成真实认证，且 VBS 能以健康/陈旧两种
状态正确跳过或恢复原客户端。

## Python API

推荐导入名为 `ruijie_hxxy`。便捷函数会同步执行一次登录，并返回保留本次运行状态和
日志的 `RuijieClient` 对象：

```python
import ruijie_hxxy

client = ruijie_hxxy.login(
    "YOUR_ID",
    "YOUR_PASSWORD",
    # interface="enp3s0",  # 省略时自动探测
)

status = client.status()
print(status["authenticated"])
print(status["message"])
print(status["interface_description"])
print("\n".join(status["logs"]))
```

需要先配置、后执行时使用类接口：

```python
from ruijie_hxxy import RuijieClient

client = RuijieClient(
    "YOUR_ID",
    "YOUR_PASSWORD",
    interface=None,
    timeout=30,
    retries=3,
    repeats=3,
    log_file="ruijie-auth.log",
    verbose=False,
    private_tail=True,  # False 时仅发送标准 EAP-MD5，调试用
)

success = client.login()
state = client.status()
client.logout()
```

`status()` 返回新的 `dict` 快照，主要字段如下：

- `state`：`idle`、`authenticating`、`authenticated`、`failed`、`error` 或
  `logged_out`；
- `authenticated`：本次登录是否收到 EAP-Success；
- `message`：简短中文状态；
- `interface`、`interface_description`、`ip`、`mac`：实际认证网卡；
- `started_at`、`finished_at`：带时区的 ISO 8601 时间；
- `logs`：本次调用的日志列表，每行均以 `YYYY-MM-DD HH:MM:SS` 开头。

账号和密码只保存在当前 `RuijieClient` 对象内，不会放入状态字典或日志。若将密码直接
写入源码，应自行保护源码文件；命令行 `--password` 还可能被 Shell 历史或进程列表记录。

## 账号兼容性与抓包说明

- 源码、构建产物和 README 均不包含测试账号、密码或捕获报文；
- 当前标准 EAP Identity/EAP-MD5 和私有尾实现没有绑定某个账号，算法层面对任意账号
  相同；
- 项目运行时不会读取或重放开发阶段的 pcap；
- 0x54 静态机器标识来自测试机抓包（`Static:AB45A862`），0x39 服务名默认值为
  本网络下发的“电信宽带服务”；换机器或换学校时这两个字段可能需要调整；
- 0x17 nonce、0x4D 指纹和网络地址字段在已验证环境中不影响服务器判定，代码使用
  随机值或本机自动采集值；
- 若更换学校或服务器策略，建议先用 `--verbose` 抓取一次原客户端和本工具的
  EAPOL 流程进行对比。

## 0.9.0 改动

- 新增 Windows 系统服务 `HXXY-CampusAuth`：以 LocalSystem 在用户登录前启动，
  读取安装目录下的 `service.json`，无界面认证并抢占保活；
- 服务配置、日志、状态、发现缓存全部位于
  `D:\Storageredirect\Program\RuijieCampusAuth`，不再使用 `%ProgramData%`；
- 机器级凭据使用 LocalMachine DPAPI，`service.json` ACL 仅 SYSTEM /
  Administrators 可读；
- GUI 新增“系统服务开机自启”开关；服务运行时 GUI 不再抢占认证；
- 部署路径改为
  `D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe`；
- GUI / 托盘启动命令固定为：

```powershell
"D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" --tray --auto
```

- 托管/接管心跳与 blocker 状态也迁移到安装目录，`restart_ruijie.vbs` 同步更新。

## 0.8.0 改动

- 对齐官方客户端的认证相关配置：服务名（0x39）、静态 ID（0x54）、Start 重试
  间隔、DHCP 模式/超时/Option 82、学校代码；
- 服务名和静态 ID 已接入私有尾，覆盖 Identity、MD5、Start、Logoff；
- DHCP 参数为 OpenWrt 预留，Windows 下只持久化，不改变系统网络配置；
- CLI 新增 `--service-name` / `--static-id` / `--start-timeout`；
- API 同步支持 `service_name` / `static_id` / `start_timeout`；
- 连接页新增“高级认证参数”分组。

## 0.7.0 改动

- 新增“联动”页：填写本机 Network Guardian MCP 地址，默认
  `http://127.0.0.1:18765/mcp`，仅允许 `127.0.0.1` 回环；
- 联动启用后，认证线程按网卡 GUID/MAC 从 `guardian_status` 读取认证网卡的
  真实外网探测（`isOnline` / `stableOnline` / 强制门户），连续 N 次判定离线
  自动补认证；
- MCP 不可用或该网卡没有探测数据时，自动回退到本机 TCP 检查；
- 新增零依赖 `guardian.py` MCP 客户端；设置页提供“测试并读取”查看当前网卡、
  IP、网关、探测摘要；
- 配置项持久化：`guardian_enabled` / `guardian_url` / `guardian_interval` /
  `guardian_failures`。

## 0.6.0 改动

- 新增“服务器”页：服务器应答 EAPOL 后自动入库，记录 MAC、名称/描述、发现时间、
  最近成功时间和成功次数；
- 服务器列表持久化到 `%APPDATA%\RuijieCampusAuth\servers.json`；发现缓存中的
  服务器会在首次启动时自动导入；
- 新增“锁定服务器发包”：锁定后 EAPOL-Start 单播到锁定服务器，并忽略其他服务器
  返回的 EAP 帧；Logoff 仍发往标准 PAE 组播（单播 Logoff 无法清理交换机会话）；
- 锁定配置写入 `settings.json`（`server_lock` / `locked_server`），支持手动添加、
  重命名、删除和刷新；
- `Supplicant` 新增 `locked_server` 参数；CLI 新增 `--server MAC`，API
  `RuijieClient(..., locked_server=...)` 同步支持。

## 0.5.1 改动

- 窗口固定为 880×620，禁止改变大小和最大化；左侧配置栏固定 440px，取消可拖动分割条；
- 开始/停止合并为同一个按钮：空闲时“开始认证”，运行中变为“停止”，停止后恢复；
- 账号表格启用双向按像素滚动，支持上下/左右拖动，列宽可手动调整；
- 托盘右键菜单改为白底样式，菜单中的认证操作同样合并为开始/停止切换；
- 认证通道/强制重新探测移到连接页顶部，账号状态列默认可见；
- 托管模式、抢占模式、所有策略开关和连接页参数继续持久化到
  `%APPDATA%\RuijieCampusAuth\settings.json`。

## 0.5.0 改动

- GUI 重排：约 1/4 屏窗口、iOS 白色风格、左侧选项卡（账号 / 认证 / 连接 /
  调试）、右侧独立日志；
- 所有按钮/开关重新布局：iOS 风格 ToggleSwitch、主操作按钮、账号操作网格、
  调试操作 2×2 网格；
- 新增认证通道缓存：自动探测命中后记录网卡/服务器 MAC，后续直接复用；无响应
  自动清缓存重探；“连接”页提供强制重新探测按钮；
- `Supplicant` 新增 `connected/connected_at`，任意服务器 EAP 响应都会被视为
  通道可用（认证失败也缓存）；
- 新增 `DiscoveryCache` 及单元测试；
- 认证时间窗说明：服务器闭窗时返回“不在认证时段内!”，客户端会按重试策略继续。

## 0.4.0 改动

- 新增 PySide6 GUI：多账号管理、抢占保活、系统托盘、调试工具；
- 新增 `storage.py`：`%APPDATA%\RuijieCampusAuth` 持久化，Windows DPAPI
  加密密码；
- 新增 `blocker.py`：托管模式下持续结束 `8021x.exe`，写入 64/32 位 IFEO
  `Debugger` 和 `HXXYTakeover` 标记，退出/停止时恢复；
- 新增 `handoff.py`：`%ProgramData%\RuijieCampusAuth\takeover.ini` 每秒心跳，
  供 VBS 判断托管是否健康；
- 新增 `--restore-client`：清理陈旧 IFEO/心跳，兼容旧版无标记策略；
- 重写 `restart_ruijie.vbs`：四重校验后决定是否拉起原版；陈旧/崩溃/旧策略时
  自动恢复；
- 新增 `--tray`/`--auto`/`--account`/`--interface` 启动参数；
- 新增 `build.ps1`、PyInstaller spec、应用图标、版本信息、`-Deploy` 部署
  （exe、VBS、快捷方式）；
- 新增 GUI/存储/心跳/离屏冒烟测试；
- `Supplicant` 支持取消、源 MAC 覆盖和带私有尾的 `logoff()`。

## 0.3.0 改动

- 新增完整锐捷私有尾：23 字节混淆头、47 字节前导、0x17/0x2D/0x2F/0x39/0x54
  等属性；
- 实现 MD5 链式 XOR 凭据加密，用户名作 key、密码作明文、challenge 作 MD5 应答 IV；
- EAPOL-Start/Logoff 也携带私有尾，并在认证前清理旧会话；
- 修正 EAPOL 长度字段：只覆盖标准 EAP 包，私有尾不进入 EAP/EAPOL 长度；
- Windows 使用 Npcap 发送队列复现原客户端的紧凑三次发送；
- 增加约 40ms 应答延迟、重复请求去重和自动网络信息采集；
- 新增 `--no-private` 和 API `private_tail` 调试开关；
- 新增私有尾结构、CRC/混淆、凭据加密和 EAPOL 长度单元测试。

## 0.2.0 改动

- 新增 `ruijie_hxxy.login()` 便捷函数；
- 新增可复用的 `RuijieClient` 类及 `login()`、`logout()`、`status()` API；
- 保留 `stat()` 作为 `status()` 的兼容别名；
- 内存日志与文件/控制台日志使用相同的时间在前格式；
- 自动选卡探测到认证服务器后主动发送 Logoff，避免探测遗留临时会话。

## 日志

默认日志文件为当前目录下的 `ruijie-auth.log`，最多保留三份轮转文件。日志包括：

- 自动选择或手动指定的网卡；
- 寻找和连接认证服务器；
- Identity 与 EAP-MD5 阶段；
- 服务器返回的中文通知；
- 认证成功、失败、超时和网卡访问错误。

使用 `--log-file PATH` 可修改日志位置，使用 `--verbose` 可在控制台显示协议调试信息。

## 权限

程序需要收发二层 EAPOL 帧：

- Windows：使用管理员终端，并安装 Npcap；
- Linux：使用 `sudo ruijie-auth`，或为 Python/启动器配置等效的原始套接字权限。

## 实网验证结果

2026-11-17 在厦门华厦学院有线校园网验证：

- 抓包确认原客户端使用 EAP-MD5；EAPOL-Logoff/Start 帧为 587 字节，Identity 应答
  为 601 字节，MD5 应答为 618 字节；
- 私有尾重建结果与原客户端抓包逐字节一致（Identity 和 MD5 两帧均为 569/569）；
- Python 客户端完成 EAP-MD5 后收到 EAP-Success，服务器消息为
  “欢迎使用厦门华厦学院网络”；
- 同一时间原客户端也可正常认证，网络和账号状态正常；
- 23:49 使用部署后的 `D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe`
  `--tray --auto` 完成真实认证；托管模式同时结束 8021x 并写入 64/32 位
  IFEO 标记；
- 23:50 调用部署后的 `restart_ruijie.vbs`：托管健康时日志为
  `healthy takeover; skip original`，未拉起原版；
- 强制结束 GUI 后再次调用 VBS：日志为 `restoring stale policy` →
  `launching original supplicant`，IFEO/心跳恢复，原版 8021x 启动，网络 200；
- 23:54 模拟旧版无 `HXXYTakeover` 标记的 IFEO 策略，VBS 同样能识别、恢复并
  拉起原版；
- 自动化测试覆盖私有尾长度/结构、CRC/混淆、凭据加密、EAPOL 长度、账号存储、
  认证通道缓存、心跳和 GUI 离屏构建（12 passed）；
- 2026-11-18 00:03 后服务器开始返回“不在认证时段内!”，这是学校认证时间窗限制，
  不是账号或客户端失败；GUI 会按配置的无限重试策略继续尝试；
- 0.5.0 的认证通道缓存及新布局已通过单元/离屏测试；由于闭窗，尚未完成一次新的
  实网缓存回归。

仓库源码、构建产物、README 和安装元数据不包含测试账号或密码。GUI 的账号文件
位于 `%APPDATA%\RuijieCampusAuth`，由用户通过界面管理，密码使用 Windows
DPAPI 加密。
