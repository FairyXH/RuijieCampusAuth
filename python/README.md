# 锐捷认证器 · Python 版

与官方锐捷（Ruijie）802.1X 协议兼容的第三方校园网认证客户端，Python 实现。
协议算法、调用链和函数边界与仓库根目录的 C# 版逐模块对应，学校差异通过
`profiles/*.json` 档案导入。**没有 GUI**，入口是数字菜单 CLI，同时保留
argparse 子命令和 Python API 供脚本使用。

> 详细的分步操作说明见 [`docs/MANUAL_PYTHON.md`](../docs/MANUAL_PYTHON.md)。

## 环境要求

- Python 3.10 或更高版本。
- 收发二层 EAPOL 帧需要原始套接字权限：
  - Windows：安装 [Npcap](https://npcap.com/)，安装时勾选 **WinPcap API
    兼容模式**；终端用管理员身份运行。
  - Linux/macOS：安装 libpcap（Debian/Ubuntu：`sudo apt install libpcap-dev`），
    运行时用 `sudo`。
- 依赖只有 `scapy`（`pyproject.toml` 已声明）。

## 安装

Windows（管理员 PowerShell）：

```powershell
cd python
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -e .
```

Linux：

```bash
cd python
python3 -m venv .venv
. .venv/bin/activate
pip install -e .
```

安装后会得到两个命令：

| 命令 | 作用 |
|---|---|
| `ruijie-auth` | argparse 命令行（也给菜单入口用） |
| `ruijie-auth-menu` | 直接进入数字菜单 |

直接从仓库源码运行也可以：

```bash
python -m ruijie_auth          # 无参数 → 菜单
python -m ruijie_auth --help   # 有参数 → argparse
```

## 快速开始

### 1. 交互菜单（推荐给日常使用）

```bash
ruijie-auth
```

首次进入会显示：

```text
========================================
  锐捷校园网认证器（Python 版）
  学校档案: 未导入
  当前状态: 未检测
========================================
 1) 认证管理
 2) 账号池
 3) 认证服务器
 4) 网络与 DHCP
 5) 诊断工具
 6) 调试功能
 7) 系统服务
 8) 学校配置档案
 9) 查看日志
 0) 退出
```

推荐顺序：`8` 导入学校档案 → `2` 添加账号 → `1` 选择网卡并认证。

### 2. 一次性命令行认证

```bash
# 自动探测收到锐捷响应的网卡，交互输入账号密码
sudo ruijie-auth

# 指定账号，密码隐藏输入
sudo ruijie-auth -u YOUR_ID

# 完全非交互（注意 shell 历史/进程列表会留下密码）
sudo ruijie-auth -u YOUR_ID -p 'YOUR_PASSWORD' -i enp3s0
```

Windows 把 `sudo` 换成管理员 PowerShell，直接运行 `ruijie-auth`。

### 3. 常用调试选项

```bash
ruijie-auth --list-interfaces          # 列出网卡
ruijie-auth --logoff -i eth0           # 只发 EAPOL-Logoff
ruijie-auth --no-private -u ID         # 只发标准 EAP-MD5，不带私有尾
ruijie-auth -u ID -i eth0 -v           # 详细协议日志
```

## 菜单说明

| 菜单 | 功能 |
|---|---|
| 1) 认证管理 | 开始认证、注销、网关/DNS/HTTP 连通测试 |
| 2) 账号池 | 添加、更新、删除、启用/停用、设为当前 |
| 3) 认证服务器 | 查看已学习服务器、锁定/取消锁定、清空 |
| 4) 网络与 DHCP | 续租/释放/释放并续租/查看租约、设置 `dhcp_mode` |
| 5) 诊断工具 | 网络探测、ARP 监控、代理检测、拨号检测、OUI 识别、小锐医生管道 |
| 6) 调试功能 | Portal 登录/注销/在线信息/页面信息、自助服务、客户端中心、升级检查、SAM 探测、私有属性解析 |
| 7) 系统服务 | 安装并启动、启动、停止、卸载、应用服务配置 |
| 8) 学校配置档案 | 扫描档案、导入/切换 |
| 9) 查看日志 | 显示服务日志最后 60 行 |

菜单状态栏读取 `service_status.json`；没有安装/运行服务时显示“未检测”。

## 命令行参数

```text
-i, --interface NAME     网卡名称、描述或 NPF 设备名
--list-interfaces        列出网卡后退出
-u, --username USER      校园网用户名
-p, --password PASS      校园网密码（省略时隐藏输入）
--encoding ENC           账号密码编码，默认 gb18030
--timeout SEC            认证超时，默认 30
--retries N              EAPOL-Start 次数，默认 3
--repeats N              每个请求的发送次数，默认 3
--start-timeout SEC      Start 重试间隔，默认 3
--no-private             不附加 569 字节私有尾（调试）
--server MAC             锁定认证服务器，Start 单播到该地址
--service-name NAME      私有尾 0x39 服务名（默认见档案/设置）
--static-id ID           私有尾 0x54 静态 ID
--log-file PATH          日志文件，默认 ./ruijie-auth.log
--logoff                 只发送注销
-v, --verbose            详细日志
```

