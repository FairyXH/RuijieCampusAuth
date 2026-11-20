# 锐捷认证器 · C# 版操作手册

面向第一次接触本项目的用户：从安装、配置、认证，到服务模式、接管模式、
命令行和排错。全部操作都在 Windows 图形界面或 PowerShell 里完成。

> 适用版本：仓库根目录的 C# / .NET 10 实现（Core + CLI + Avalonia GUI +
> Windows 服务 / Linux systemd）。
>
> 相关文档：`README.md`（项目总览）、`profiles/README.md`（档案字段）、
> `docs/ADAPTATION.md`（新学校适配）、`docs/REVERSE_ENGINEERING.md`（逆向细节）。

---

## 1. 它是什么

一个与锐捷官方客户端协议兼容的第三方校园网认证器：

- 走标准 802.1X / EAP-MD5（以及 `auth_mode` 打开的 PEAP / EAP-TLS）；
- 在标准 EAP 之外附加锐捷私有的 569 字节尾包；
- 不依赖、不修改官方客户端进程，也不改服务器行为；
- 学校差异（服务名、静态 ID、认证方式、各类 URL）通过 JSON 档案导入，
  程序本身不内置学校数据。

三种使用形态：

| 形态 | 适用场景 | 入口 |
|---|---|---|
| GUI | 日常开关认证、看日志、改配置 | `RuijieCampusAuth.exe` |
| 服务 | 开机未登录就认证、无人值守 | Windows 服务 `RuijieCampusAuth` |
| CLI | 脚本、批处理、排错 | `RuijieCampusAuth.Cli.exe` |

---

## 2. 准备工作

### 2.1 系统和权限

- Windows 10 / 11 x64（GUI 与服务都在 Windows 上验证过）。
- 有线网卡直连校园网墙面端口；Wi-Fi 不适用本流程（无线企业级认证是另一套）。
- 需要**管理员权限**：安装服务、打开网卡抓包、修改接管策略都要。
- 关闭或退出官方锐捷客户端，避免两个客户端抢同一条 802.1X 会话。

### 2.2 安装 Npcap（必需）

程序通过 Npcap 收发二层 EAPOL 帧：

1. 打开 <https://npcap.com/> 下载安装包。
2. 安装时勾选 **“Install Npcap in WinPcap API-compatible Mode”**。
3. 安装完成后重启（至少注销一次）。
4. 验证：管理员 PowerShell 执行
   `RuijieCampusAuth.Cli.exe --list-devices`，能看到网卡列表。

> 只装了 WinPcap 不行，必须是 Npcap 的 WinPcap 兼容模式。

### 2.3 .NET 运行时

- 使用已发布的程序：需要 **.NET 10 Desktop Runtime**（GUI）与
  **.NET 10 Runtime**（CLI/服务）。发布包是 framework-dependent。
- 自己构建：需要 **.NET SDK 10**（`dotnet --version` ≥ `10.0.1xx`）。

---

## 3. 安装与部署

### 3.1 方式 A：从源码构建（推荐）

```powershell
git clone https://github.com/FairyXH/RuijieCampusAuth.git
cd RuijieCampusAuth
dotnet build RuijieCampusAuth.slnx -c Release
powershell -ExecutionPolicy Bypass -File build-csharp.ps1 -Runtime win-x64
```

产物在 `publish\win-x64\`：

```text
RuijieCampusAuth.exe         GUI
RuijieCampusAuth.Cli.exe     命令行
RuijieCampusAuth.Service.exe Windows 服务宿主
profiles\                    学校档案
```

### 3.2 方式 B：构建并部署到指定目录

```powershell
powershell -ExecutionPolicy Bypass -File build-csharp.ps1 -Runtime win-x64 -Deploy `
    -DeployDir "D:\Storageredirect\Program\RuijieCampusAuth"
```

部署脚本会：

