# 锐捷认证器 · OpenWrt 版操作手册

面向第一次给路由器装认证插件的用户。读完你会完成：

1. 把 OpenWrt 固件/插件装到路由器；
2. 在 LuCI 里导入学校档案、添加账号、选 WAN 口；
3. 认证成功并看懂状态页；
4. 会用命令行、日志和自检排查问题；
5. 知道怎么升级、卸载、自己编译。

> 代码位置：`openwrt/`。技术细节与 C# 对照见 `openwrt/README.md`、
> `docs/OPENWRT_PORT.md`。本手册只讲怎么用。

---

## 1. 它是什么

路由器端的锐捷校园网 802.1X 认证器：

```text
LuCI 网页（8 个页面）
        │  UCI 配置 / 控制 socket / CLI JSON
        ▼
ruijie-auth 守护进程（Go，procd 管理）
        │  AF_PACKET 原始套接字
        ▼
WAN 口 ── EAPOL/802.1X ── 校园网交换机
```

特点：

- **纯用户态、无专属 kmod 依赖**：认证器本身不装任何内核模块，同一个
  二进制在同架构 OpenWrt 上都能跑；镜像中的 nftables kmod 来自
  LuCI/firewall4 基础系统；
- **WAN 自动识别**：ubus 逻辑接口 + 默认路由 + 链路状态，下拉框可多选；
- **学校差异走档案**：与桌面版同一份 `profiles/*.json`；
- **全中文 LuCI**：8 个页面，随 ipk 安装 `.lmo` 翻译；
- 支持 EAP-MD5、PEAP+MSCHAPv2；EAP-TLS 协议栈已实现，但需客户端证书，
  当前未开放配置入口。

---

## 2. 支持范围

| 项 | 说明 |
|---|---|
| 固件 | OpenWrt 24.10（x86_64 与 ramips/mt7621 实测） |
| 架构 | 默认 `mipsle`（mipsel_24kc）；换架构改 `GOARCH` 重编 |
| 设备 | 不绑定机型；R3G 只是默认 Image Builder `PROFILE` |
| 依赖 | 运行时只有 `libc`、`luci-base`、`rpcd` |
| 认证器不需要 | 专属 kmod、UA3F、NFQUEUE、iptables 扩展、USB 网卡驱动 |

> 上表是**认证器 ipk** 的依赖边界。固件镜像另含 UA3F、https-dns-proxy、
> 防检测 kmod 和 Vantage 主题，这些是整机功能，不是 ipk 的依赖。
>
> 2026-11-21 实测补充：固件内置 UA3F 3.7.0、https-dns-proxy（自定义
> AdGuardHome DoH）、Vantage 主题 + app；IPID 由 nftables
> `ip id set 0` 改写，UA3F 只排队 TCP SYN；外网探测为 TCP 53 四目标 +
> HTTP 204 回退，`ruijie-auth probe` 可自检；日志页为 logs2。

> 只要路由器能跑 OpenWrt 24.10、有可用 WAN 口，就能用。无线中继/AP 模式
> 不适合 802.1X 有线认证。

---

## 3. 安装

有两条路：刷整机镜像（带 LuCI 和插件），或只装 ipk（已有 OpenWrt）。

### 3.1 方式 A：刷整机镜像（新手推荐）

构建产物（在 Ubuntu 上生成）：

```text
openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-sysupgrade.bin
openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-kernel1.bin
openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-rootfs0.bin
openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-breed.bin
```

#### 普通 OpenWrt / 已有 Breed 的设备

1. 电脑网线接路由器 **LAN** 口，浏览器打开 `http://192.168.1.1`。
2. 登录 LuCI。
3. **系统 → 备份/升级 → 刷写新固件**，选择 `...sysupgrade.bin`。
4. 第一次刷机建议**不勾**“保留配置”，避免旧配置冲突。
5. 等待重启（2–3 分钟），重新打开 `192.168.1.1`。

#### 小米 R3G（Breed）

Breed 不认 OpenWrt 的 sysupgrade tar，必须用重打包的 `...breed.bin`：

