# 小米路由器 3G 原版（R3G）

- SoC：MT7621A（MIPS 24KEc，双核四线程）
- RAM：256MB
- Flash：128MB NAND
- 网络：1 WAN + 2 LAN，千兆
- USB：1 × USB 3.0
- OpenWrt target：`ramips/mt7621`
- OpenWrt profile：`xiaomi_mi-router-3g`

只适用于 R3G 原版。R3Gv2、4A 千兆版、4A 百兆版的分区、Breed 布局和
内核 ABI 不同，不能刷本目录固件。

## 版本

| 版本 | 说明 |
|---|---|
| `0.10.0` | 精简全功能固件；237 个 kmod；完整 R3G 包集 |
| `0.11.4` | 当前版；SAM 心跳默认关闭；ZeroTier 内置；无线解锁；511 包 |

## v0.11.4 文件

| 文件 | 用途 |
|---|---|
| `...-squashfs-sysupgrade.bin` | OpenWrt 内 sysupgrade |
| `...-squashfs-kernel1.bin` | Breed OpenWrt 布局内核，写 `0x600000` |
| `...-squashfs-rootfs0.bin` | Breed OpenWrt 布局 rootfs，写 `0xA00000` |
| `...-breed.bin` | 合并镜像（8MB 头 + kernel1 双写 + rootfs），写 Breed 第二分区 `0x200000` |
| `...manifest` | 511 包清单 |

Breed 1.2 的“小米 R3G 原厂 → 固件”不接受合并的 `-breed.bin`；实测有效
路径是“常规固件 → 闪存布局 小米 R3G OpenWrt”，分别上传 kernel1 与
rootfs0。详细步骤见
`D:\Files\Develop\OpenWrt\MiR3G_V1-Openwrt\固件刷写教程.txt`。

## 校验

```sh
cd 0.11.4
sha256sum -c SHA256SUMS
```

## 说明

v0.11.4 默认启用 UA3F + `https-dns-proxy` + `ruijie-disguise full`，
内置 ZeroTier 1.14.1 + LuCI、Vantage 主题和无线解锁 cfg80211。认证器
`ruijie-auth 0.11.4` 的 SAM 直通心跳默认关闭；只有 SAM 目标可达时才在
LuCI「设置 → SAM 心跳」手动开启。