1. 停止正在运行的 GUI 与服务；
2. 把 `publish\win-x64\*` 复制到 `-DeployDir`；
3. 同步 `profiles\`；
4. 创建 `RuijieAuth.lnk` 快捷方式；
5. 不会自动安装服务（需要你手动 `service install`）。

### 3.3 冻结还原机器（Deep Freeze 等）

- 程序、配置、日志全部放**非冻结盘**（例如 D:），不要放 C:。
- `-DeployDir` 指向非冻结盘即可；部署脚本不会写 C: 的启动项或服务注册。
- 重启还原后，服务注册可能丢失；把 `service install` 放进还原前的基线，
  或用计划任务在登录后执行。

### 3.4 目录里都有什么

| 文件 | 作用 |
|---|---|
| `settings.json` | GUI / 服务共用设置、当前档案、认证参数 |
| `accounts.json` | 账号池（密码 DPAPI 加密，按当前 Windows 用户） |
| `service.json` | 服务专用配置（账号 + 设置，LocalMachine DPAPI） |
| `service_status.json` | 服务写入的状态（CLI/GUI 读取） |
| `discovery.json` | 网卡 / 服务器探测缓存 |
| `servers.json` | 已学习的认证服务器与锁定状态 |
| `takeover_state.json` / `takeover.ini` | 接管模式心跳与状态 |
| `logs\service.log` | 服务日志（10MB 轮转） |
| `logs\csharp-gui.log` | GUI 日志（5MB 轮转） |
| `logs\debug.log` | 调试功能审计日志（10MB 轮转） |

---

## 4. 第一次认证（GUI）

### 4.1 打开程序

双击 `RuijieCampusAuth.exe`。窗口固定大小，共六个页签：
**账户 / 认证 / 网络 / 服务 / 配置 / 调试**。

> GUI 是单实例的：如果已经有一个在运行，再启动会静默退出，属于正常行为。

### 4.2 导入学校档案（配置页）

1. 切到 **配置** 页。
2. 在“学校配置档案”下拉框里选择档案（程序目录 `profiles\*.json` 会被扫描）。
3. 点 **导入 / 切换**。
4. 页面会显示当前档案名与已应用字段。

导入只覆盖档案里声明的字段：账号、网卡、接管开关、Guardian 设置都不变，
导入前自动备份 `settings.json.bak-<时间戳>`。

没有档案时：向学校管理员或项目维护者索取 `profiles\<学校>.json`，
或者参照 `profiles/README.md` 自己写一个。

### 4.3 选择网卡（网络页）

1. 切到 **网络** 页。
2. “网卡”下拉框选择接校园网的有线网卡（看描述里的芯片/接口名）。
3. 右侧显示“认证通道”状态；首次使用可以点 **强制重新探测**。
4. 认证成功后，程序会把网卡和服务器 MAC 写入 `discovery.json`，
   下次直接复用；如果缓存的通道完全没响应，会自动清缓存重探。

### 4.4 添加账号（账户页）

1. 切到 **账户** 页。
2. 在右侧表单填 **用户名**、**密码**、备注（可选）。
3. 点 **添加**。
4. 选中账号，点 **设为当前**（服务模式会用这个账号）。

可以添加多个账号：当前账号失败后，服务会按启用顺序尝试其它账号。

### 4.5 开始认证（认证页）

1. 切到 **认证** 页。
2. 点 **开始认证**。
3. 观察右侧日志：
   - `正在清理可能存在的旧会话...` → `正在寻找认证服务器...`
   - `已连接认证服务器，提交用户身份...`
   - `正在进行 EAP-MD5 认证...`
   - `服务器消息: 欢迎使用……` → `认证成功`
4. “抢占保活”勾选后，认证成功会持续检测连通性并自动补认证。
5. “详细信息”打开后会显示心跳等例行输出；`[服务]` 前缀的行来自后台服务。

### 4.6 停止 / 注销

- 点 **停止** 结束本次认证循环。
- 如果服务也在跑，要停服务请到 **服务** 页点 **停止**，或命令行
  `service stop`。

---

## 5. 认证方式与协议参数（配置页高级区）

展开 **配置 → 高级功能：认证与协议参数**：

| 字段 | 说明 | 典型值 |
|---|---|---|
| 服务名 | 私有尾 0x39，学校下发或档案提供 | 由档案决定 |
| 静态 ID | 私有尾 0x54 | 由档案决定 |
| 认证方式 | `EAP-MD5` / `PEAP` / `EAP-TLS` | 多数学校是 EAP-MD5 |
| 认证超时 | 单次认证总超时（秒） | 20 |
| Start 次数 | EAPOL-Start 最多发几次 | 4 |
| 应答次数 | 每个应答帧重复发几次 | 3 |
| Start 间隔 | 两次 Start 之间等待（秒） | 3 |
| 掉线检测 | 抢占保活的连通性检测间隔（秒） | 2 |
| 失败重试 | 一轮失败后等待多久再试（秒） | 3 |
| 连通检测 | 用来判断在线的 URL | `http://www.baidu.com/` |
| 学校代码 | 仅记录/诊断用 | 档案提供的值 |
| AD 域 / AD 超时 | PEAP 场景的域信息 | 可留空 |
| 引擎版本 | 对应官方客户端构建 | 例如 `5.0.2014.108` |
| 自助 / 升级 / Portal URL | 调试页预填用 | 档案提供 |
| SAM 主机 / 端口 | SAM 直通调试用 | 可留空 |