1. 断电，按住复位孔上电，橙灯闪烁后松开；电脑接 LAN 口。
2. 打开 `http://192.168.1.1`，进 Breed 的“固件更新 → 常规固件”。
3. 闪存布局选“小米 R3G OpenWrt”，分别上传
   `...-squashfs-kernel1.bin` 和 `...-squashfs-rootfs0.bin`。
   实测落点：kernel1 → `0x600000`，rootfs0 → `0xA00000`，
   kernel0（`0x200000`）保留原厂。
4. 在“环境变量”确认 `autoboot.command = boot flash 0x600000`。
5. 刷写完成后重启。

> Breed 1.2 的“小米 R3G 原厂 → 固件”不接受合并的
> `...-squashfs-breed.bin`（预处理失败）。该合并镜像保留给其他
> 写入方式或整包恢复，常规刷机用上面的 kernel1 + rootfs0。

> 已经在跑标准 OpenWrt 布局的设备，继续用 LuCI/sysupgrade 升级即可，
> 不需要每次重打包 Breed 镜像。

### 3.2 方式 B：只装 ipk（已有 OpenWrt，免刷机）

产物：`ruijie-auth_0.9.0_mipsel_24kc.ipk`（架构按你的设备选）。

```bash
# 电脑上把 ipk 传到路由器
scp ruijie-auth_0.9.0_mipsel_24kc.ipk root@192.168.1.1:/tmp/

# 路由器上安装
ssh root@192.168.1.1
opkg install --force-reinstall /tmp/ruijie-auth_0.9.0_mipsel_24kc.ipk
```

也可以在 LuCI **系统 → 软件包 → 上传软件包**。

安装后：

- `/etc/config/ruijie-auth` 是 conffile，升级不会覆盖账号与设置；
- 服务会自动重启；LuCI 页面强制刷新一次（Ctrl+F5）。

### 3.3 从改名前的旧包升级

旧包的配置文件名不同，不会自动迁移：

```bash
# 停旧服务（旧服务名以你机器上的为准）
/etc/init.d/<旧服务名> stop

# 手工把旧配置内容复制到新文件
cp /etc/config/<旧配置名> /etc/config/ruijie-auth

# 安装新 ipk，然后启用新服务
opkg install --force-reinstall /tmp/ruijie-auth_0.9.0_mipsel_24kc.ipk
/etc/init.d/ruijie-auth enable
/etc/init.d/ruijie-auth restart
```

---

## 4. 第一次配置（LuCI）

浏览器打开 `http://192.168.1.1`，登录后菜单在 **服务 → 锐捷认证器**。

### 4.1 导入学校档案

进入 **学校档案** 页：

**方式一：上传/粘贴（推荐）**

1. 点“选择文件”，选 `profiles/<学校>.json`；
2. 点 **校验**，页面显示档案名、认证方式、服务名、静态 ID；
3. 点 **应用档案**。

**方式二：从已安装文件导入**

把 JSON 放到 `/etc/ruijie-auth/profiles/`：

```bash
scp "<学校>.json" root@192.168.1.1:/etc/ruijie-auth/profiles/
```

页面“已安装档案”列表会出现它，点 **导入** 即可。

导入会：

- 只覆盖档案声明的字段；
- 先备份 `/etc/config/ruijie-auth.bak-<时间戳>`；
- 账号池保持不变；
- 自动通知守护进程重载。

### 4.2 添加账号

进入 **账号** 页 → **添加**：

- 用户名、密码必填；
- 备注可选；
- “启用”默认开；
- “当前”勾一个（当前账号优先，失败后按顺序尝试其它启用账号）。

最多 5 个账号。

### 4.3 选择 WAN 口

进入 **设置 → 认证**：

- **WAN 接口** 默认是“自动识别校园网 WAN 口”；
- 下拉里会列出所有网卡（名称 + 逻辑接口 + 链路状态）；
- 可以多选：守护进程按顺序尝试，优先链路已连接的；
- 认不出时，先看 **调试 → 已识别接口**，确认哪个是接校园网的物理口。

### 4.4 开始认证

进入 **概览**：

