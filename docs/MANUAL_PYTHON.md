# 锐捷认证器 · Python 版操作手册

面向第一次使用 Python 版的用户。它没有图形界面，入口是**数字菜单**，
同时保留命令行参数和 Python API。协议算法与 C# 版逐模块对应，学校配置
使用同一套 `profiles/*.json`。

> 代码位置：`python/`。包级 README：`python/README.md`。
> 本手册按“装好 → 导入档案 → 加账号 → 认证 → 排错”的顺序写。

---

## 1. 适合谁用

| 场景 | 建议 |
|---|---|
| Windows 日常使用、要 GUI/服务 | 用 C# 版（`docs/MANUAL_CSHARP.md`） |
| Linux 服务器 / 软路由 / 脚本 | Python 版或 OpenWrt 版 |
| 研究协议、跑向量、做自动化 | Python 版（API + 可读源码） |
| 需要开机认证、无人值守 | Windows 用 C# 服务；Linux 用 systemd 包 CLI |

Python 版主认证流程是 EAP-MD5 + 锐捷私有尾；`auth_mode` 字段支持 PEAP/EAP-TLS
（`peap_session.py` 已移植），但和 C# 一样还没在真实 RADIUS 上端到端验证。

---

## 2. 环境准备

### 2.1 Python

- Python **3.10+**（`python --version` 检查）。
- 建议用虚拟环境，避免污染系统 Python：

```powershell
cd python
python -m venv .venv
.\.venv\Scripts\Activate.ps1
```

Linux/macOS：

```bash
cd python
python3 -m venv .venv
. .venv/bin/activate
```

### 2.2 抓包依赖

程序用 scapy 收发二层 EAPOL 帧：