改完点 **保存配置**。

> `auth_mode` 为 `PEAP`/`EAP-TLS` 时，程序走 TLS 隧道；这条路径协议栈已完成，
> 但尚未在真实 RADIUS 上做端到端验证。生产环境默认用 EAP-MD5。

---

## 6. 认证服务器：学习与锁定（网络页）

- 服务器只要回过任意 EAP 包，就会自动加入“认证服务器”列表，记录 MAC、
  名称、发现时间、最近成功时间、成功次数。
- 选中服务器 → **锁定选中服务器**：之后 EAPOL-Start 单播到该 MAC，
  并忽略其它服务器的 EAP 帧；Logoff 仍走标准组播（单播 Logoff 清不掉
  交换机侧会话）。
- **取消锁定** 恢复自动发现。
- 手动填 “服务器 MAC” + 勾“锁定服务器发包”也可以。
- **删除选中记录** 只删本地记录，不影响网络。

适用场景：宿舍/办公室存在多个认证服务器，或者需要固定走某一台。

---

## 7. 服务模式（开机未登录就认证）

### 7.1 安装

前提：至少有一个账号，并且 `service.json` 已生成。

**方式一（GUI）**：
1. 账户页确认账号和“当前”标记。
2. 服务页点 **安装并启动**。
3. 首次安装会删除旧的 `HXXY-CampusAuth` 服务并迁移到新服务名
   `RuijieCampusAuth`。

**方式二（CLI）**：

```powershell
# 1) 生成服务配置（必须提供账号、网卡）
RuijieCampusAuth.Cli.exe config init `
    --username YOUR_ID --password YOUR_PASSWORD --interface "以太网"

# 2) 安装并启动
RuijieCampusAuth.Cli.exe service install
```

服务属性：

- 服务名 `RuijieCampusAuth`，显示名 “Ruijie Campus Auth”；
- LocalSystem，自动启动，依赖 `npcap`；
- 失败恢复：5s / 10s / 30s；
- 每轮重试重新读取 `service.json`，改配置下一轮生效；
- 账号池失败切换、服务器锁定、掉线检测、家庭网络暂停都生效。

### 7.2 常用命令

```powershell
RuijieCampusAuth.Cli.exe service status
RuijieCampusAuth.Cli.exe service start
RuijieCampusAuth.Cli.exe service stop
RuijieCampusAuth.Cli.exe service uninstall
```

服务日志：`<安装目录>\logs\service.log`。
状态：`<安装目录>\service_status.json`，`service status` 会直接打印。

### 7.3 应用配置

GUI 服务页的 **应用服务配置** 会把当前账号 + 设置写入 `service.json`，
运行中的服务下一轮生效。CLI 等价操作是重新执行 `config init`。

`service.json` 的 ACL 只允许 SYSTEM / Administrators 读取，普通用户读不了，
这是设计如此。

---

## 8. 接管模式（可选，风险操作）

作用：在服务层临时阻止官方锐捷 `8021x.exe` 启动，避免两个客户端抢会话。

- 打开：服务页 → 高级功能 → **接管模式** → **应用服务配置**。
- 关闭：取消勾选 → **应用服务配置**。
- 实现：IFEO `Debugger` + `RuijieTakeover` 标记（64/32 位视图），
  兼容旧 `HXXYTakeover` 标记。

注意：

1. 这是**修改系统注册表**的操作，只在你明确知道后果时启用。
2. 关闭程序不会自动清除策略时，执行 `config takeover off`，或卸载服务。
3. 如果官方客户端无法启动，先关接管，再检查
   `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\8021x.exe`。
4. 杀毒软件可能把 IFEO 修改当成恶意行为；需要时加白名单。

---

## 9. 网络管家联动（Guardian，可选）

服务页高级区：

- 勾 **启用网络管家联动**；
- 填 MCP 地址（默认 `http://127.0.0.1:18765/mcp`，只允许回环）；
- 设置检测间隔和离线确认次数。