1. 看“状态”：应为 `认证中` → `已认证`；
2. 点 **立即认证** 可手动触发；
3. “服务器私有属性”表会显示服务器回的属性（
   `SU_REAUTH_INTERV`、`INDICATE_PORT`、`ENCRYPT_KEY` 等）。

认证成功后，概览会显示账号、接口、服务器 MAC、认证方式、档案名、
重认证间隔。

---

## 5. LuCI 页面逐页说明

### 概览

| 项 | 含义 |
|---|---|
| 状态 | 启动中/认证中/已认证/重试中/已暂停/已注销/已停用/错误/已停止 |
| 消息 | 最近一条中文状态 |
| 账号 / 接口 / 服务器 | 当前使用 |
| 认证方式 | EAP-MD5 / PEAP / EAP-TLS |
| 学校档案 | 最近导入的档案名 |
| 重认证间隔 | 服务器 `SU_REAUTH_INTERV` 优先，否则本地配置 |
| 服务器私有属性 | 名称 / 值 / 十六进制 |

按钮：立即认证、注销、暂停、恢复、重载配置。

### 账号

账号池表格：用户名、密码（弹窗编辑）、备注、启用、当前。
保存时校验最多 5 个账号，然后重载守护进程。

### 学校档案

- 当前生效的档案字段（名称、认证方式、学校代码、服务名、静态 ID、
  自助/升级/Portal 地址、DHCP 模式、引擎版本）；
- 已安装档案列表（`/etc/ruijie-auth/profiles` 和
  `/usr/share/ruijie-auth/profiles`）；
- 上传/粘贴 JSON，校验后应用。

### 认证服务器

- **已学习的认证服务器**：只要回过 EAP 包就会入库，记录 MAC、名称、
  成功次数、最近成功时间；
- **锁定**：之后 EAPOL-Start 单播到该 MAC，并忽略其它服务器；
- **探测缓存**：记录最近一次成功使用的接口/服务器，可清除。

### 设置

五个分页：

**认证**
- 启用认证
- 认证方式（EAP-MD5 / PEAP+MSCHAPv2 / EAP-TLS）
- WAN 接口（多选，`auto` 默认）
- 服务名（0x39）、静态 ID（0x54）、学校代码

**协议**
- 认证超时、Start 次数、Start 间隔、每次请求帧数、响应延迟
- 定时重认证间隔（服务器值优先）
- 附加私有尾、认证前先发注销、停止时发注销

**守护**
- 自动守护、在线检测间隔、定时抢占
- 非 802.1X 网络时暂停、重试间隔、连通性探测 URL、探测缓存

**档案数据**
- 自助/升级/Portal 地址、SAM 主机/端口
- DHCP 模式/超时/Option 82
- AD 域/超时、引擎版本

**其他**
- 帧伪装（关 / 基础 TTL+MSS）
- 日志级别（error / info / debug）

保存后会自动重载守护进程并重启伪装服务。

### MAC 地址

- 查看当前网络配置节的已配置 MAC 与实际 MAC；
- 设置 MAC / 随机 MAC / 恢复硬件 MAC；
- 修改会写 `network.wan.macaddr`、执行 `ifup wan`、重载守护进程，
  链路会短暂中断。

适用：学校把账号绑定到笔记本网卡 MAC，需要克隆原 MAC。

### 调试

- 版本信息；
- 已识别接口列表（设备、逻辑接口、MAC、链路、角色）；
- **运行自检**：RC4、MD4、MSCHAPv2、569 字节私有尾、混淆往返、
  私有属性解析；
- 导出接口/状态、清除探测缓存；
- 原始 `status.json`。

### 日志

显示 `/var/log/ruijie-auth.log` 的最后部分，每 3 秒刷新。

---

