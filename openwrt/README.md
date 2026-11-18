# OpenWrt 组件（当前验证目标：Xiaomi R3G）

本目录是 OpenWrt 软路由端的源码，和仓库根目录的 C#/Python 实现并列维护。

## 目标可移植性

源码不绑定 R3G。Go daemon、LuCI、UCI 都只依赖 OpenWrt 用户态和标准
AF_PACKET/ubus/UCI 能力，没有 MT7621 专属代码。目标相关只有：

- Image Builder 的 `PROFILE` 和 device packages（默认
  `xiaomi_mi-router-3g`）；
- `GOARCH`（默认 `mipsle`，换 ARM/ARM64/x86 时改这里）；
- UCI 的 `device`（默认 `wan`，不同设备按实际 netdev 调整）；
- `disguise=full` 时 UA3F 的 kmod 依赖（`kmod-nft-queue`、
  `kmod-nft-tproxy`），这是 OpenWrt 目标相关，不是 R3G 专属。

所以同 target（`ramips/mt7621`）的其他 OpenWrt 设备只改 `PROFILE` 就能构建；
换架构只改 `GOARCH` 重新编译，LuCI/UCI 文件不用动。

## 目录

```text
openwrt/
  hxxy-auth/                 Go 守护进程 + CLI（目标 mipsle）
  luci-app-hxxy-auth/        LuCI 应用源码（菜单/ACL/视图）
  files/                     Image Builder 覆盖文件（UCI 配置等）
  scripts/build-go.sh        交叉编译 Go 二进制
  scripts/build-image.sh     组装 files/ 并调用 Image Builder
  README.md
```

## 源码与编译分工

- **源码统一放在实体机仓库的 `openwrt/`**。在 Windows 上编辑、提交、推送 GitHub。
- **编译和测试只在 Ubuntu VM 进行**。Windows 不安装 Go、不跑 OpenWrt Image Builder。
- Ubuntu 侧从 GitHub 拉代码后执行：

```sh
cd openwrt
sh scripts/build-go.sh
sh scripts/build-image.sh
```

- 构建产物在 `openwrt/build/`，已被 `.gitignore` 排除，不提交。
- 私有仓库在 Ubuntu 上需要只读 deploy key 或 `gh auth login`；临时/离线可用
  `git bundle` 传输，命令见 `docs/OPENWRT_BUILD_ENV.md`。

## 当前状态（2026-11-19）

已实现：

- `internal/protocol`：私有尾、CRC16/XMODEM、位反转、MD5 链式凭据、
  EAPOL 组帧、EAP-MD5、服务器 GB18030 消息解析。
- 569 字节 golden test 与 C#/Python 参考实现一致：
  `779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5`。
- `hxxy-auth` CLI：`version`、`status [--json]`、`tail`、`selftest`、
  `daemon`、`auth/logoff/pause/resume/reload`、`accounts`、
  `servers list/lock/unlock/remove/rename`、`discovery show/clear`、
  `mac show/set/random/clear`。
- 常驻 daemon：Linux AF_PACKET raw socket、Start/Identity/MD5/Success、
  账号轮换（UCI `config account`，最多 5 个）、服务器学习、探测缓存、
  自动守护（掉线重认证）、持续抢占/定时重认证、家庭网络暂停、
  Unix socket 控制接口、1MB 日志轮转。
- LuCI 六页：概览（状态 + 控制按钮）、账号池、服务器/探测缓存、MAC、
  设置、日志。
- 默认校园网防检测（`disguise=full`）：
  - `hxxy-disguise` 设置 nft TTL 128 + MSS 1460；
  - UA3F 3.7.0 mipsel 以 NFQUEUE 运行，L3 改写 TTL/IPID/TCP timestamp/
    TCP initial window；
  - UA 改写**默认关闭**（`rewrite_mode DIRECT`），不触碰微信/企业微信 UA；
  - sysctl `ip_default_ttl=128`、`tcp_timestamps=0`、`tcp_ecn=0`、
    IPv6 关闭；flow offload 关闭。
- 内置 `https-dns-proxy` + `luci-app-https-dns-proxy` +
  `luci-i18n-https-dns-proxy-zh-cn` + `luci-i18n-base-zh-cn`；
  默认 AliDNS + Tencent DoH，IPv4 bootstrap，`force_dns` 开启。
- MAC 快捷修改：CLI `hxxy-auth mac ...` + LuCI MAC 页；写
  `network.wan.macaddr`、`ifup wan`、reload daemon。