启用后，认证线程按网卡 GUID/MAC 从 `guardian_status` 读取真实外网探测，
连续 N 次判定离线就补认证；MCP 不可用或没数据时回退到本机 TCP 检测。

---

## 10. 命令行（CLI）完整参考

所有命令都在管理员 PowerShell 里执行。程序目录不在 PATH 时用完整路径。

### 10.1 一次性认证（默认命令）

```powershell
RuijieCampusAuth.Cli.exe [--interface NAME] [--username USER] [--password PASS] ...
```

- 省略 `--username/--password` 时，从 `accounts.json` 的当前账号读取；
- 省略 `--interface` 时，从 `settings.json` 的 `interface` 读取；
- 输出 `RESULT SUCCESS` / `RESULT FAILURE`，退出码 0/1。

可选参数：

```text
--service-name NAME   私有尾 0x39
--static-id ID        私有尾 0x54
--server MAC          锁定服务器
--start-timeout SEC   Start 间隔
--timeout SEC         认证超时
--retries N           Start 次数
--repeats N           应答重复次数
--list-devices        列出 Npcap 设备后退出
```

### 10.2 状态

```powershell
RuijieCampusAuth.Cli.exe status
```

输出服务是否安装/运行，以及 `service_status.json` 的最近状态。

### 10.3 档案

```powershell
RuijieCampusAuth.Cli.exe profile list
RuijieCampusAuth.Cli.exe profile list --dir "<档案目录>"
RuijieCampusAuth.Cli.exe profile show "profiles\厦门华厦学院.json"
RuijieCampusAuth.Cli.exe profile import "profiles\厦门华厦学院.json"
RuijieCampusAuth.Cli.exe profile import "profiles\厦门华厦学院.json" --root "<安装目录>"
```

`import` 会打印档案名、settings 路径、备份路径、实际应用的字段列表。

### 10.4 服务

```powershell
RuijieCampusAuth.Cli.exe service status
RuijieCampusAuth.Cli.exe service install
RuijieCampusAuth.Cli.exe service start
RuijieCampusAuth.Cli.exe service stop
RuijieCampusAuth.Cli.exe service uninstall
```

Linux 上 `service install` 会写
`/etc/systemd/system/ruijie-campus-auth.service`，然后按提示执行：

```bash
systemctl daemon-reload
systemctl enable --now ruijie-campus-auth.service
```

### 10.5 服务配置

```powershell
RuijieCampusAuth.Cli.exe config init --username U --password P --interface "以太网"
RuijieCampusAuth.Cli.exe config show
RuijieCampusAuth.Cli.exe config path
RuijieCampusAuth.Cli.exe config takeover on|off
```

`config init` 可追加：`--service-name`、`--static-id`、`--timeout`、
`--retries`、`--repeats`、`--start-timeout`、`--check-interval`、
`--retry-interval`、`--url`、`--server`、`--no-preempt`。

### 10.6 调试功能