## 6. 认证方式与参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `auth_mode` | EAPMD5 | `EAPMD5` / `PEAP` / `EAP-TLS`（也接受 `MD5`、`PEAP-MSCHAPV2`、`TLS`） |
| `private_tail` | 1 | 是否附加 569 字节私有尾 |
| `logoff_first` | 1 | 认证前先发 EAPOL-Logoff 清理旧会话 |
| `timeout` | 20 | 单轮认证总超时（秒） |
| `retries` | 4 | EAPOL-Start 次数 |
| `repeats` | 3 | 每个请求重复发送次数 |
| `start_timeout` | 3 | Start 重试间隔（秒） |
| `response_delay` | 0.04 | 应答前延迟（秒），模仿原客户端 |
| `reauth_interval` | 21600 | 定时重认证；服务器 `SU_REAUTH_INTERV` 优先 |
| `check_interval` | 2 | 自动守护的连通性检测间隔 |
| `retry_interval` | 3 | 失败后重试间隔 |
| `autoguard` | 1 | 探测失败时自动重新认证 |
| `preempt` | 1 | 周期性重认证保持会话 |
| `pause_on_home` | 1 | 无认证服务器但网络在线时暂停 |
| `dhcp_mode` | none | `before`/`after` 目前只记录，不驱动 udhcpc |

PEAP / EAP-TLS 的服务端证书校验当前关闭（与 C# 常规路径一致），适合
校园网自签 RADIUS；协议栈已实现，但请在真实环境先验证再长期使用。
EAP-TLS 是双向认证，必须提供客户端证书；OpenWrt 版暂未开放证书配置
入口，因此实际可用的方式是 `EAPMD5` 与 `PEAP`。

---

## 7. WAN 识别规则

守护进程每个重试周期重新枚举，不需要重启：

1. 用 `ubus call network.interface dump` 取逻辑接口与 `l3_device`；
2. 读 `/proc/net/route` 找默认路由接口；
3. 读 `/sys/class/net/<dev>/carrier` 判断链路；
4. 打分排序：逻辑名含 `wan`/`usbwan`/`wwan`、默认路由、`wan*` 前缀、
   链路 up；
5. 配置为 `auto` 时展开为所有“WAN + 链路 up”的候选；
6. 用户多选时按列表顺序保留，优先选第一个链路 up 的。

调试页的“已识别接口”会标注 `Campus WAN` / `Other` 与 `Link up`。

USB 共享：插手机开启 USB 网络共享后，`/etc/hotplug.d/net/90-ruijie-usbwan`
会创建 `usbwan`（DHCP，metric 100）备用 WAN。需要设备本身有 USB 网卡
驱动；插件不依赖这些 kmod。

---

## 8. 命令行（SSH）

```text
ruijie-auth version
ruijie-auth status [--json]
ruijie-auth auth|logoff|pause|resume|reload
ruijie-auth daemon                    # 由 procd 调用，不要手动常驻
ruijie-auth accounts [--json]
ruijie-auth servers list|lock|unlock|remove|rename
ruijie-auth discovery show|clear
ruijie-auth mac show|set|random|clear [--section wan]
ruijie-auth interfaces [--json] [--auto]
ruijie-auth profile list|show|import [--file PATH] [--config PATH]
ruijie-auth tail [--username ...] [--mac ...]
ruijie-auth selftest
```

常用示例：

```bash
# 看状态
ruijie-auth status --json

# 手动认证/注销
ruijie-auth auth
ruijie-auth logoff

# 看候选网口
ruijie-auth interfaces

# 导入档案
ruijie-auth profile list
ruijie-auth profile import --file "/tmp/<学校>.json"

# 锁定服务器
ruijie-auth servers list
ruijie-auth servers lock 00:1a:a9:17:ff:ff

# 改 MAC
ruijie-auth mac set --section wan aa:bb:cc:dd:ee:ff
ruijie-auth mac random --section wan
ruijie-auth mac clear --section wan

# 设备自检
ruijie-auth selftest
```

`selftest` 期望输出：

```text
rc4-vector                   通过 bbf316e8d940af0ad3
md4-vector                   通过 a448017aaf21d8525fc10ae87aa6729d
mschapv2-nt-response         通过 82309ecd8d708b5ea08faa3981cd83544233114a3d85d6df
private-tail                 通过 长度=569 错误=<nil>
obfuscate-involution         通过 ffff3777afff
private-properties           通过 错误=[] 属性=1
自检通过
```

任一 `失败` 说明二进制损坏或架构不符（比如把 mips 包装到 arm 上）。