- USB 网络共享：内置 `kmod-usb-net`、RNDIS、CDC-ECM/NCM/subset、ipheth；
  `/etc/hotplug.d/net/90-hxxy-usbwan` 自动接管 `usb*`/`wwan*`，生成
  `usbwan` DHCP 接口（metric 100）并加入 WAN 防火墙 zone。
- 应用层打成 `hxxy-auth` ipk，opkg 拥有二进制/脚本/LuCI 文件；后续可
  `opkg install` 升级，不必重刷固件。
- procd init、WAN hotplug、UCI defaults（0600、enable、flow offload 关闭）。
- 测试：协议 golden vector、UCI/账号池上限、服务器/缓存、控制 socket、
  supplicant 假认证器全链路、网关解析、`go vet`。
- 2026-11-19 已验证：`go test ./...`、`go vet ./...`、mipsle 交叉编译、
  `qemu-mipsel selftest`；完整 R3G 固件 14MB，
  SHA256 `001fb2df06f47760fe6e90a32f3e343fbc10ae0432e7e81ffb029ea6dfc1373a`，
  rootfs 约 10MB，含 UA3F 17MB、https-dns-proxy、LuCI 中文包、
  nft/iptables/ipset 依赖、USB tethering kmods。
- x86_64 OpenWrt QEMU 实测：daemon 在 `br-lan` 上真实打开 AF_PACKET，
  无认证服务器时自动进入 `paused`（“未检测到认证服务器，当前网络在线，
  暂停认证”），概览页显示账号/接口；Resume 按钮能把 paused 拉回认证流程；
  账号池、服务器/缓存、设置页均正常渲染。

待验证：

- R3G 真机：`wan` AF_PACKET、DSA/MT7530、DHCP 时序、100Mbps `iperf3`。
- UA3F NFQUEUE 在 MT7621 100Mbps 下的实际吞吐。
- MAC 页在真机 `wan` 上的 ifup/重认证时序。
- USB 共享在 R3G 上的实际枚举（`usb0`/`wwan0`）、DHCP 与路由切换。
- Breed 重打包镜像在真机 Breed 上的刷写与启动。
- 可选：malta/le MIPS QEMU、EAPOL tap/veth 真实传输模拟。

## 构建

```sh
# Ubuntu，仅编译 daemon
cd openwrt
sh scripts/build-go.sh

# 构建完整 R3G 固件（UA3F + https-dns-proxy + LuCI 中文 + kmods）
# 脚本会自动下载/校验 UA3F 3.7.0 mipsel 包并抽取二进制
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
  sh scripts/build-image.sh
```

默认输出：

```text
/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64/
  bin/targets/ramips/mt7621/
  openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-sysupgrade.bin
  openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-kernel1.bin
  openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-rootfs0.bin
```

同时生成可单独升级的 ipk：

```text
openwrt/build/ipk/hxxy-auth_0.1.0_mipsel_24kc.ipk
```

## USB 网络共享（Android / iPhone）

固件已内置以下内核模块，插手机开“USB 网络共享”即可用，不需要重编：

```text
kmod-usb-net kmod-usb-net-rndis kmod-usb-net-cdc-ether
kmod-usb-net-cdc-ncm kmod-usb-net-cdc-subset kmod-usb-net-ipheth
```

行为：

- 手机枚举出的 `usb0`（RNDIS/CDC-ECM）或 `wwan0`（CDC-NCM）由
  `/etc/hotplug.d/net/90-hxxy-usbwan` 自动识别，只接受 USB 驱动挂载的
  netdev，不会误抓同名的非 USB 接口。
- 首次启动由 `uci-defaults` 创建 `network.usbwan`：DHCP、metric 100，
  并加入 WAN 防火墙 zone（NAT/转发）。
- metric 100 表示**备用 WAN**：校园网 WAN 在线时仍走 WAN；拔掉 WAN 网线
  或 `ifdown wan` 后自动走手机流量。如果校园网口有链路但未认证，默认路由
  仍会压在 WAN 上，需要拔线或 `ifdown wan` 才会切到 USB。
- 查看状态：`ifstatus usbwan`、`logread -e hxxy-usbwan`；
  LuCI 的“网络 → 接口”里会出现 `usbwan`。
- 固件的防检测 sysctl 关闭了 IPv6，因此 USB 共享走 IPv4；手机侧不需要
  特别设置。

## 免刷固件更新插件（ipk）

固件里的 `hxxy-auth` 二进制、init 脚本、热插拔脚本和 LuCI 页面都由 opkg
包 `hxxy-auth` 拥有，不再散落在只读 `FILES` 覆盖层里。后续更新流程：