```powershell
RuijieCampusAuth.Cli.exe debug list
RuijieCampusAuth.Cli.exe debug status peap
RuijieCampusAuth.Cli.exe debug run system-info
RuijieCampusAuth.Cli.exe debug run private-properties --args '{"hex":"00001311..."}'
RuijieCampusAuth.Cli.exe debug enable peap
RuijieCampusAuth.Cli.exe debug disable peap
RuijieCampusAuth.Cli.exe debug enable all
RuijieCampusAuth.Cli.exe debug settings
```

功能名（21 项）：

```text
system-info           系统信息
private-properties    服务器私有属性
nic-monitor           网卡监控
oui-detect            锐捷设备识别
arp-monitor           ARP 网关监控
network-probe         网络诊断
proxy-detect          代理检测
dialup-detect         拨号规避
doctor-pipe           小锐医生管道
dhcp-client           DHCP 客户端 / Option 82
peap                  PEAP / MSCHAPv2
eap-tls               EAP-TLS
wireless-profile      无线配置
portal-auth           Portal 认证
portal-udp            Portal UDP
user-messages         用户消息
client-center         客户端中心
self-service          自助服务/余额
server-list-update    服务器列表更新
upgrade-check         升级检查
sam-direct            SAM 直通
```

调试功能默认关闭。启用后从 GUI 调试页或 CLI 手动触发；所有调用写
`logs\debug.log` 审计。涉及 DHCP release、无线变更、Portal 登录的操作
需要二次确认。

---

## 11. GUI 各页速查

### 账户

- 账号表格：用户名、备注、启用、当前；
- 右侧表单：用户名 / 密码 / 备注；
- 按钮：添加、更新选中账号、删除选中账号、新建（清空表单）、
  启用/停用、设为当前账号；
- 服务模式使用“当前”账号，其余启用账号按顺序回退。

### 认证

- 开始认证 / 停止、抢占保活开关、状态文字；
- 实时日志：GUI 本机输出 + `[服务]` 前缀的服务日志；
- 详细信息、清空日志、打开日志目录。

### 网络

- 网卡下拉、强制重新探测、认证通道状态；
- 认证服务器列表、锁定/取消、刷新、删除；
- 高级：DHCP 模式/超时/Option 82、服务器 MAC、锁定发包。

### 服务

- 安装并启动 / 启动 / 停止 / 卸载 / 刷新；
- 高级：接管模式、应用服务配置、Guardian 联动。

### 配置

- 学校档案下拉、导入/切换、刷新；
- 高级：服务名、静态 ID、认证方式、各类超时/重试、URL、AD、引擎版本。

### 调试

- 启用调试功能开关、保存、刷新、打开调试日志；
- 左侧功能列表（分类/阶段/状态）；
- 右侧：说明、启用勾选、参数 JSON、执行/停止、结果输出。

---

## 12. 学校档案怎么写

字段说明见 `profiles/README.md`。最简可用档案：

```json
{
  "name": "示例大学",
  "auth_mode": "EAPMD5",
  "service_name": "校园宽带服务",
  "static_id": "Static:00112233",
  "dhcp_mode": "none"
}
```

关键字段来源：

| 字段 | 从哪来 |
|---|---|
| `service_name` | 原版客户端配置 / 认证成功包 0x39 / 抓包 |
| `static_id` | 原版配置 / 0x54 属性 / 抓包 |
| `auth_mode` | 学校认证方式（MD5 或 PEAP） |
| `school_code` | 学校代码，诊断用 |
| `dhcp_mode` | `before`（认证前续租）/ `after` / `none` |
| 各类 URL | 原版客户端配置里的自助/升级/Portal 地址 |

导入后如果认证仍失败，用 GUI 调试页的 `private-properties` 或抓包核对
服务名/静态 ID 是否与服务器期望一致。

---

## 13. 常见问题

### 13.1 `--list-devices` 没有网卡 / 认证报网卡错误

- 没装 Npcap，或没勾 WinPcap 兼容模式；
- 不是管理员终端；
- 网卡被官方客户端/其它抓包程序独占。退出它们再试。

### 13.2 一直“正在寻找认证服务器...”

按顺序排查：