---

## 9. 状态文件与日志

### 9.1 status.json

路径 `/var/run/ruijie-auth/status.json`：

```json
{
  "time": "2026-11-20T01:00:00+08:00",
  "state": "authenticated",
  "message": "认证成功: 欢迎使用……",
  "account": "S12016004",
  "interface": "wan",
  "devices": ["wan"],
  "server": "00:1a:a9:17:ff:ff",
  "auth_mode": "EAPMD5",
  "profile": "<学校档案名>",
  "reauth_interval": 21600,
  "server_messages": ["欢迎使用……"],
  "properties": [
    {"name": "SU_REAUTH_INTERV", "type": 86, "value": "0", "hex": "00000000"},
    {"name": "ENCRYPT_KEY", "type": 92, "value": "7c73533126403871", "hex": "7c73533126403871"}
  ]
}
```

`state` 取值：`starting`、`authenticating`、`authenticated`、`retrying`、
`paused`、`idle`、`disabled`、`error`、`stopped`。

### 9.2 日志

```bash
logread -e ruijie-auth          # procd/系统日志
tail -f /var/log/ruijie-auth.log
```

日志 1MB 轮转。`log_level=debug` 会打印 PEAP 步骤与服务器属性。

---

## 10. 常见问题

### 10.1 一直“正在寻找认证服务器...”

1. 网线是否接在校园网口（不是 LAN 口/路由器级联口）；
2. 设置里的 WAN 接口是否选对：调试页看“已识别接口”；
3. 交换机端口是否启用 802.1X、是否需要网页注册/绑定；
4. 学校是否绑定 MAC：用 MAC 页克隆笔记本 MAC；
5. 是否在认证时段外；
6. 抓包确认：

```bash
opkg install tcpdump
tcpdump -i wan -w /tmp/eapol.pcap 'ether proto 0x888e'
```

把 pcap 拿回电脑用 Wireshark 看有没有 Identity Request。

### 10.2 认证失败

- 概览的“消息”会显示服务器返回的中文原文；
- 常见：密码错、账号不存在、MAC 绑定、已在线、不在认证时段；
- 核对档案里的服务名/静态 ID 是否与学校一致；
- `auth_mode` 是否选错（学校用 PEAP 时选 EAP-MD5 会失败）。

### 10.3 “已暂停：未检测到认证服务器，当前网络在线”

这是 `pause_on_home` 的设计行为：没有认证服务器响应，但探测 URL 可达，
说明当前不是校园网，暂停等待。接回校园网后点“恢复”或等待自动恢复。

### 10.4 设置页没有网卡下拉

- 确认 `rpcd` 正常运行：`/etc/init.d/rpcd restart`；
- 刷新页面（Ctrl+F5）；
- 命令行 `ruijie-auth interfaces` 看后端是否识别。

### 10.5 中文界面没生效

- LuCI 语言要是“简体中文”：**系统 → 系统 → 语言和界面**；
- 确认文件存在：
  `ls /usr/lib/lua/luci/i18n/ruijie-auth.zh-cn.lmo`；
- 清浏览器缓存/强刷。

### 10.6 ipk 装不上

- `opkg install` 报架构不符：确认 `_mipsel_24kc` / `_aarch64_...` 与设备
  一致（`ubus call system board` 看 `architecture`）；
- 空间不足：`df -h /overlay`；先删旧包或换大闪存；
- 签名问题：本地构建的包未签名，`opkg install --force-reinstall` 可绕过。

### 10.7 服务起不来

```bash
/etc/init.d/ruijie-auth status
logread -e ruijie-auth | tail -30
/usr/sbin/ruijie-auth selftest
```

- 二进制架构不符 → 重装对应架构的 ipk；
- 配置损坏 → `uci show ruijie-auth`，必要时 `cp /etc/config/ruijie-auth.bak-*`；
- 端口被占/权限问题 → 检查 `/var/run/ruijie-auth.sock` 与 0600 权限。

### 10.8 改了 MAC 后网络不通

```bash
ruijie-auth mac show --section wan
ruijie-auth mac clear --section wan   # 恢复硬件 MAC
```

