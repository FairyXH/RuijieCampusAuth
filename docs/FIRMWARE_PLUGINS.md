# R3G 固件预装插件清单

固件基线：OpenWrt 24.10.0 / Linux 6.6.73 / ramips mt7621，目标
`xiaomi_mi-router-3g`。本文对应 `ruijie-auth 0.10.0` 精简全功能固件
（提交 `7f0043c`），后续版本以实际镜像的 `*.manifest` 为准。

体积：rootfs squashfs 53,279,744；sysupgrade 56,474,746；Breed 合并
64,356,352；首次启动 overlay 约 49.5 MiB。

## 用户功能插件

| 插件 | 版本 | 作用 | LuCI | 中文 | 默认状态 |
|---|---|---|---|---|---|
| `ruijie-auth` | 0.10.0 | 锐捷 802.1X 有线认证 | 8 页 | 是 | 启用，device=wan |
| `ua3f` | 3.7.0 | TCP timestamp/window、desync reorder | 无独立页 | 是 | 启用，inject 关 |
| `luci-theme-vantage` + `luci-app-vantage` | 1.0.21 | 默认主题与设置 | 是 | 是 | 默认主题 |
| `https-dns-proxy` | 2026.05.06 | 自定义 DoH 上游 | 是 | 是 | 启用 |
| `mwan3` | 2.11.16 | 校园 WAN / USB / Wi-Fi STA 切换 | 是 | 是 | 启用 |
| `sqm-scripts` | 1.6.0 | CAKE 95/95 Mbit、按主机隔离 | 是 | 是 | 启用 |
| `nlbwmon` | 2024.02.21 | 按设备流量统计 | 是 | 是 | 启用，1h 持久化 |
| `vnstat2` | 2.12 | 按接口流量统计 | 是 | 是 | 启用，5 分钟保存 |
| `p910nd` | 0.97 | USB 打印机共享，TCP 9100 | 是 | 是 | 启用，热插拔重启 |
| `watchcat` | 1-r17 | 6 小时无 ping 自动重启 | 是 | 是 | 启用 |
| `frpc` | 0.51.3 | FRP 内网穿透 | 是 | 是 | 预配置，停止 |
| `luci-app-openclash` | 0.48.002 | Clash 代理面板 | 是 | 是 | 预配置，停止 |
| mihomo 核心 | 1.19.34 | OpenClash mipsle softfloat | — | — | 内置 /etc |
| `usbip-server` / `usbip-client` | 2.0 | USB/IP 导出与 CLI | CLI | — | usbipd 3240 |
| `miniupnpd-nftables` | 2.3.9 | UPnP / NAT-PMP | 是 | 是 | 启用，LAN 侧 |
| `luci-app-wol` + `etherwake` | 26.301 | 网络唤醒 | 是 | 是 | 可用 |
| `luci-app-commands` | 26.301 | 自定义命令面板 | 是 | 是 | 可用 |
| `usbutils` / `usb-modeswitch` | 上游 | lsusb、上网卡模式切换 | — | — | 可用 |

LuCI 中文补齐：`luci-i18n-base/firewall/package-manager`，以及
sqm/mwan3/nlbwmon/vnstat2/frpc/upnp/p910nd/watchcat/wol/commands/
https-dns-proxy/vantage 的 zh-cn 包；OpenClash 与 UA3F 的中文 `.lmo`
随各自插件安装。UA3F 上游没有独立 LuCI 页面，配置在
`/etc/config/ua3f`。

## 防检测

- `ruijie-disguise full`：nftables `ttl 128`、`mss 1460`、`ip id 0`；
  UA3F NFQUEUE 只处理 TCP SYN 的 timestamp/window。
- UA3F `desync_reorder=1`（1500 字节 / 8 包，端口 80/443）。实测对
  HTTPS 无影响，UA3F CPU 0%，系统空闲 93%。
- UA3F `desync_inject=0`。本校园网实测 TTL 1/2/3、80/443 都会导致
  TLS 握手 EOF，因此默认关闭，避免刷入即断网。
- 外网探测：4 个公共 DNS 的 TCP 53 并行 + HTTP 204 回退，探针可绑定
  `wan` 出口。

## 内核模块

预装 237 个 kmod（依赖闭包），清单见 `openwrt/scripts/kmods-r3g.txt`：

- USB 主机 xHCI、存储/UAS、串口全系、网卡 RNDIS/NCM/ECM/MBIM/QMI、
  USB Wi-Fi（MT7921U、MT76x0u/x2u、RTL8xxxU、RTW88、RT2800 等）、
  USB 音频/视频/HID/打印机；
- ext4/f2fs/exfat/NTFS3/vfat/Btrfs/XFS/UDF/ISO9660/FUSE；
- R3G 板载 mt76、GPIO、LED、按键、thermal；
- 插件依赖：tun、inet-diag、netlink-diag、sched-cake、ifb、
  nft-queue/tproxy、conntrack-netlink、usbip；
- 共享打印机：`kmod-usb-printer` + `p910nd`。

完整 feed 的 1034 个列表保留在 `openwrt/scripts/kmods-all.txt` 供对照；
互斥/异构变体未安装：`kmod-ath10k-ct`、`kmod-ath10k-ct-smallbuffers`、
`kmod-ath10k-smallbuffers`、`kmod-hsdma-mtk`、`kmod-mwifiex-sdio`、
`kmod-r8125-rss`、`kmod-r8126-rss`、`kmod-sdhci-mt7620`。

kmod 与内核 ABI 绑定，只能安装与 `6.6.73~3abe85de...-r1` 匹配的官方
24.10.0 feed 包。

## 外部插件来源

固定版本与 SHA256 存放于 `openwrt/vendor/`，构建不依赖网络：

```text
ua3f_3.7.0-r1_mipsel_24kc.ipk
luci-app-openclash_0.48.002_all.ipk
mihomo-linux-mipsle-softfloat-v1.19.34.gz
luci-theme-vantage_1.0.21-r1_all.ipk
luci-i18n-vantage-theme-zh-cn_1.0.21-r1_all.ipk
luci-app-vantage_1.0.21-r1_all.ipk
luci-i18n-vantage-zh-cn_1.0.21-r1_all.ipk
```

哈希见 `openwrt/vendor/SHA256SUMS`。OpenClash 的 keep.d 只保留
`config/custom/overwrite/rule_provider/proxy_provider/history`，不把
61MB 核心和 11MB GeoSite 复制进 overlay。

## 免刷更新

```sh
opkg update
opkg install --force-reinstall /tmp/ruijie-auth_<new>_mipsel_24kc.ipk
opkg install --force-reinstall /tmp/ua3f_<new>_mipsel_24kc.ipk
opkg install --force-reinstall /tmp/luci-app-openclash_<new>_all.ipk
```

mihomo 核心直接替换 `/etc/openclash/core/clash_meta`（mipsle
softfloat），或从 OpenClash 界面更新。

## 从零构建

```sh
git clone <repo> RuijieCampusAuth
cd RuijieCampusAuth/openwrt
sh scripts/fetch-imagebuilder.sh   # 固定 24.10.0 IB
sh scripts/build-image.sh          # 优先 vendor/，产物在 build/
```

刷机与回归步骤见 `docs/MANUAL_OPENWRT.md`。