1. 网线是否插在**校园网**端口（不是宿舍路由器 LAN 口）；
2. 网络页选的网卡是否是这块有线网卡；
3. 交换机端口是否启用了 802.1X（有些学校先要网页注册/绑定 MAC）；
4. 是否需要克隆 MAC：学校把账号绑定到笔记本网卡 MAC 时，在路由器/网卡上
   改成原 MAC；
5. 用 Wireshark 抓 `EAPOL`，看有没有服务器回的 Identity Request；
6. 学校可能限制认证时段，服务器直接不回包。

### 13.3 “认证失败” + 服务器中文消息

日志会原样显示服务器返回的 GB18030 文本，例如：

- `不在认证时段内!` → 学校时间窗限制，等窗口内再试；
- `密码错误` / `账号不存在` → 核对账号；
- `MAC 地址绑定错误` → 需要改 MAC 或找管理员解绑；
- `该账号已在线` → 先在别处注销，或等服务端超时。

### 13.4 服务装了但启动失败

```powershell
RuijieCampusAuth.Cli.exe service status
Get-Content "D:\Storageredirect\Program\RuijieCampusAuth\logs\service.log" -Tail 50
```

常见原因：

- `service.json` 不存在 → 先 `config init`；
- `service.json` 的账号/网卡为空；
- 服务账户读不到 Npcap 驱动 → 重装 Npcap；
- 旧服务 `HXXY-CampusAuth` 还在运行 → `service install` 会自动删旧服务，
  也可以手动 `sc delete HXXY-CampusAuth`。

### 13.5 服务在跑，但 GUI 说没有认证

- 服务模式与 GUI 认证是两条路径，以服务日志为准；
- GUI 认证页的 `[服务]` 行是服务日志镜像；
- 服务运行时不要在 GUI 再点“开始认证”，会抢会话。

### 13.6 GUI 双击没反应

- 已有一个 GUI 实例在运行（单实例互斥锁），检查任务栏/托盘；
- 程序目录被冻结还原，配置文件写不进去；
- 看 `logs\csharp-gui.log`。

### 13.7 换 Windows 用户后账号解不开

`accounts.json` 用当前用户的 DPAPI 加密，换用户/换机器无法解密。
重新录入账号，或改用服务模式（`service.json` 是 LocalMachine DPAPI）。

### 13.8 接管模式后官方客户端起不来

```powershell
RuijieCampusAuth.Cli.exe config takeover off
RuijieCampusAuth.Cli.exe service uninstall
```

再检查 IFEO 键是否还有 `Debugger`/`RuijieTakeover` 残留。

### 13.9 Linux 上没有 GUI/服务

`service install` 会写 systemd unit；GUI 需要 Avalonia 运行时，当前发布脚本
只面向 Windows。Linux 场景优先用 OpenWrt 版或 CLI。

---

## 14. 重置与卸载

### 重置配置（保留程序）

```powershell
# 退出 GUI、停止服务
RuijieCampusAuth.Cli.exe service stop
# 备份后删除配置
Rename-Item settings.json settings.json.bak
Rename-Item accounts.json accounts.json.bak
Rename-Item service.json service.json.bak
```

重新打开 GUI 会生成默认配置，重新导入档案、添加账号即可。

### 完全卸载

```powershell
RuijieCampusAuth.Cli.exe service uninstall
RuijieCampusAuth.Cli.exe config takeover off   # 如果开过
# 删除部署目录（先确认没有别的程序在用）
Remove-Item -Recurse "D:\Storageredirect\Program\RuijieCampusAuth"
```

Npcap 如果只为本程序安装，可以在“应用和功能”里卸载。

---

## 15. 安全与合规

- 账号密码：GUI 账号用当前用户 DPAPI；服务配置用 LocalMachine DPAPI；
  `service.json` ACL 限制为 SYSTEM/Administrators。
- 日志会对 password/token/key/iv 脱敏，但仍建议不要把日志外发。
- 接管模式修改注册表，属于高风险操作，用完及时关闭。
- 本项目与锐捷网络无关联，仅用于协议兼容研究；请确保你有该校园网的
  合法接入权限，并遵守学校网络管理规定。
