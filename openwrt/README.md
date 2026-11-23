# 锐捷认证器 · OpenWrt 版

OpenWrt 软路由端的锐捷校园网 802.1X 认证器：Go 守护进程 + LuCI 管理界面。
协议算法与桌面版（根目录 C# / .NET 10）一一对应，学校差异只通过
`profiles/*.json` 档案导入，产品本身不内置任何学校配置。

## 设计约束

- **认证器纯用户态，无专属 kmod 依赖**：这条约束只针对认证器 ipk，不限制
  固件。认证器只用到 Linux 自带的 AF_PACKET 原始套接字和 Go 标准库，
  `opkg` 依赖只有 `libc`、`luci-base`、`rpcd`；同一二进制在任何同架构
  OpenWrt 上都能跑。固件本身按需包含 UA3F、https-dns-proxy 及相应 kmod。
- **R3G 是实测载体，不是适用范围**：预编译 ipk 不绑定 R3G，同架构、
  OpenWrt 版本相近的设备可直接安装；R3G 定制固件用于验证整机功能，
  后续会补充更多机型固件。
- **不做机型绑定**：WAN 口由 daemon 自动识别（ubus 逻辑接口 + 默认路由 +
  链路状态），也可在 LuCI 里多选候选网口。
- **协议一致**：私有尾、CRC16/XMODEM、字节混淆、MD5 链式凭据加密、
  服务器私有属性分段解析、RC4、MSCHAPv2、PEAP/EAP-TLS 都是 C# 实现的
  逐函数移植，测试向量与桌面版共用。
- **配置通用**：`profiles/*.json` 字段与 C# `SchoolProfile` 完全一致，
  同一份档案在桌面版和路由器上都能导入。

## 目录

```text
openwrt/
  ruijie-auth/                 Go 守护进程 + CLI（module: ruijie-auth）
    cmd/ruijie-auth/           入口：daemon / status / profile / ...
    internal/protocol/         协议算法层（与 C# Core/Protocols 对应）
    internal/supplicant/       EAP-MD5 与 PEAP/EAP-TLS 会话状态机
    internal/profile/          学校档案加载与 UCI 导入
    internal/config/           UCI 配置解析
    internal/netcfg/           WAN 候选接口识别、MAC 修改
    internal/servers/          已学习认证服务器 + 探测缓存
    internal/daemon/           常驻循环、账号轮换、守护
    internal/transport/        AF_PACKET 收发
  luci-app-ruijie-auth/        LuCI 应用
    htdocs/.../view/ruijie-auth/ 8 个页面
    po/zh_Hans/ruijie-auth.po    简体中文翻译
    root/.../menu.d、acl.d、uci-defaults
  files/                       ipk 里的系统文件（init、UCI 默认值、hotplug）
  vendor/                      固定版本的外部插件（UA3F/OpenClash/mihomo/Vantage）
  scripts/
    build-go.sh                交叉编译
    build-ipk.sh               打 ipk（含 LuCI 与中文 .lmo）
    build-image.sh             组装固件
    fetch-imagebuilder.sh      下载并校验固定版本 Image Builder
    kmods-all.txt              目标 feed 的完整 kmod 清单
    po2lmo.py                  把 .po 编译成 LuCI 的 .lmo
```

## 产物镜像

预编译产物按 OpenWrt 原生层级组织，便于本地 opkg 源或文件服务器直接发布：

```text
ipk/<arch>/<package>/<version>/<file>.ipk
firmware/<target>/<subtarget>/<profile>/<version>/...
```

目录约定、当前产物和校验方式见 [`ipk/README.md`](ipk/README.md)、
[`firmware/README.md`](firmware/README.md)。

## 源码与编译分工

源码统一在实体机仓库编辑、提交；编译和测试只在 Ubuntu VM 进行。
Ubuntu 侧：

```sh
cd openwrt
sh scripts/build-go.sh      # 只编译 daemon
sh scripts/build-ipk.sh     # 打 ipk（默认 mipsle，可用 GOARCH= 改）
sh scripts/fetch-imagebuilder.sh  # 首次构建：下载并校验 24.10.0 IB
sh scripts/build-image.sh   # 打 R3G 全功能固件
sh scripts/build-target.sh scripts/profiles/<profile>.env  # 单机型 lite/standard
sh scripts/build-wave1.sh   # Wave 1 全部 mipsel 机型
sh scripts/build-arches.sh  # 全架构 ruijie-auth ipk
```

