# Profile env

每个 `.env` 描述一个 OpenWrt profile：

```sh
TARGET=ramips
SUBTARGET=mt7621
PROFILE=xiaomi_mi-router-4a-gigabit
ARCH=mipsel_24kc
GOARCH=mipsle
TIER=lite
UA=ua2f
```

| 字段 | 说明 |
|---|---|
| `TARGET` / `SUBTARGET` | OpenWrt target |
| `PROFILE` | Image Builder profile |
| `ARCH` | opkg `arch_packages`，决定 ipk 目录 |
| `GOARCH` | Go 交叉编译目标（`mipsle`/`mips`/`arm64`/`arm`/`amd64`） |
| `TIER` | `lite`（UA2F）或 `standard`（UA3F） |
| `UA` | `ua2f`、`ua3f` 或 `none` |

## 构建

```sh
cd openwrt
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
  sh scripts/build-target.sh scripts/profiles/<name>.env
```

批量：

```sh
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
  JOBS=8 sh scripts/build-wave1.sh
```

## Wave 1

lite/UA2F：R4A 千兆 V1/V2、Mi Router 4、K2P、Archer C6 v3、Archer A6 v3、
MikroTik hEX。

standard/UA3F：Newifi D2/D1、R3P、R3G v2、AC2100、CR6606/6608/6609。

结果见 [`docs/WAVE1_BUILD.md`](../../../docs/WAVE1_BUILD.md)。
