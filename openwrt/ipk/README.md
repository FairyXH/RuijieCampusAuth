# OpenWrt 独立插件 ipk 镜像

按 OpenWrt 包架构组织，目录规则固定为：

```text
ipk/<arch>/<package>/<version>/<file>.ipk
```

`<arch>` 使用 OpenWrt 的 `arch_packages` 原值，`<package>` 使用 opkg 包名，
`<version>` 保留完整包版本（含 `-rN`），文件名保持构建产物原名。
每个包目录下的 `latest.txt` 指向当前推荐版本。

## 目录

```text
ipk/
  all/                          # 架构无关包（LuCI 页面、翻译）
  mipsel_24kc/                  # ramips/mt7621、mt7620、mt76x8、rt305x
  mips_24kc/                    # ath79、lantiq
  aarch64_cortex-a53/           # mediatek/filogic、mt7622、qualcommax、mvebu/cortexa53、sunxi/cortexa53
  aarch64_generic/              # rockchip/armv8、armsr/armv8
  aarch64_cortex-a72/           # bcm27xx/bcm2711
  aarch64_cortex-a76/           # bcm27xx/bcm2712
  arm_cortex-a7_neon-vfpv4/     # ipq40xx、mediatek/mt7623、sunxi/cortexa7
  arm_cortex-a9_vfpv3-d16/      # mvebu/cortexa9
  arm_cortex-a15_neon-vfpv4/    # ipq806x
  x86_64/                       # x86/64
```

## 当前内容

| 架构 | 包 | 版本 | 大小 | 用途 |
|---|---|---|---:|---|
| `mipsel_24kc` | `ruijie-auth` | 0.10.0 | 2.85MB | 锐捷认证器（旧版） |
| `mipsel_24kc` | `ruijie-auth` | 0.11.4 | 2.87MB | 锐捷认证器（当前版） |
| `mipsel_24kc` | `ua3f` | 3.7.0-r1 | 5.5MB | UA3F DPI 伪装（32MB+ 机型） |
| `mipsel_24kc` | `ua2f` | 4.10.2-r1 | 261KB | UA2F 低 ROM 伪装（16MB 机型） |
| `mipsel_24kc` | `https-dns-proxy` | 2026.05.06-r1 | 33KB | DoH 代理 |
| `mipsel_24kc` | `kmod-cfg80211` | 6.6.73.6.12.6-r1 | 178KB | R3G 无线解锁（可选） |
| `all` | `luci-app-https-dns-proxy` | 2026.05.06-r1 | 12.6KB | DoH LuCI 页面 |
| `all` | `luci-i18n-https-dns-proxy-zh-cn` | 26.301.70438~aa4865e | 4.5KB | DoH 中文翻译 |

## 架构与 Go 目标

| OpenWrt arch | 常见 target | Go |
|---|---|---|
| `mipsel_24kc` | ramips/mt7621、mt7620、mt76x8、rt305x | `GOARCH=mipsle` |
| `mips_24kc` | ath79、lantiq | `GOARCH=mips` |
| `aarch64_cortex-a53` | mediatek/filogic、mt7622、qualcommax、mvebu/cortexa53、sunxi/cortexa53 | `GOARCH=arm64` |
| `aarch64_generic` | rockchip/armv8、armsr/armv8 | `GOARCH=arm64` |
| `aarch64_cortex-a72` | bcm27xx/bcm2711 | `GOARCH=arm64` |
| `aarch64_cortex-a76` | bcm27xx/bcm2712 | `GOARCH=arm64` |
| `arm_cortex-a7_neon-vfpv4` | ipq40xx、mediatek/mt7623、sunxi/cortexa7 | `GOARCH=arm GOARM=7` |
| `arm_cortex-a9_vfpv3-d16` | mvebu/cortexa9 | `GOARCH=arm GOARM=7` |
| `arm_cortex-a15_neon-vfpv4` | ipq806x | `GOARCH=arm GOARM=7` |
| `x86_64` | x86/64 | `GOARCH=amd64` |

## 新增一个 ipk

```sh
cd openwrt

# 例：mipsel
GOARCH=mipsle PKG_ARCH=mipsel_24kc sh scripts/build-ipk.sh

# 例：aarch64
GOARCH=arm64 PKG_ARCH=aarch64_cortex-a53 sh scripts/build-ipk.sh

# 例：armv7
GOARCH=arm GOARM=7 PKG_ARCH=arm_cortex-a7_neon-vfpv4 sh scripts/build-ipk.sh

# 例：x86_64
GOARCH=amd64 PKG_ARCH=x86_64 sh scripts/build-ipk.sh
```

把产物放到 `ipk/<arch>/<package>/<version>/`，然后生成校验：

```sh
cd "ipk/<arch>/<package>/<version>"
sha256sum *.ipk > SHA256SUMS
```

## 校验

```sh
cd "ipk/mipsel_24kc/ruijie-auth/0.11.4"
sha256sum -c SHA256SUMS
```

## 安装

```sh
opkg install --force-reinstall /tmp/ruijie-auth_0.11.4_mipsel_24kc.ipk
```

`/etc/config/ruijie-auth` 是 conffile，升级不会丢账号、设置和已导入档案。
UA3F/UA2F 的依赖（iptables/ipset/kmod 或 libnetfilter/ip-full/kmod-nft）
仍从目标系统的 OpenWrt feed 安装；本目录只保存插件本体，不是完整 feed。