- **Windows**：安装 [Npcap](https://npcap.com/)，勾选
  **WinPcap API-compatible Mode**；之后所有命令都在**管理员**终端运行。
- **Linux**：`sudo apt install libpcap-dev`，运行时 `sudo`。
- **macOS**：系统自带 libpcap，运行时 `sudo`。

### 2.3 安装包

```bash
python -m pip install -e .
```

依赖只有 `scapy`。安装后验证：

```bash
ruijie-auth --help
ruijie-auth --list-interfaces
```

第二条命令能列出网卡，说明 Npcap/权限没问题。

---

## 3. 五分钟完成第一次认证

### 3.1 导入学校档案

```bash
ruijie-auth
```

进入菜单后输入 `8`：

```text
--- 学校配置档案 ---
 1) <学校档案名> (EAPMD5)
 当前档案: 未导入
 1) 导入/切换  2) 刷新  0) 返回
选择: 1
档案编号: 1
已切换到 <学校档案名>；应用字段: profile_name, school_code, auth_mode, ...
```

档案从这些目录扫描（去重）：仓库根 `profiles/`、当前目录 `profiles/`。

### 3.2 添加账号

菜单输入 `2`：

```text
--- 账号池 ---
 1) 添加  2) 更新选中  3) 删除选中  4) 启用/停用  5) 设为当前  0) 返回
选择: 1
用户名: YOUR_ID
密码:            ← 隐藏输入
备注: 主账号
账号已添加
```

添加后会自动设为当前账号。

### 3.3 选择网卡并认证

菜单输入 `1`：

```text
可用网卡:
  1) Intel(R) Ethernet Connection [\Device\NPF_{...}]
  2) Realtek USB GbE [\Device\NPF_{...}]
选择网卡编号（回车保持当前）: 1

--- 认证管理 ---
 1) 开始认证
 2) 注销会话
 3) 测试连通
 0) 返回
选择: 1
```

成功时：

```text
认证成功
```

失败时菜单会提示“认证失败（校园网不可达属正常）”。详细原因看同目录的
`ruijie-auth.log`，或菜单 `9` 看服务日志。

### 3.4 只做一次认证（不进菜单）

```bash
# 管理员/sudo
ruijie-auth -u YOUR_ID -i "以太网"
# 提示输入密码
```

完全非交互：

```bash
ruijie-auth -u YOUR_ID -p 'YOUR_PASSWORD' -i eth0
```

> `-p` 会留在 shell 历史和进程列表里，多人环境请用隐藏输入。

---

## 4. 菜单逐项说明

### 1) 认证管理

- 先选网卡（会写入 `settings.json` 的 `interface`）；
- **开始认证**：调用一次完整认证；`Ctrl+C` 可中断；
- **注销会话**：向锐捷组播和标准组播各发 3 个 EAPOL-Logoff；
- **测试连通**：网关可达、DNS、HTTP 状态、耗时。

### 2) 账号池

- 列表标记：`●` 启用、`○` 停用、`[当前]` 当前账号；
- **添加**：用户名不能为空、不能重复；密码隐藏输入；
- **更新选中**：回车保持原值；密码留空保持原密码；
- **删除选中**：立即从 `accounts.json` 移除；
- **启用/停用**：切换 `enabled`；
- **设为当前**：服务模式与快捷认证优先使用。

### 3) 认证服务器

- 显示已学习服务器（MAC、来源、成功次数等，来自 `servers.json`）；
- **锁定选中**：写 `server_lock=1` 和 `locked_server=<MAC>`，之后
  EAPOL-Start 单播到该服务器；
- **取消锁定**；**清空全部** 只删本地记录。

### 4) 网络与 DHCP

```text
 dhcp_mode=before timeout=30 option82=False
 1) 续租  2) 释放  3) 释放并续租  4) 查看租约  5) 设置模式
```

- DHCP 操作走 `protocols/dhcp_control.py`，Windows 下用 iphlpapi，
  **Linux 不支持**，会报错；
- 模式含义：`before` 认证前续租、`after` 认证后续租、`none` 不使用。

### 5) 诊断工具

| 项 | 说明 |
|---|---|
| 网络探测 | 网关/DNS/HTTP/耗时 |
| ARP 监控 | 在指定时长内观察网关 MAC 是否变化（防 ARP 欺骗） |
| 代理检测 | 探测常见代理端口 `1080/3128/8000/8080/8888` |
| 拨号检测 | 检查是否同时存在 PPP 拨号 |
| OUI 识别 | 按 MAC 前缀判断是否锐捷设备 |
| 小锐医生管道 | 检查 `\\.\PIPE\SU_DOCTOR_INTERFACE` 是否可用（Windows） |

### 6) 调试功能

用当前档案预填参数，逐项手动触发：

1. Portal 登录
2. Portal 注销
3. Portal 在线信息
4. Portal 页面信息
5. 自助服务
6. 客户端中心
7. 升级检查
8. SAM 探测（主机 / 端口 / hex 载荷）
9. 私有属性解析（粘贴 hex，打印属性、消息、告警）

这些是协议研究功能，不参与主认证，失败不影响登录。

### 7) 系统服务

```text
 1) 安装并启动  2) 启动  3) 停止  4) 卸载  5) 应用服务配置
```

- **安装前**必须先构建服务宿主（见第 7 节），否则会提示找不到
  `RuijieService.exe`；
- **应用服务配置**把当前账号 + 设置写入 `service.json`，运行中的服务
  下一轮生效。

### 8) 学校配置档案

列出扫描到的档案，显示名称与认证方式；选择编号导入。导入会：

- 只覆盖非空字段；
- 备份 `settings.json.bak-<时间戳>`；
- 如果 `service.json` 存在，同步写入。

### 9) 查看日志

读取服务日志 `<服务目录>\logs\service.log` 的最后 60 行。没有安装服务时
提示“服务日志尚未生成”。单次认证日志在 `./ruijie-auth.log`。

---

## 5. 命令行参数（argparse）

```text
ruijie-auth [选项]
```

| 参数 | 说明 |
|---|---|
| `-i, --interface NAME` | 网卡名称、描述或 NPF 设备名 |
| `--list-interfaces` | 列出网卡后退出 |
| `-u, --username USER` | 用户名；省略时交互输入 |
| `-p, --password PASS` | 密码；省略时隐藏输入 |
| `--encoding ENC` | 账号密码编码，默认 `gb18030` |
| `--timeout SEC` | 认证超时，默认 30 |
| `--retries N` | EAPOL-Start 次数，默认 3 |
| `--repeats N` | 每个请求重复发送次数，默认 3 |
| `--start-timeout SEC` | Start 重试间隔，默认 3 |
| `--no-private` | 不附加私有尾（只发标准 EAP-MD5） |
| `--server MAC` | 锁定认证服务器 |
| `--service-name NAME` | 私有尾 0x39；省略时用设置/档案 |
| `--static-id ID` | 私有尾 0x54 |
| `--log-file PATH` | 日志文件，默认 `./ruijie-auth.log` |
| `--logoff` | 只发注销 |
| `-v, --verbose` | 详细协议日志 |

退出码：`0` 成功、`1` 失败、`2` 参数或账号编码错误。

### 常用组合

```bash
# 列出网卡
ruijie-auth --list-interfaces

# 指定网卡、隐藏输入密码、详细日志
sudo ruijie-auth -i enp3s0 -u S12016004 -v

# 只注销
sudo ruijie-auth --logoff -i enp3s0

# 协议对比：不带私有尾
sudo ruijie-auth -u S12016004 --no-private
```

---

## 6. Python API

### 6.1 一步登录

```python
import ruijie_auth

client = ruijie_auth.login("YOUR_ID", "YOUR_PASSWORD")
status = client.status()
print(status["authenticated"], status["message"])
print("\n".join(status["logs"]))
client.logout()
```

`ruijie_auth` 是包入口，也可以直接
`from ruijie_auth import RuijieClient, login`。

### 6.2 可配置客户端

```python
from ruijie_auth import RuijieClient

client = RuijieClient(
    "YOUR_ID",
    "YOUR_PASSWORD",
    interface=None,          # None = 自动探测
    timeout=30,
    retries=3,
    repeats=3,
    private_tail=True,
    locked_server=None,
    service_name=None,       # None = 用设置里的值
    static_id=None,
    start_timeout=3.0,
    log_file="ruijie-auth.log",
    verbose=False,
)
ok = client.login()
print(client.status())
client.logout()
```

`status()` 字段：

| 字段 | 含义 |
|---|---|
| `state` | `idle` / `authenticating` / `authenticated` / `failed` / `error` / `logged_out` |
| `authenticated` | 是否收到 EAP-Success |
| `message` | 中文状态 |
| `interface` / `interface_description` / `ip` / `mac` | 实际使用的网卡 |
| `started_at` / `finished_at` | ISO 8601 时间 |
| `logs` | 本次调用日志（与文件日志同格式） |

密码只保存在客户端对象内，不写状态字典，也不写日志。

---

## 7. 系统服务（Windows）

### 7.1 构建服务宿主

服务不能用 `python.exe` 直接注册（SCM 只接受一个可执行路径），需要
PyInstaller 的 onedir 宿主：

```powershell
cd python
.\build.ps1
# 产物：dist\RuijieService\RuijieService.exe
```

可选部署到安装目录：

```powershell
.\build.ps1 -Deploy -DeployDir "D:\Storageredirect\Program\RuijieCampusAuth"
```

首次构建会安装 `.[build]` 依赖（pyinstaller、pillow、pywin32）。

### 7.2 安装与启停

菜单 `7) 系统服务`：

1. 先在菜单 2 添加账号并设为当前；
2. `1) 安装并启动`：
   - 写 `service.json`（LocalMachine DPAPI，ACL 只给 SYSTEM/Administrators）；
   - `CreateService` 注册 `RuijieCampusAuth`，自动启动；
   - 设置失败恢复 5s/10s/30s；
3. `2/3/4` 分别是启动、停止、卸载；`5` 刷新服务配置。

服务名由 `system_service.py` 的 `SERVICE_NAME` 定义，默认为
`RuijieCampusAuth`。

### 7.3 服务文件在哪

源码运行（未打包）时，服务目录是：

```text
python\RuijieCampusAuth\
  service.json
  service_status.json
  logs\service.log
```

也可以用环境变量 `RUIJIE_SERVICE_DIR` 覆盖服务目录。
打包/部署后，服务目录默认是 exe 所在目录。

Linux 没有内置 systemd 单元；需要开机自启时自行写 unit 调用 CLI，
或使用 C# 版的 `service install`。

---

## 8. 数据文件与路径

源码运行（Windows）：

```text
%APPDATA%\RuijieCampusAuth\
  accounts.json     账号池（密码 DPAPI，当前用户）
  settings.json     设置、当前档案、网卡、认证参数
  servers.json      已学习服务器
  discovery.json    探测缓存
```

源码运行（Linux）：`~/.config/RuijieCampusAuth/`。
打包/冻结后：程序目录（`sys.frozen` 时取 exe 目录）。

文件说明：

| 文件 | 内容 |
|---|---|
| `accounts.json` | 账号 id、用户名、密码密文、备注、启用 |
| `settings.json` | 当前账号 id、网卡、超时/重试、抢占、服务器锁定、档案字段、Portal/SAM/URL |
| `servers.json` | 服务器 MAC、名称、成功次数、最近成功时间 |
| `discovery.json` | 认证通道缓存（网卡 + 服务器 MAC） |
| `service.json` | 服务账号与设置（machine DPAPI） |
| `service_status.json` | `time` / `state` / `message`（+ 附加字段） |
| `logs/service.log` | 服务滚动日志 |

---

## 9. 学校档案

与 C# 完全相同的 JSON。示例：

```json
{
  "name": "示例大学",
  "school_code": "DEMO",
  "auth_mode": "EAPMD5",
  "service_name": "校园宽带",
  "static_id": "Static:00112233",
  "self_service_url": "http://self.example/",
  "update_url": "http://update.example/",
  "dhcp_mode": "none",
  "dhcp_timeout": 30,
  "dhcp_option82": false,
  "engine_version": "5.0.2014.108",
  "notes": "本学校档案"
}
```

导入后如果想确认实际生效值：

```bash
python - <<'PY'
import json, os
p = os.path.join(os.environ["APPDATA"], "RuijieCampusAuth", "settings.json")
print(json.dumps(json.load(open(p)), ensure_ascii=False, indent=2))
PY
```

---

## 10. 日志与排错

### 10.1 日志位置

- 单次 CLI/菜单认证：`./ruijie-auth.log`（默认当前目录）；
- 服务：`<服务目录>\logs\service.log`；
- 用 `--verbose` 把协议细节打到控制台。

### 10.2 常见错误

| 现象 | 原因与处理 |
|---|---|
| `ModuleNotFoundError: ruijie_auth` | 没安装：在 `python/` 下 `pip install -e .`，或设置 `PYTHONPATH=src` |
| `PermissionError` / 打开网卡失败 | 没用管理员（Windows）或 `sudo`（Linux）；Npcap 未装 |
| 列不出网卡 | Npcap 没勾 WinPcap 兼容；官方客户端占用；网卡被禁用 |
| 一直寻找服务器 | 网线不在校园网口；选错网卡；端口没开 802.1X；不在认证时段 |
| `账号或密码无法用 gb18030 编码` | 账号含特殊字符；确认 `--encoding` 与学校一致 |
| 认证失败但有中文消息 | 看服务器原文：密码错、MAC 绑定、已在线、时段限制 |
| 换机器后密码解不开 | `accounts.json` 是 DPAPI 当前用户加密，重新录入 |
| 菜单 7 提示找不到 RuijieService.exe | 先 `.\build.ps1`，或改用 C# 服务 |
| DHCP 菜单报错（Linux） | `dhcp_control` 依赖 Windows iphlpapi，Linux 不支持 |
| 服务启动后立刻停止 | 看 `service.log`；确认 `service.json` 账号/网卡非空 |

### 10.3 抓包对比

协议问题最有效的办法是同时抓官方客户端和本工具：

```bash
# Linux
sudo tcpdump -i eth0 -w eapol.pcap 'ether proto 0x888e'
```

对比点：帧总长度、EAPOL 长度字段是否只覆盖标准 EAP、私有尾是否 569 字节、
0x39/0x54 是否与服务端期望一致。

---

## 11. 测试与开发

```bash
cd python
PYTHONPATH=src python -m pytest -q
```

当前 60 项通过，覆盖：

- 私有尾结构/CRC/混淆/凭据加密（`test_protocols.py`、`test_tail_options.py`）；
- 真实抓包私有属性向量、RC4、MSCHAPv2 RFC2759（`test_protocols.py`）；
- 账号存储/DPAPI 往返（`test_storage.py`）；
- 档案导入、菜单逻辑（`test_menu.py` 等）；
- Guardian、服务状态、交接心跳。

只跑协议向量：

```bash
PYTHONPATH=src python -m pytest -q tests/test_protocols.py
```

---

## 12. 卸载

```bash
pip uninstall ruijie-campus-auth
```

再按需删除：

- `%APPDATA%\RuijieCampusAuth`（账号与设置）；
- `python\RuijieCampusAuth`（源码模式的服务配置与日志）；
- `dist\`、`build\`（PyInstaller 产物）。

如果装过 Windows 服务，先 `sc stop RuijieCampusAuth` 和
`sc delete RuijieCampusAuth`，或运行菜单 7 → 4。