`build-image.sh` 优先使用 `vendor/` 里的固定版本插件，其次才联网下载；
Image Builder 的版本和 SHA256 由 `fetch-imagebuilder.sh` 固定。因此同一
份仓库在干净环境里可以复现同一套包和内核 ABI。

`build-target.sh` 读取 `scripts/profiles/*.env`，按 `scripts/packages/lite.txt`
或 `standard.txt` 组装镜像，并把产物写入 `firmware/<target>/<subtarget>/
<profile>/<version>/`。Wave 1 的 15 个机型结果见
[`docs/WAVE1_BUILD.md`](../docs/WAVE1_BUILD.md)。

`build-ipk.sh` 需要 `python3` 生成中文 `.lmo`；没有 python3 时只跳过翻译，
其余照常构建。

## 功能

已实现：

- EAP-MD5 有线认证（Start / Identity / MD5 / Success-Failure 全流程）。
- 569 字节私有尾：CRC16/XMODEM、逐字节位反转+取反混淆、MD5 链式凭据加密、
  0x17–0x7E 属性集；`private_tail` 可关。
- 服务器私有属性：段 1 GBK 消息、段 2 0x6F 布局、段 3/4 版本门、段 5
  15 字节解混淆、属性 TLV 循环；`ENCRYPT_KEY`/`ENCRYPT_IV` 走 RC4
  (`com.ruijie.www`)；`SU_REAUTH_INTERV` 驱动重认证。
- PEAPv0 + MSCHAPv2（RFC 2759，含 MD4、DES 展开、奇偶校验），由
  `auth_mode` 选择；隧道分片、ACK、phase2 状态机与 C# 一致。EAP-TLS 协议栈
  已实现，但双向认证需要客户端证书，当前未开放证书配置入口。
- 账号池（最多 5 个）、服务器学习/锁定、探测缓存、自动守护、
  成功后不主动重认证、00:00-06:00 认证关闭时段、家庭网络暂停、
  Unix socket 控制、1MB 日志轮转。
- WAN 自动识别与多选；MAC 修改（写 `network.<section>.macaddr`）。
- 学校档案：读取 `/etc/ruijie-auth/profiles`、`/usr/share/ruijie-auth/profiles`
  或上传/粘贴 JSON；导入语义与 C# 一致（只覆盖档案声明字段、先备份）。
- LuCI 8 页：概览、账号、学校档案、认证服务器、设置、MAC、调试、日志；
  中文翻译随 ipk 安装。

暂未移植（桌面版有、路由器不需要或无证据）：

- Portal / eportal、自助服务、客户端中心消息、SAM 直通、升级下载。
- Windows 专用 DHCP 控制（`dhcp_mode` 目前只记录，不驱动 udhcpc）。
- 无线 WPA2-Enterprise 档案管理与诊断页（OUI/ARP/代理探测）。

## 整机固件附加能力

- **精简 kmod**：只保留 USB 全功能、R3G 板载、插件依赖和打印机，共
  237 个（依赖闭包）。清单见 `scripts/kmods-r3g.txt`；完整 feed 的
  1034 个列表保留在 `scripts/kmods-all.txt` 对照。
- **USB 存储**：`20-usb-mount` 热插拔自动挂载到 `/mnt/usb-<dev>`，支持
  vfat/exfat/NTFS3/ext4/f2fs/Btrfs/XFS；拔出时自动卸载。
- **USB 网络**：`90-ruijie-usbwan` 识别 RNDIS/NCM/ECM 和 iPhone ipheth，
  自动创建 `usbwan`；`91-usb-wifi` 让后插入的 USB 无线网卡自动注册为
  radio，实测 MT7921U 可开 5G AP。
- **USB/IP**：`usbipd` 监听 3240，`usbip-server/client` 与
  `/usr/sbin/usbip-share` 支持导出 ADB/串口/存储；绑定会解绑本地驱动，
  手机 USB 共享会暂时断开。
- **共享打印机**：`kmod-usb-printer` + `p910nd`，插打印机后自动监听
  9100，`20-p910nd` 处理热插拔。
- **上行故障切换**：`mwan3` 按 校园 WAN → USB 共享 → Wi-Fi STA 排序，
  httping 探测 204 端点，5 秒周期、连续 2 次失败切换；Wi-Fi STA 用
  LuCI 无线页连接上游 AP，接口名 `wwan`。认证器默认 `device=wan`，
  避免 USB 共享插上后把 `eth1` 当成校园 WAN。
- **QoS**：SQM + CAKE，默认 95/95 Mbit（100Mbps 校园线），
  `dual-srchost`/`dual-dsthost` 按主机隔离。
