# OpenWrt 独立插件 ipk 镜像

按 OpenWrt 包架构组织，目录规则固定为：

```text
ipk/<arch>/<package>/<version>/<file>.ipk
```

`<arch>` 使用 OpenWrt 的 `arch_packages` 原值，`<package>` 使用 opkg 包名，
`<version>` 保留完整包版本（含 `-rN`），文件名保持构建产物原名。
每个包目录下的 `latest.txt` 指向当前推荐版本。

## 当前内容

- `ruijie-auth` 0.11.4：**33 个架构**全部预编译。
- `ruijie-auth` 0.10.0：仅 `mipsel_24kc`（历史版本）。
- `ua3f` 3.7.0-r1、`ua2f` 4.10.2-r1、`https-dns-proxy` 2026.05.06-r1、
  `kmod-cfg80211`：当前为 `mipsel_24kc`。
- `all/`：`luci-app-https-dns-proxy`、`luci-i18n-https-dns-proxy-zh-cn`。

`ruijie-auth` 是纯 Go 静态二进制 + LuCI 页面，不依赖专属 kmod；33 个
架构的 ipk 只差 `Architecture` 和二进制目标。

## 认证器架构矩阵

| OpenWrt arch | Go | 适用 target / 备注 |
|---|---|---|
| `aarch64_cortex-a53` | `arm64` | mediatek/filogic、mt7622、qualcommax、mvebu/cortexa53、sunxi/cortexa53 |
| `aarch64_cortex-a72` | `arm64` | bcm27xx/bcm2711 |
| `aarch64_cortex-a76` | `arm64` | bcm27xx/bcm2712 |
| `aarch64_generic` | `arm64` | rockchip/armv8、armsr/armv8 |
| `arm_cortex-a15_neon-vfpv4` | `arm GOARM=7` | ipq806x |
| `arm_cortex-a5_vfpv4` | `arm GOARM=7` | ARMv7-A |
| `arm_cortex-a7` | `arm GOARM=7` | ARMv7-A |
| `arm_cortex-a7_neon-vfpv4` | `arm GOARM=7` | ipq40xx、sunxi/cortexa7 |
| `arm_cortex-a7_vfpv4` | `arm GOARM=7` | ARMv7-A |
| `arm_cortex-a8_vfpv3` | `arm GOARM=7` | ARMv7-A |
| `arm_cortex-a9` | `arm GOARM=7` | bcm53xx |
| `arm_cortex-a9_neon` | `arm GOARM=7` | ARMv7-A |
| `arm_cortex-a9_vfpv3-d16` | `arm GOARM=7` | mvebu/cortexa9 |
| `arm_arm1176jzf-s_vfp` | `arm GOARM=6` | ARMv6 |
| `arm_arm926ej-s` | `arm GOARM=5` | ARMv5TE |
| `arm_xscale` | `arm GOARM=5` | ARMv5TE |
| `arm_fa526` | `arm GOARM=5` | 尽力而为：FA526 是 ARMv4，Go 最低 ARMv5 |
| `mipsel_24kc` | `mipsle softfloat` | ramips/mt7621、mt7620、mt76x8、rt305x |
| `mipsel_24kc_24kf` | `mipsle softfloat` | ramips hardfloat userland |
| `mipsel_74kc` | `mipsle softfloat` | ramips/rt3883 |
| `mipsel_mips32` | `mipsle softfloat` | 尽力而为：通用 MIPS32 little-endian |
| `mips_24kc` | `mips softfloat` | ath79、lantiq |
| `mips_4kec` | `mips softfloat` | MIPS32 big-endian |
| `mips_mips32` | `mips softfloat` | 尽力而为：通用 MIPS32 big-endian |
| `mips64_octeonplus` | `mips64 softfloat` | Octeon big-endian |
| `mips64_mips64r2` | `mips64 softfloat` | MIPS64r2 big-endian |
| `mips64el_mips64r2` | `mips64le softfloat` | MIPS64r2 little-endian |
| `i386_pentium4` | `386 GO386=sse2` | x86/generic |
| `i386_pentium-mmx` | `386 GO386=softfloat` | i386，无 SSE2 |
| `x86_64` | `amd64` | x86/64 |
| `loongarch64_generic` | `loong64` | LoongArch 64 |
| `riscv64_riscv64` | `riscv64` | RISC-V 64 |
| `powerpc64_e5500` | `ppc64` | 尽力而为：Go ppc64 假定 POWER8 |

### 未生成

| arch | 原因 |
|---|---|
| `armeb_xscale` | Go 不支持 big-endian ARM (`armeb`) |
| `powerpc_464fp` | Go 不支持 32 位 PowerPC |
| `powerpc_8548` | Go 不支持 32 位 PowerPC |

### UA3F / UA2F 覆盖

UA3F 3.7.0 上游提供 10 个架构的 ipk（aarch64_cortex-a53/a72/a76/generic、
arm_cortex-a15/a7_neon/a9_vfpv3-d16、mipsel_24kc、mips_24kc、x86_64）。
UA2F 4.10.2 提供 9 个（少 aarch64_cortex-a76）。i386、loong64、riscv64、
mips64、armv5/v6 等只有认证器，没有预编译 UA3F/UA2F；这些平台如需
DPI 伪装，需要从源码自行编译 UA2F。

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

单个架构：

```sh
cd openwrt
GOARCH=mipsle PKG_ARCH=mipsel_24kc sh scripts/build-ipk.sh

GOARCH=arm64 PKG_ARCH=aarch64_cortex-a53 sh scripts/build-ipk.sh

GOARCH=arm GOARM=7 PKG_ARCH=arm_cortex-a7_neon-vfpv4 sh scripts/build-ipk.sh

GOARCH=amd64 PKG_ARCH=x86_64 sh scripts/build-ipk.sh
```

全架构（33 个）：

```sh
cd openwrt
VERSION=0.11.4 sh scripts/build-arches.sh
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