1. 在 Ubuntu 构建新包：`sh scripts/build-image.sh` 会顺带生成
   `build/ipk/hxxy-auth_<version>_mipsel_24kc.ipk`；只更新插件也可以单独跑
   `sh scripts/build-ipk.sh`。
2. 把 ipk 传到路由器（LuCI“系统 → 软件包”上传，或 `scp` 到 `/tmp`）。
3. 安装：

```sh
opkg install --force-reinstall /tmp/hxxy-auth_0.1.0_mipsel_24kc.ipk
```

`/etc/config/hxxy-auth` 是 conffile，账号池和设置不会被覆盖；安装后
`postinst` 会自动重启 `hxxy-auth` 和 `hxxy-disguise`。LuCI 页面更新后
浏览器强制刷新一次（Ctrl+F5）即可。

需要重刷固件的只有：内核、kmod、UA3F 本体、https-dns-proxy 等系统级组件。

## Breed 刷写

标准 `...-squashfs-sysupgrade.bin` 是 OpenWrt 的 sysupgrade tar，Breed
的升级页不接受它，**不要直接选这个文件**。Breed 需要专用的重打包镜像：

```text
openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-breed.bin
```

生成方式（Ubuntu）：

```sh
git clone https://github.com/eg15/mir3g-breed-image-maker /opt/openwrt/breed
cd /opt/openwrt/breed
./make_mir3g_breed_image.sh \
  .../openwrt-...-squashfs-kernel1.bin \
  .../openwrt-...-squashfs-rootfs0.bin
```

脚本把 `kernel1.bin` 写到偏移 0 和 4M，再从 8M 起追加 `rootfs0.bin`。
这对应 R3G 的 `kernel1`（8MB）+ `rootfs0`（0xE00000）布局。

刷写步骤：

1. 断电，按住复位孔上电，橙灯闪烁后松开；电脑接 LAN 口。
2. 打开 `http://192.168.1.1`，进 Breed 的固件更新页。
3. 选择 `...-squashfs-breed.bin`，目标选**第二个分区**（0x600000）。
4. 在环境变量页确认/设置 `autoboot.command = boot flash 0x600000`。
5. 刷写完成后重启。

注意：

- 该重打包工具是社区方案，当前镜像已按相同布局生成，但尚未在 R3G 真机
  上验证。
- 已经在跑标准 OpenWrt 布局的设备，继续用 LuCI/`sysupgrade` 升级即可，
  不需要每次重打包 Breed 镜像。
- 如果之前刷的是小米原厂固件且没有 Breed，需要先自行安装 Breed；本仓库
  不提供 bootloader。

## API/配置契约

LuCI 只依赖 CLI 的 JSON 输出、UCI 配置和控制 socket，不要求 daemon 监听
HTTP 端口：

```text
hxxy-auth status --json
hxxy-auth version
hxxy-auth auth|logoff|pause|resume|reload
hxxy-auth accounts --json
hxxy-auth servers list --json
hxxy-auth discovery show --json
```

UCI 配置在 `/etc/config/hxxy-auth`，由 LuCI 设置页/账号页读写；daemon 直接
解析 `config auth` 和 `config account`。账号池最多 5 个，`Current` 优先，
失败后按顺序尝试其他启用账号。

运行数据：

- `/etc/config/hxxy-auth` — 设置 + 账号（明文，0600）
- `/etc/hxxy-auth/servers.json` — 学习到的认证服务器 + locked 标记
- `/etc/hxxy-auth/discovery.json` — 探测缓存
- `/var/run/hxxy-auth/status.json` — 状态
- `/var/run/hxxy-auth.sock` — 控制 socket（0600）
- `/var/log/hxxy-auth.log` — 1MB 轮转日志

状态文件：

```json
{
  "time": "2026-11-19T00:00:00+08:00",
  "state": "authenticated",
  "message": "认证成功",
  "account": "S12016004",
  "interface": "wan",
  "server": "00:1a:a9:17:ff:ff"
}
```

## 安全与兼容说明

- 协议核心必须使用 MD5：EAP-MD5 challenge 和锐捷私有尾 0x2F 凭据都是
  MD5 链式运算。这不是密码存储，也不能换成 AES/SHA；换掉会直接认证失败。
- 账号密码明文保存在设备 `/etc/config/hxxy-auth` 的 `config account` 段，
  文件权限 0600；按当前部署要求不使用 DPAPI/加密。仓库里不包含真实账号。
- 本目录不包含任何真实账号、密码或抓包。