`ifup wan` 会短暂断链；USB 共享/多 WAN 时确认默认路由没被抢。

### 10.9 想恢复出厂

```bash
firstboot -y && reboot
```

或者 LuCI **系统 → 备份/升级 → 恢复出厂设置**。会清空
`/etc/config/ruijie-auth` 与档案，请先备份：

```bash
cp /etc/config/ruijie-auth /tmp/
tar -czf /tmp/ruijie-profiles.tgz /etc/ruijie-auth/profiles
```

---

## 11. 升级、卸载、备份

### 升级 ipk（保留配置）

```bash
opkg install --force-reinstall /tmp/ruijie-auth_<新版本>_<arch>.ipk
```

`/etc/config/ruijie-auth` 是 conffile，账号/设置/档案路径不变。

### 卸载

```bash
/etc/init.d/ruijie-auth stop
/etc/init.d/ruijie-auth disable
opkg remove ruijie-auth
rm -rf /etc/ruijie-auth          # 账号、服务器、档案（确认后再删）
```

### 备份

```bash
tar -czf /tmp/ruijie-backup.tgz \
  /etc/config/ruijie-auth /etc/ruijie-auth
```

---

## 12. 自己编译

编译在 Linux（Ubuntu）上进行，Windows 只编辑源码。

### 12.1 只编译守护进程

```bash
cd openwrt
sh scripts/build-go.sh                 # 默认 mipsle
GOARCH=arm64 sh scripts/build-go.sh    # 换架构
```

产物：`openwrt/build/ruijie-auth`。

### 12.2 打 ipk

```bash
cd openwrt
sh scripts/build-ipk.sh
```

需要 `python3`（生成中文 `.lmo`）；没有 python3 时自动跳过翻译。
产物：`openwrt/build/ipk/ruijie-auth_<version>_<arch>.ipk`。

### 12.3 打整机固件

依赖 OpenWrt Image Builder：

```bash
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
PROFILE=xiaomi_mi-router-3g \
  sh scripts/build-image.sh
```

默认包集合只有 `luci-ssl luci-i18n-base-zh-cn` + 本插件；认证器自身不引入
kmod，镜像中的 nftables kmod 随 firewall4 基础系统安装。需要额外软件包时：

```bash
PACKAGES="luci-ssl luci-i18n-base-zh-cn tcpdump" sh scripts/build-image.sh
```

Ubuntu 26.04 的 `install` 是 uutils 版本，Image Builder 会检查失败：

```bash
mkdir -p /opt/openwrt/gnu-bin
ln -sf /usr/bin/gnuinstall /opt/openwrt/gnu-bin/install
PATH=/opt/openwrt/gnu-bin:$PATH sh scripts/build-image.sh
```

输出在 Image Builder 的
`bin/targets/ramips/mt7621/`，脚本会打印路径。

### 12.4 版本号

`VERSION=0.9.0 sh scripts/build-ipk.sh` 覆盖包版本；镜像里的
`ruijie-auth version` 显示 `build-go.sh` 的 `VERSION`（默认 0.9.0）。

---

## 13. 安全说明

- `/etc/config/ruijie-auth` 明文保存账号密码，权限 0600；
  需要更强保护时自行加文件加密或改用账号池轮换。
- 控制 socket `/var/run/ruijie-auth.sock` 权限 0600。
- `/etc/ruijie-auth` 与 `profiles/` 权限 700（uci-defaults 设置）。
- 不要把真实账号、抓包、档案提交到公开仓库。
- 本项目与锐捷网络无关联；请确保你有校园网合法接入权限，并遵守学校规定。

---

## 14. 一页速查

```text
LuCI:      服务 → 锐捷认证器
配置:      /etc/config/ruijie-auth
档案:      /etc/ruijie-auth/profiles/*.json
状态:      /var/run/ruijie-auth/status.json
日志:      /var/log/ruijie-auth.log
socket:    /var/run/ruijie-auth.sock
服务:      /etc/init.d/ruijie-auth
自检:      ruijie-auth selftest
重载:      ruijie-auth reload
```
