# OpenWrt / Xiaomi R3G 组件

本目录是 R3G 软路由端的源码，和仓库根目录的 C#/Python 实现并列维护。

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

## 当前状态（2026-11-18）

已完成并可编译：

- `internal/protocol`：私有尾、CRC16/XMODEM、位反转、MD5 链式凭据、
  EAPOL 组帧、EAP-MD5、服务器 GB18030 消息解析。
- 569 字节 golden test 与 C#/Python 参考实现一致：
  `779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5`。
- `hxxy-auth` CLI：`version`、`status [--json]`、`tail`、`selftest`。
- LuCI 应用骨架：菜单、rpcd ACL、概览（状态轮询）、设置（UCI）、日志。
- UCI 默认配置：`files/etc/config/hxxy-auth`。
- 构建脚本：Go 交叉编译 + Image Builder 组镜像。
- 2026-11-18 已验证：`go test ./...`、`go vet ./...`、mipsle 交叉编译、
  `qemu-mipsel selftest`、R3G 自定义镜像（7.9MB，
  SHA256 `a7af74eae303ef8234ab3eb14bd97a49abf60fcadc69ac4669f7966021a66bdd`）。

待下一提交：

- 常驻 supplicant/daemon、raw socket、账号池、服务器学习、重认证。
- procd init、hotplug。
- UA3F/UA2F 伪装脚本。

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

LuCI 只依赖 CLI 的 JSON 输出和 UCI 配置，不要求 daemon 监听端口：

```text
hxxy-auth status --json
hxxy-auth version
```

UCI 配置在 `/etc/config/hxxy-auth`，由 LuCI 设置页读写；daemon 后续通过
`uci -q show hxxy-auth` 或等价解析读取。

状态文件：`/var/run/hxxy-auth/status.json`

```json
{
  "time": "2026-11-18T21:00:00+08:00",
  "state": "authenticated",
  "message": "认证成功",
  "interface": "wan",
  "server": "00:1a:a9:17:ff:ff"
}
```

## 安全与兼容说明

- 协议核心必须使用 MD5：EAP-MD5 challenge 和锐捷私有尾 0x2F 凭据都是
  MD5 链式运算。这不是密码存储，也不能换成 AES/SHA；换掉会直接认证失败。
- 账号密码不写入仓库；运行时配置在设备 `/etc/hxxy-auth/accounts.json`
  （0600，后续提交加入）。
- 本目录不包含任何真实账号、密码或抓包。