- **流量统计**：`nlbwmon` 按设备（1h 持久化）、`vnstat2` 按接口
  （wan/br-lan，5 分钟保存），DB 在 `/etc`。
- **自动重启**：`watchcat` 6 小时无 ping 才重启，避免网络抖动导致
  重启循环。
- **OpenClash**：0.48.002 + mihomo 1.19.34 softfloat 核心，默认
  `redir-host`、不劫持 DNS，和 dnsmasq + DoH 共存；无订阅时保持停止。
  核心/GeoSite 只在 rootfs，keep.d 只备份用户配置，升级不会复制
  72MB 到 overlay。
- **frpc**：0.51.3 + LuCI，预配置但停止，填写服务器后一键启用。
- **UPnP / WoL / 命令面板**：`miniupnpd-nftables`（LAN 侧、secure mode）、
  `luci-app-wol`、`luci-app-commands`。
- **防检测零配置**：UA3F（desync reorder 开、inject 关）、nftables
  TTL/MSS/IPID、DoH、DNS 查询 + TCP 53 + 百度/Bing/QQ 二次确认由
  首次启动脚本统一启用。

## 配置与运行数据

| 路径 | 用途 |
|---|---|
| `/etc/config/ruijie-auth` | 设置 + 账号池（明文，0600） |
| `/etc/ruijie-auth/profiles/*.json` | 学校档案 |
| `/etc/ruijie-auth/servers.json` | 已学习服务器 + locked |
| `/etc/ruijie-auth/discovery.json` | 探测缓存 |
| `/var/run/ruijie-auth/status.json` | 状态（含服务器私有属性） |
| `/var/run/ruijie-auth.sock` | 控制 socket（0600） |
| `/var/log/ruijie-auth.log` | 1MB 轮转日志 |

`config auth` 关键项：`device`（`auto` 或 `list device`）、`auth_mode`、
`service_name`、`static_id`、`private_tail`、`logoff_first`、
`response_delay`、`reauth_interval`、`autoguard`、`preempt`。

## CLI

```text
ruijie-auth version
ruijie-auth daemon
ruijie-auth status [--json]
ruijie-auth log [--lines N]
ruijie-auth probe [--device <iface>]
ruijie-auth auth|logoff|pause|resume|reload
ruijie-auth accounts [--json]
ruijie-auth servers list|lock|unlock|remove|rename
ruijie-auth discovery show|clear
ruijie-auth mac show|set|random|clear [--section wan]
ruijie-auth interfaces [--json] [--auto]
ruijie-auth profile list|show|import [--file ...] [--config ...]
ruijie-auth tail [--username ...] [--mac ...]
ruijie-auth selftest
```

`selftest` 在设备上跑一遍算法向量（RC4、MD4、MSCHAPv2、569 字节私有尾、
混淆往返、私有属性分段解析），不依赖服务器，可用来确认二进制正常。

## LuCI 与中文

- 菜单：服务 → 锐捷认证器；8 个页面标题与全部控件文本都是
  `_('English')` 源串。
- 翻译：`luci-app-ruijie-auth/po/zh_Hans/ruijie-auth.po`，构建时由
  `scripts/po2lmo.py` 编译为 `/usr/lib/lua/luci/i18n/ruijie-auth.zh-cn.lmo`。
  LuCI 会把该目录下当前语言的所有 `.lmo` 合并进 `window.TR`，所以不需要
  额外注册。
- `po2lmo.py` 的哈希、索引和字符串表与 LuCI 自带 `po2lmo` 逐字节一致
  （用 24.10 镜像里的 `https-dns-proxy.zh-cn.lmo` 反查校验：154/154 条
  键值和偏移完全相同）。

## 防检测与 DoH

认证器 ipk 本身不带任何 kmod；固件镜像默认包含以下整机功能：

- `ruijie-disguise`：nftables 改写 IPv4 TTL=128、TCP MSS=1460，并在内核
  直接 `ip id set 0`；`disguise=full` 时同时启动 UA3F。
- UA3F 3.7.0：NFQUEUE 只排队 TCP SYN，改写 TCP timestamp / initial window；
  IPID 改写关闭（nft 已接管，避免 MT7621 上全量 NFQUEUE 打满 CPU）。
  HTTP UA 改写默认关闭（`rewrite_mode DIRECT`），避免破坏微信/企业微信。
- `https-dns-proxy`：当前构建使用自定义 AdGuardHome DoH 端点，dnsmasq
  强制走 DoH；端点可在 `/etc/config/https-dns-proxy` 修改。