退出码：`0` 成功，`1` 认证失败，`2` 参数/账号错误。

## Python API

```python
import ruijie_hxxy   # 兼容别名，也可 from ruijie_auth import RuijieClient

client = ruijie_hxxy.login("YOUR_ID", "YOUR_PASSWORD")
print(client.status()["authenticated"])
print(client.status()["message"])

client.logout()
```

需要先配置、后执行时：

```python
from ruijie_auth import RuijieClient

client = RuijieClient(
    "YOUR_ID", "YOUR_PASSWORD",
    interface="enp3s0",     # None = 自动探测
    timeout=30,
    retries=3,
    repeats=3,
    private_tail=True,
    service_name="",        # 留空时使用设置/档案
    static_id="",
    log_file="ruijie-auth.log",
)
ok = client.login()
print(client.status())
```

`status()` 返回可 JSON 序列化的快照：`state`、`authenticated`、`message`、
`interface`、`interface_description`、`ip`、`mac`、`started_at`、
`finished_at`、`logs`。密码不会出现在状态或日志里。

## 学校配置档案

档案目录按以下顺序合并扫描，去重：

1. 仓库根 `profiles/`
2. 当前工作目录 `profiles/`
3. `discover_profiles()` 的附加目录（菜单/调用方传入）

导入语义与 C# 版一致：

- 只覆盖档案中非空的字段；
- 账号池、网卡、服务开关不动；
- 先备份 `settings.json.bak-<时间戳>`；
- 如果 `service.json` 存在，同时把新配置写入，运行中的服务下一轮生效。

档案字段见 [`profiles/README.md`](../profiles/README.md)。

## 数据文件

源码运行时（Windows）：

```text
%APPDATA%\RuijieCampusAuth\
  accounts.json          账号池（Windows DPAPI 加密）
  settings.json          GUI/菜单/服务共用设置与档案
  servers.json           已学习认证服务器
  discovery.json         探测缓存
```

打包/部署后以程序目录为准（`sys.frozen` 时使用 exe 所在目录）。服务模式：

```text
<服务目录>\
  service.json           服务账号与配置（LocalMachine DPAPI）
  service_status.json    服务状态
  logs\service.log       服务日志（轮转）
```

Linux 源码运行时是 `~/.config/RuijieCampusAuth/`（遵循 `XDG_CONFIG_HOME`）。

## 系统服务

Windows 服务名：`HXXY-CampusAuth`（历史名，已部署环境沿用，避免迁移时丢配置）。
它以 LocalSystem 在登录前认证，读取 `service.json`，按设置做抢占保活。

安装前需要先构建服务宿主（PyInstaller onedir）：

```powershell
cd python
.\build.ps1                 # 生成 dist\RuijieService\RuijieService.exe
# 可选：部署到安装目录
.\build.ps1 -Deploy -DeployDir "D:\Storageredirect\Program\RuijieCampusAuth"
```

然后在菜单 `7) 系统服务 → 1) 安装并启动`。源码模式下没有 `RuijieService.exe`
时，菜单会明确报错而不是注册一个无法启动的服务。

Linux 没有内置 systemd 单元；需要开机自启时自行写 unit 调用
`ruijie-auth -u ... -p ...`（更推荐用 C# 版的 `service install`，它会写
`ruijie-campus-auth.service`）。

## 测试

```bash
cd python
PYTHONPATH=src python -m pytest -q
```

当前 60 项通过：协议向量（私有属性真实抓包、RC4、MSCHAPv2 RFC2759、
DPAPI 往返、档案导入、菜单逻辑、Guardian、存储、服务状态）。

## 协议模块

`src/ruijie_auth/protocols/` 与 C# `Core/Protocols/` 一一对应：

| Python | C# |
|---|---|
| `rc4.py` | `Rc4.cs` |
| `private_properties.py` | `PrivateProperties.cs` |
| `mschap_v2.py` | `MschapV2.cs` |
| `auth_method.py` | `AuthMethod.cs` |
| `peap_session.py` | `PeapSession.cs` + `EapTlsStream.cs` |
| `pcap_eap_transport.py` | `PcapEapTransport.cs` |
| `peap_vendor_segments.py` | `PeapVendorSegments.cs` |
| `dhcp_control.py` | `DhcpControl.cs` |
| `http_json_client.py` | `HttpJsonClient.cs` |
| `clients.py` | Portal/ClientCenter/SelfService/SAM/Upgrade |
| `wlan_profile_manager.py`（`cli.py` 内） | `WlanProfileManager.cs` |

`src/ruijie_auth/diagnostics/` 对应 C# `Diagnostics/`。

## 已知限制

- 服务模式的功能覆盖以 C# 版为准；Python 版适合脚本、研究与 Linux 场景。
- Portal/SAM/无线等实验功能在菜单 `6)` 里单独触发，不参与主认证流程。
- 账号文件在 Windows 使用 DPAPI；换用户或换机器后无法解密，需要重新录入。
- `--password` 会出现在 shell 历史和进程列表里，多人环境请用隐藏输入。
