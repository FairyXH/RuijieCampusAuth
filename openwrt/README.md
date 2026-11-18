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
  `servers list/lock/unlock/remove/rename`、`discovery show/clear`。
- 常驻 daemon：Linux AF_PACKET raw socket、Start/Identity/MD5/Success、
  账号轮换（UCI `config account`，最多 5 个）、服务器学习、探测缓存、
  自动守护（掉线重认证）、持续抢占/定时重认证、家庭网络暂停、
  Unix socket 控制接口、1MB 日志轮转。
- LuCI 五页：概览（状态 + 控制按钮）、账号池、服务器/探测缓存、设置、日志。
- procd init、WAN hotplug、UCI defaults（0600、enable、start）。
- 测试：协议 golden vector、UCI/账号池上限、服务器/缓存、控制 socket、
  supplicant 假认证器全链路、网关解析、`go vet`。
- 2026-11-19 已验证：`go test ./...`、`go vet ./...`、mipsle 交叉编译、
  `qemu-mipsel selftest`；R3G 自定义镜像 8.1MB，
  SHA256 `08ddc0b19a41e4b582cfab5ef8cc37ffc2c8d619c1e6384a519ebcd8b4c3e39a`。
- x86_64 OpenWrt QEMU 实测：daemon 在 `br-lan` 上真实打开 AF_PACKET，
  无认证服务器时自动进入 `paused`（“未检测到认证服务器，当前网络在线，
  暂停认证”），概览页显示账号/接口；Resume 按钮能把 paused 拉回认证流程；
  账号池、服务器/缓存、设置页均正常渲染。

待验证：

- R3G 真机：`wan` AF_PACKET、DSA/MT7530、DHCP 时序、100Mbps `iperf3`。
- UA3F/UA2F 伪装脚本（`disguise=full` 目前只是配置项，脚本未实现）。
- 可选：malta/le MIPS QEMU、EAPOL tap/veth 真实传输模拟。

## 构建

```sh
# Ubuntu，仅编译 daemon
cd openwrt
sh scripts/build-go.sh

# 构建 R3G 镜像（默认 luci-ssl + files/ + LuCI 应用）
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
  sh scripts/build-image.sh
```

默认输出：

```text
/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64/
  bin/targets/ramips/mt7621/
  openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-sysupgrade.bin
```

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
