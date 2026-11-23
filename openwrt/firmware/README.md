# OpenWrt 整机固件镜像

按 OpenWrt 的 target/subtarget/profile 组织，目录规则固定为：

```text
firmware/<target>/<subtarget>/<profile>/<version>/
```

每个 profile 目录下的 `latest.txt` 指向当前推荐版本。

每个版本目录包含：

- `openwrt-<openwrt-version>-<target>-<subtarget>-<profile>-squashfs-sysupgrade.bin`
- `openwrt-<openwrt-version>-<target>-<subtarget>-<profile>-squashfs-kernel1.bin`
- `openwrt-<openwrt-version>-<target>-<subtarget>-<profile>-squashfs-rootfs0.bin`
- `openwrt-<openwrt-version>-<target>-<subtarget>-<profile>-breed.bin`（如机型需要）
- `openwrt-<openwrt-version>-<target>-<subtarget>-<profile>.manifest`
- `BUILD_INFO.txt`
- `SHA256SUMS`

## 当前内容

```text
firmware/
  ramips/mt7621/
    xiaomi_mi-router-3g/             0.10.0, 0.11.4（R3G 全功能）
    xiaomi_mi-router-4a-gigabit/            0.11.4（lite/UA2F）
    xiaomi_mi-router-4a-gigabit-v2/         0.11.4（lite/UA2F）
    xiaomi_mi-router-4/                     0.11.4（lite/UA2F）
    phicomm_k2p/                            0.11.4（lite/UA2F）
    tplink_archer-c6-v3/                    0.11.4（lite/UA2F）
    tplink_archer-a6-v3/                    0.11.4（lite/UA2F）
    mikrotik_routerboard-750gr3/            0.11.4（lite/UA2F）
    d-team_newifi-d2/                       0.11.4（standard/UA3F）
    lenovo_newifi-d1/                       0.11.4（standard/UA3F）
    xiaomi_mi-router-3-pro/                 0.11.4（standard/UA3F）
    xiaomi_mi-router-3g-v2/                 0.11.4（standard/UA3F）
    xiaomi_mi-router-ac2100/                0.11.4（standard/UA3F）
    xiaomi_mi-router-cr6606/                0.11.4（standard/UA3F）
    xiaomi_mi-router-cr6608/                0.11.4（standard/UA3F）
    xiaomi_mi-router-cr6609/                0.11.4（standard/UA3F）
  mediatek/filogic/xiaomi_mi-router-ax3000t/   # 预留
  mediatek/mt7622/xiaomi_redmi-router-ax6s/    # 预留
  qualcommax/ipq807x/xiaomi_ax3600/            # 预留
  x86/64/generic/                              # 预留
  rockchip/armv8/friendlyarm_nanopi-r2s/       # 预留
  bcm27xx/bcm2711/rpi-4/                       # 预留
```

Wave 1 的 15 个机型、大小、包数和完整校验见
[`docs/WAVE1_BUILD.md`](../../docs/WAVE1_BUILD.md)。

| 版本 | 设备 | 说明 |
|---|---|---|
| 0.10.0 | 小米路由器 3G 原版（R3G） | 精简全功能固件，完整 R3G 包集 |
| 0.11.4 | 小米路由器 3G 原版（R3G） | 当前版；SAM 心跳默认关闭，ZeroTier 内置，无线解锁 |
| 0.11.4 | Wave 1 的 15 个 mt7621 机型 | lite（UA2F）或 standard（UA3F） |

## 目录示例

```text
firmware/
  ramips/
    mt7621/
      xiaomi_mi-router-3g/
        0.11.4/
          openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-sysupgrade.bin
          openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-kernel1.bin
          openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-rootfs0.bin
          openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-breed.bin
          openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g.manifest
          BUILD_INFO.txt
          SHA256SUMS
  mediatek/
    filogic/
      xiaomi_mi-router-ax3000t/
        <version>/
  x86/
    64/
      generic/
        <version>/
```

## 构建

```sh
cd openwrt

# 单个机型
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
  sh scripts/build-target.sh scripts/profiles/xiaomi_mi-router-4a-gigabit.env

# Wave 1 批量
IB_DIR=/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64 \
  JOBS=8 sh scripts/build-wave1.sh
```

Profile env 在 `scripts/profiles/`，包集在 `scripts/packages/`。
`build-target.sh` 会把产物直接写入本目录的
`<target>/<subtarget>/<profile>/<version>/`。

## 校验

```sh
cd "firmware/ramips/mt7621/xiaomi_mi-router-3g/0.11.4"
sha256sum -c SHA256SUMS
```

## 刷写方式

- **OpenWrt 内升级**：刷 `-squashfs-sysupgrade.bin`。
- **Breed（R3G）**：分别写 `-squashfs-kernel1.bin`（`0x600000`）和
  `-squashfs-rootfs0.bin`（`0xA00000`）；或使用 `-breed.bin` 合并镜像。
- **不要跨机型刷**：R3G、R4A、AX3000T 的分区、Breed 布局和内核 ABI
  完全不同；刷错会变砖。其他机型使用对应目录下的固件。

R3G 的详细刷写步骤、分区偏移和回滚方式见
`D:\Files\Develop\OpenWrt\MiR3G_V1-Openwrt\固件刷写教程.txt` 与
[`docs/MANUAL_OPENWRT.md`](../../docs/MANUAL_OPENWRT.md)。

## 计划中的 profile

下列目录已预留，构建后按 `<version>/` 放入固件。完整优先级和容量分层见
[`docs/MODEL_PORTFOLIO_AUDIT.md`](../../docs/MODEL_PORTFOLIO_AUDIT.md)。

- `mediatek/filogic/xiaomi_mi-router-ax3000t`（128MB，full/standard）
- `mediatek/mt7622/xiaomi_redmi-router-ax6s`（128MB，full）
- `qualcommax/ipq807x/xiaomi_ax3600`（128MB，full）
- `x86/64/generic`（磁盘，full）
- `rockchip/armv8/friendlyarm_nanopi-r2s`（SD，full）
- `bcm27xx/bcm2711/rpi-4`（SD，full）

## 二进制不入库

固件 `.bin` 体积大，默认不进入 git；本目录保留 `BUILD_INFO.txt`、
`SHA256SUMS` 和结构。发布时把 `.bin` 上传到 GitHub Releases 或文件服务器，
或删除 `.gitignore` 中的 `openwrt/firmware/**/*.bin` 规则后再提交。