- 外网探测：4 个公共 DNS 并行真实 DNS 查询（任一成功即在线），失败后
  TCP 53 兜底，再用百度/Bing/QQ 二次确认；`ruijie-auth probe` 可自检。
- 日志：`ruijie-auth log` + LuCI logs2 页面（避开旧静态文件缓存）。

`disguise=off` 时删除 nft 表并停止 UA3F。认证器二进制与这些包解耦，
换机器时仍可只安装 ipk。

`files/etc/hotplug.d/net/90-ruijie-usbwan` 负责 USB 共享备用 WAN：识别
RNDIS/NCM/ECM/ipheth 后自动创建 `usbwan`，并交给 mwan3 管理。固件已预装
对应 usbnet 驱动，插上手机即可用；`91-usb-wifi` 负责后插入的 USB 无线
网卡注册。

## 升级与迁移

- `/etc/config/ruijie-auth` 是 conffile，`opkg install --force-reinstall`
  不会覆盖账号和档案。
- UA3F、Vantage、OpenClash 同样以 ipk 安装，可在不重刷固件的情况下
  `opkg install --force-reinstall <newer.ipk>` 升级；mihomo 核心直接替换
  `/etc/openclash/core/clash_meta`。
- 常驻 feed 包（mwan3/SQM/统计/frpc/UPnP）可直接 `opkg update && opkg
  upgrade`。内核模块必须与 24.10.0 的 `6.6.73~3abe85de...-r1` 完全匹配，
  不要跨版本升级内核。
- 从改名前的老包迁移：老包的配置文件名与包名不同，先停老服务，把老配置
  内容手工复制到 `/etc/config/ruijie-auth`，再安装新 ipk。
- 手动安装的 SakuraFrp/雷神不进固件，也不建议长期缓存在路由器。
  固件工具包 `MiR3G_V1-Openwrt/tools/manual-apps/` 提供
  `backup.sh`（升级前拉到 PC）和 `restore.sh`（sysupgrade 后推回、
  装依赖/恢复配置/开机自启）。备份含 natfrp 访问密钥，只留本机，
  不要提交或上传网盘。

## 验证状态（2026-11-20）

- `go test ./...`：protocol（含真实抓包的三份 569 字节私有尾逐字节向量、
  私有属性向量、RC4、MD4、MSCHAPv2 RFC 向量、PEAP 分片布局）、config、
  profile、supplicant、servers、network 全部通过。
- 私有尾三类语义与真实抓包逐字节一致：Start（会话 nonce + 全零指纹）、
  Identity（会话 nonce + 随机指纹）、MD5 Response（随机 nonce/指纹 +
  challenge 作 IV）。
- 常驻会话边界有回归测试：服务器应答后不再补发 EAPOL-Start；重认证时
  清空重复报文过滤表；服务器主动重认证有超时兜底；TLS 桥接关闭后并发
  读写不 panic（含 `-race`）。
- `go vet ./...` 干净；`gofmt` 无差异。
- amd64 原生与 mipsle（`qemu-mipsel`）`selftest` 均通过。
- ipk 构建通过，内容包含二进制、init、hotplug、UCI 默认值、8 个 LuCI
  页面、菜单/ACL 和 `ruijie-auth.zh-cn.lmo`。
- x86_64 OpenWrt QEMU 端到端冒烟（把文件直接放进 rootfs）：
  - `selftest` 通过；`interfaces` 把 `br-lan` 识别为校园网 WAN；
  - 守护进程在 `br-lan` 上真实打开 AF_PACKET，日志完整走完
    Logoff → 4 次 Start → 未找到服务器 → `paused`，状态文件含
    `auth_mode`、`devices`、`account`；
  - `profile import` 在设备上写入 8 个字段、生成 backup、触发 reload，
    状态随后显示 `profile=测试学校`；
  - LuCI 登录后 `data-page=admin-services-ruijie-auth-overview`、
    `body class=lang_zh-cn`，`/admin/translations/zh-cn` 返回 182 条运行时
    词条；按 hash 逐条校验 8 个菜单标题与 8 个页面全部命中，包括三条长
    说明文案（账号池、WAN 多选、协议自检）。
  - CLI 输出已全中文：`selftest` 显示“通过/自检通过”，`status` 显示
    “认证中/已认证”，`interfaces` 显示“已连接/无链路”，`accounts` 显示
    “已启用，当前”；日志级别标签为 `[信息]/[错误]/[调试]`。
- 未在真实校园网环境上做端到端认证（需要现场 WAN 与账号）。
