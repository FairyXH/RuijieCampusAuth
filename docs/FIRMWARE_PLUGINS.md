# R3G 固件预装插件清单

固件基线：OpenWrt 24.10.0 / Linux 6.6.73 / ramips mt7621，目标
`xiaomi_mi-router-3g`。本文对应 `ruijie-auth 0.10.0` 固件构建，后续版本
以实际镜像的 `*.manifest` 为准。

## 用户功能插件

| 插件 | 版本 | 作用 | LuCI | 中文 | 默认状态 |
|---|---|---|---|---|---|
| `ruijie-auth` | 0.10.0 | 锐捷 802.1X 有线认证 | 8 页 | 是 | 启用 |
| `ua3f` | 3.7.0 | TCP timestamp/window 伪装 | 上游配置页 | 是 | 启用 |
| `luci-theme-vantage` + `luci-app-vantage` | 1.0.21 | 默认主题与设置 | 是 | 是 | 默认主题 |
| `https-dns-proxy` | 2026.05.06 | 自定义 DoH 上游 | 是 | 是 | 启用 |
| `mwan3` | 2.11.16 | 校园 WAN / USB / Wi-Fi STA 故障切换 | 是 | 是 | 启用 |
| `sqm-scripts` | 1.6.0 | CAKE 整形与按主机公平分配 | 是 | 是 | 启用，200/200 Mbit |
| `nlbwmon` | 2024.02.21 | 按设备流量统计 | 是 | 是 | 启用 |
| `vnstat2` | 2.12 | 按接口流量统计 | 是 | 是 | 启用 |
| `frpc` | 0.51.3 | FRP 内网穿透客户端 | 是 | 是 | 预配置，停止 |
| `luci-app-openclash` | 0.48.002 | Clash 代理面板 | 是 | 是 | 预配置，停止 |
| mihomo 核心 | 1.19.34 | OpenClash 的 mipsle softfloat 核心 | — | — | 已内置 |
| `miniupnpd-nftables` | 2.3.9 | UPnP / NAT-PMP | 是 | 是 | 启用，LAN 侧 |
| `luci-app-wol` + `etherwake` | 26.301 | 网络唤醒 | 是 | 是 | 可用 |
| `luci-app-commands` | 26.301 | 自定义命令面板 | 是 | 是 | 可用 |
| `usbutils` | 上游 | `lsusb` | — | — | 可用 |
| `usb-modeswitch` | 2022.02.24 | 4G/5G 上网卡模式切换 | — | — | 可用 |

`dnsmasq-full` 2.93 与 `wpad-mbedtls` 2024.09 分别替代了基础镜像里的
`dnsmasq` 和 `wpad-basic-mbedtls`，用于 OpenClash 的 nftset 支持和完整
STA / WPA3 / 企业级无线。两个替换在构建时用 `-dnsmasq`、
`-wpad-basic-mbedtls` 显式移除旧包。

## 内核模块

镜像预装 24.10.0/ramips/mt7621 feed 中全部 1034 个 kmod（完整列表见
`openwrt/scripts/kmods-all.txt`），覆盖：

- USB 主机、存储（UAS）、串口、网卡、RNDIS/NCM/ECM/MBIM/QMI、
  USB Wi-Fi、USB 音频/视频/HID/打印机；
- ext4/f2fs/exfat/NTFS3/Btrfs/XFS 等文件系统，`kmod-fuse`；
- 板载 GPIO/LED/按键/I2C/SPI/MMC/HWMON/RTC/看门狗；
- 完整 netfilter/nftables/ipset、`kmod-tun`、WireGuard、`kmod-inet-diag`、
  `kmod-netlink-diag`；
- 其余目标 feed 可编译的驱动。

排除的 8 个包都是互斥替代或不属于 R3G 的硬件变体，否则 opkg 会因同名
模块文件互相覆盖而失败：

```text
kmod-ath10k-ct  kmod-ath10k-ct-smallbuffers  kmod-ath10k-smallbuffers
kmod-hsdma-mtk  kmod-mwifiex-sdio
kmod-r8125-rss  kmod-r8126-rss  kmod-sdhci-mt7620
```

kmod 与内核 ABI 绑定，只能安装与 `6.6.73~3abe85de...-r1` 完全匹配的
官方 24.10.0 feed 包；不要跨版本升级内核模块。

## 外部插件来源

外部包已固定版本和 SHA256，存放在 `openwrt/vendor/`，构建不依赖网络：

```text
ua3f_3.7.0-r1_mipsel_24kc.ipk
luci-app-openclash_0.48.002_all.ipk
mihomo-linux-mipsle-softfloat-v1.19.34.gz
luci-theme-vantage_1.0.21-r1_all.ipk
luci-i18n-vantage-theme-zh-cn_1.0.21-r1_all.ipk
luci-app-vantage_1.0.21-r1_all.ipk
luci-i18n-vantage-zh-cn_1.0.21-r1_all.ipk
```

哈希见 `openwrt/vendor/SHA256SUMS`，来源与许可证见
`openwrt/vendor/README.md`。

## 免刷更新

常规 feed 包：

```sh
opkg update
opkg install --force-reinstall <package>
```

本地/第三方 ipk（ruijie-auth、UA3F、Vantage、OpenClash）：

```sh
opkg install --force-reinstall /tmp/ruijie-auth_<new>_mipsel_24kc.ipk
opkg install --force-reinstall /tmp/ua3f_<new>_mipsel_24kc.ipk
opkg install --force-reinstall /tmp/luci-app-openclash_<new>_all.ipk
```

`/etc/config/ruijie-auth`、`/etc/config/ua3f`、`/etc/config/openclash` 都是
conffile，账号、DoH、伪装和 OpenClash 设置不会被覆盖。

mihomo 核心不是 opkg 包，直接替换：

```sh
/etc/init.d/openclash stop
# 上传对应 mipsle softfloat 版本
cp clash_meta /etc/openclash/core/clash_meta
chmod 0755 /etc/openclash/core/clash_meta
/etc/init.d/openclash start
```

## 从零构建

```sh
git clone <repo> RuijieCampusAuth
cd RuijieCampusAuth/openwrt
sh scripts/fetch-imagebuilder.sh   # 固定 24.10.0 IB，校验 SHA256
sh scripts/build-image.sh          # 优先使用 vendor/，产物在 build/
```

`build-image.sh` 会生成 `ruijie-auth` ipk、安装全部 kmod/插件、写入默认
配置，并输出 kernel1/rootfs0/sysupgrade/manifest。完整刷机步骤见
`docs/MANUAL_OPENWRT.md`。
