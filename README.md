# Ruijie Campus Auth HXxy

一个面向 Windows 和 Linux 的锐捷校园网认证客户端，同时提供命令行和 Python API。

> **当前状态：已在厦门华厦学院实网验证通过。** 2026-11-17 使用真实测试账号完成了
> 原版客户端抓包、私有尾逐字节重建和 Python 客户端登录。Python 客户端完成标准
> EAP-MD5 后会追加 569 字节锐捷私有尾，服务器返回 EAP-Success，日志显示
> “认证成功 / 欢迎使用厦门华厦学院网络”。

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
- 自动化测试覆盖私有尾长度/结构、CRC/混淆、凭据加密和 EAPOL 长度字段。

测试账号和密码不包含在源码、配置、README 或安装元数据中。
