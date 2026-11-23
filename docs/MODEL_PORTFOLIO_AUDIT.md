# 路由器机型适配审计（2026-11-23）

> 范围：OpenWrt 24.10.0，27 个常见路由器 target，1,343 个设备 profile。
> 方法：解析官方 `profiles.json`；实测认证器、UA3F、UA2F、DoH 的 ipk
> 体积；检查当前 `openwrt/scripts` 的 target/arch 耦合。
> 结论：项目本身不绑定 R3G，认证 ipk 也是可移植的；当前**构建链**仍是
> R3G 单目标。按功能分层后，约 900–1,100 个 profile 可以产出可用镜像，
> 但社区真正值得优先适配的是 50–70 个机型。

## 1. 结论摘要

| 问题 | 结论 |
|---|---|
| 预编译 UA3F/UA2F 覆盖多少架构 | UA3F 10 个、UA2F 9 个 OpenWrt 架构；合计直接覆盖 1,287/1,343 个 profile（95.8%） |
| 加上 bcm53xx 自编译 | 1,321/1,343（98.4%） |
| 完整 R3G 包集能装多少机型 | 约 400–450 个 profile（≥128MB 存储） |
| auth + UA3F + DoH 能装多少 | 约 500–550 个 profile（≥32MB 存储） |
| auth + UA2F + DoH 能装多少 | 约 300–350 个 profile（16MB 存储，需裁剪） |
| 仅认证能装多少 | 约 150–250 个 profile（8MB，边缘可行） |
| 社区优先适配 | 现在 12–13 个（mipsel）；2–4 周再加 18–20 个（ARM/AArch64）；1–2 个月 50–70 个 |
| 不建议投入 | 4MB flash、RT3883/Octeon/APM821xx 的 DPI 伪装、16MB + UA3F |

关键判断：

1. **认证器本体已经足够通用**：Go 静态二进制，`GOARCH`/`PKG_ARCH` 可覆盖
   MIPS/ARM/AArch64/x86；LuCI 页面是 `all`，DoH 也是全架构包。
2. **UA3F 和 UA2F 是两种不同量级的东西**：UA3F 安装后约 17.8MB，UA2F
   只有 788KB。16MB 机型只能走 UA2F；32MB 以上才有 UA3F 的空间。
3. **R4A 不是“换个 PROFILE 就行”**：16MB flash、约 8MB 固件区，官方
   OpenWrt 镜像已 7.2MB，Lienol 带 UA2F 的镜像 8.4MB；再塞 2.8MB 的
   认证 ipk 必然超。需要专门的精简包集，甚至无 LuCI/仅 EAP-MD5 的
   lite 二进制。
4. **当前最大的工程缺口不是协议，而是构建矩阵**：`fetch-imagebuilder.sh`
   只认 mt7621，`build-image.sh` 只认 R3G，`kmods-r3g.txt` 和
   `zz-r3g-defaults` 都是 R3G 专用；`ruijie-disguise` 也只有 UA3F 模式，
   没有 UA2F 集成。

## 2. 当前构建链审计

| 位置 | 现状 | 影响 |
|---|---|---|
| `openwrt/scripts/fetch-imagebuilder.sh:10-14` | 固定 `24.10.0` + `ramips/mt7621` | 其他 target 无法直接构建 |
| `openwrt/scripts/build-image.sh:13-14` | `IB` 固定 mt7621，`PROFILE` 默认 `xiaomi_mi-router-3g` | 换机型要改脚本 |
| `build-image.sh:18-22` | UA3F 固定 `mipsel_24kc` | ARM/AArch64 需换 ipk |
| `build-image.sh:53-56` | mihomo 固定 `mipsle-softfloat` | 非 mipsle 的 OpenClash 核心缺失 |
| `build-image.sh:39` | `kmods-r3g.txt` | 含 mt76/USB/R3G 板载，换 target 会装不上 |
| `build-image.sh:64-116` | 完整 R3G 包集（OpenClash/USB/SQM/ZeroTier…） | 只适合 ≥128MB 存储 |
| `build-image.sh:117` | 认证 ipk 固定 `mipsel_24kc` | `build-ipk.sh` 其实支持 `GOARCH`/`PKG_ARCH`，但这里没透传 |
| `build-image.sh:181` | 输出路径固定 `ramips/mt7621` | 仅影响提示 |
| `files/etc/uci-defaults/zz-r3g-defaults` | R3G 专用默认值（mwan3/SQM/USB/IP/OpenClash） | 其他机型需要 board overlay |
| `files/etc/init.d/ruijie-disguise:5-9,40-44` | `basic` = nft TTL/IPID/MSS；`full` = 启动 UA3F | 没有 UA2F 模式 |
| `files/usr/sbin/ua3f-scope` | 只处理 UA3F 的 nft 链 | UA2F 没有等价 scope |
| `scripts/build-go.sh:6,20` | `GOARCH` 默认 `mipsle`，可覆盖 | 已具备跨架构能力 |
| `scripts/build-ipk.sh:18,82` | `PKG_ARCH` 默认 `mipsel_24kc`，可覆盖 | 已具备跨架构能力 |

结论：认证 ipk 和 LuCI 本身已经是“换架构即可”的状态；整机固件脚本
还没有 matrix 化。要实现多机型，优先改 `build-image.sh` 和
`fetch-imagebuilder.sh`，不是改协议代码。

## 3. 体积预算

| 组件 | ipk 压缩后 | 安装后 | 说明 |
|---|---:|---:|---|
| `ruijie-auth` 0.10.0 mipsel | 2.85MB | 8,088KB | Go 静态二进制占绝对大头 |
| UA3F 3.7.0 mipsel | 5.5MB | 17,817,600B | 另需 iptables/ipset/kmod |
| UA2F 4.10.2 mipsel 24.10 | 261KB | 788,480B | 另需 libnetfilter/ip-full/kmod-nft |
| `https-dns-proxy` | 33KB | 100KB | 另需 libcurl/libcares/libev |
| R3G 完整固件 | 56.5–57.5MB sysupgrade | 507–511 包 | OpenClash/mihomo 占很大比例 |

由此得到容量门槛：

- **16MB flash（约 8MB 固件区）**：只能 UA2F；认证 2.85MB + UA2F 0.26MB +
  DoH 0.03MB + 基础系统，必须把基础镜像压到约 5MB 以内。R4A 需要
  专门的精简包集。
- **32MB flash**：可以 auth + UA3F + DoH + 基础 LuCI，约 20–25MB；
  OpenClash/USB 全功能仍不够。
- **128MB NAND**：可以完整 R3G 包集（56.5MB），overlay 还有约 50MB。
- **8MB flash**：认证 8MB 安装后压缩到约 2.5MB，加基础系统约 5–6MB，
  只剩 0–1MB 余量；只能做无 LuCI 或极简 LuCI 的认证专用镜像。

> 不建议对 Go 二进制上 UPX。路由器上启动解压、内存占用和误报都不划算；
> 更可控的做法是给 lite 层做一个只含 EAP-MD5 的构建 tag，砍掉 PEAP/TLS
> 依赖。

## 4. 机型覆盖估算

以下按 OpenWrt 24.10 `profiles.json` 统计，已排除 `generic`/`default`
profile。UA 列表示上游是否有匹配架构的 UA3F/UA2F 24.10 ipk。

| Target | 架构 | profile 数 | UA 预编译 | 典型存储 | 建议层 |
|---|---|---:|---|---|---|
| ramips/mt7621 | mipsel_24kc | 264 | 是 | 16/32/128MB | F/S/L |
| ramips/mt7620 | mipsel_24kc | 129 | 是 | 8/16MB | L/A |
| ramips/mt76x8 | mipsel_24kc | 104 | 是 | 8/16MB | L/A |
| ath79/generic | mips_24kc | 372 | 是 | 4/8/16MB | A/L |
| ath79/nand | mips_24kc | 32 | 是 | 128MB | F/S |
| ipq40xx/generic | arm_cortex-a7_neon-vfpv4 | 67 | 是 | 32/128MB | S/F |
| ipq806x/generic | arm_cortex-a15_neon-vfpv4 | 31 | 是 | 128MB | F |
| mediatek/mt7622 | aarch64_cortex-a53 | 23 | 是 | 128MB | F |
| mediatek/filogic | aarch64_cortex-a53 | 80 | 是 | 128MB+ | F |
| qualcommax/ipq807x | aarch64_cortex-a53 | 29 | 是 | 128MB+ | F |
| bcm53xx/generic | arm_cortex-a9 | 34 | 需自编译/验证 | 128MB | F |
| mvebu/cortexa9 | arm_cortex-a9_vfpv3-d16 | 31 | 是 | 128MB | F |
| mvebu/cortexa53 | aarch64_cortex-a53 | 9 | 是 | 128MB | F |
| rockchip/armv8 | aarch64_generic | 32 | 是 | SD/eMMC | F |
| sunxi/cortexa7 | arm_cortex-a7_neon-vfpv4 | 30 | 是 | SD/eMMC | S/F |
| sunxi/cortexa53 | aarch64_cortex-a53 | 13 | 是 | SD/eMMC | F |
| lantiq/xrx200 | mips_24kc | 26 | 是 | 8/16MB | L/A |
| ramips/rt305x | mipsel_24kc | 10 | 是 | 4/8MB | A |
| ramips/rt3883 | mipsel_74kc | 10 | 否 | 8/16MB | A |
| octeon/generic | mips64_octeonplus | 7 | 否 | NAND | A |
| apm821xx/nand | powerpc_464fp | 5 | 否 | NAND | A |
| x86/64 | x86_64 | 1（通用） | 是 | 磁盘 | F |
| bcm27xx/bcm2711 | aarch64_cortex-a72 | 1 | 是 | SD | F |
| bcm27xx/bcm2712 | aarch64_cortex-a76 | 1 | 是 | SD | F |
| **合计** | | **1,343** | **1,287 直接 + 34 自编译** | | |

分层估算（同一机型只计一层，数字为量级）：

| 层 | 定义 | 估算 profile 数 |
|---|---|---:|
| F 完整 | 与 R3G 相同包集，≥128MB 存储、≥256MB RAM | 400–450 |
| S 标准 | 认证 + UA3F + DoH + 基础 LuCI，≥32MB | 500–550（累计） |
| L 轻量 | 认证 + UA2F + DoH + 极简 LuCI，16MB | 300–350 |
| A 仅认证 | 认证 + 极简 LuCI/CLI，8MB | 150–250（边缘） |
| X 不可行 | 4MB，或 RT3883/Octeon/APM821xx 的 DPI 伪装 | 150–200 |

> 这些是“可以产出镜像”的估算，不是逐机型实机验证。Flash 分区、
> USB、Wi-Fi 驱动和交换机 DSA 差异仍需逐机型确认；同一 target 内
> 16MB 与 128MB 机型的可用空间差别很大。

## 5. 社区优先适配清单

### Wave 1：mipsel，复用现有工具链（现在）

| 机型 | Target | Flash/RAM | 层 | 说明 |
|---|---|---|---:|---|
| 小米 R4A 千兆 V1 | ramips/mt7621 | 16MB / 128MB | L | 本地已有固件与刷机教程，首选验证 |
| 小米 R4A 千兆 V2 | ramips/mt7621 | 16MB / 128MB | L | 与 V1 分区不同，单独 profile |
| 小米路由器 4 | ramips/mt7621 | 16MB / 128MB | L | 16MB 代表机型 |
| 斐讯 K2P | ramips/mt7621 | 16MB / 128MB | L | 社区保有量大 |
| TP-Link Archer C6 v3 / A6 v3 | ramips/mt7621 | 16MB / 128MB | L | 同硬件双型号 |
| MikroTik hEX RB750Gr3 | ramips/mt7621 | 16MB / 256MB | L | 无 Wi-Fi，纯有线 |
| 新路由 3 D2 | ramips/mt7621 | 32MB / 512MB | S | UA3F 可用 |
| 联想 Newifi D1 | ramips/mt7621 | 32MB / 256MB | S | UA3F 可用 |
| 小米 R3P | ramips/mt7621 | 128MB NAND | F | 完整包集 |
| 小米 AC2100 / Redmi AC2100 | ramips/mt7621 | 128MB NAND | F | 完整包集 |
| 小米 CR6606/6608/6609 | ramips/mt7621 | 128MB NAND | F | 完整包集 |
| 小米 R3G v2 | ramips/mt7621 | 128MB NAND | F | R3G 变体 |

Wave 1 约 12 个机型；R4A 是第一个要做的，因为本地已有固件、备份和
刷机教程，验证成本最低。

### Wave 2：ARM/AArch64，补 UA3F/UA2F 架构包（2–4 周）

| 机型 | Target | 层 | 说明 |
|---|---|---:|---|
| 小米 AX3000T | mediatek/filogic | F/S | 当前热门 MT7981 |
| Redmi AX6000 | mediatek/filogic | F | MT7986 |
| GL.iNet MT2500 / MT3000 | mediatek/filogic | F | 社区热门 |
| CMCC RAX3000M | mediatek/filogic | F | 运营商定制 |
| 360T7 | mediatek/filogic | F | MT7981 |
| 小米 AX6S | mediatek/mt7622 | F | MT7622 |
| 小米 AX3600 / AX6000 | qualcommax/ipq807x | F | IPQ807x |
| Redmi AX6 | qualcommax/ipq807x | F | IPQ807x |
| GL-B1300 | ipq40xx/generic | S/F | IPQ40xx |
| Linksys EA6350v3 | ipq40xx/generic | F | IPQ40xx |
| Netgear R7800 | ipq806x/generic | F | IPQ806x |
| Linksys WRT1900ACS | mvebu/cortexa9 | F | mvebu |
| 友善 NanoPi R2S/R4S/R5S | rockchip/armv8 | F | 软路由 |
| Raspberry Pi 4/5 | bcm27xx | F | SD 启动 |
| x86_64 软路由 | x86/64 | F | 磁盘/SSD |

Wave 2 约 18–20 个机型。bcm53xx（R8000、RT-AC68U、K3）先放 Wave 3，
因为上游没有 `arm_cortex-a9` 预编译包，需要自编译或验证
`arm_cortex-a9_vfpv3-d16` 的 ABI 兼容性。

### Wave 3：长尾（1–2 个月）

- 其余 mt7621 16/32MB 机型；
- ipq40xx 32MB、mt7622/filogic 的运营商定制型号；
- ath79 16MB（Archer C7、GL-AR750 等）；
- bcm53xx（自编译 UA2F/UA3F）；
- lantiq 16MB。

合计 50–70 个机型，覆盖社区绝大多数常见硬改/刷机型号。

## 6. R4A 专项结论

本地资料：`D:\Files\Develop\OpenWrt\MiR4A_V1-Openwrt`，含官方固件、
Breed、Lienol 防多终端固件、编程器备份和 `固件刷写教程.txt`。

约束：

- 16MB SPI flash；教程按 `0x180000` 启动地址、`0x800000` 固件区刷写，
  约 8MB 可用。
- 官方 OpenWrt 镜像 7,209,588 字节（6.9MiB）。
- Lienol 防多终端固件 8,389,730 字节（8.0MiB），已含 UA2F，未含
  锐捷认证。
- 认证 ipk 2,847,901 字节（2.7MiB），安装后 8,088KB。
- UA3F 5.5MB ipk / 17.8MB 安装后，R4A 放不下。
- UA2F 261KB ipk / 788KB 安装后，可行。

结论：R4A 只能做 **L 层（auth + UA2F + DoH）**，且必须裁剪基础镜像：

- 去掉 OpenClash、USB、SQM、ZeroTier、mwan3、打印机、USB/IP 等；
- LuCI 只保留 `luci-base`、认证页面和 DoH 页面，去掉主题和无关应用；
- 或做无 LuCI 的 CLI 镜像；
- 如果仍超 8MB，给 lite 构建加“仅 EAP-MD5”tag，砍掉 PEAP/TLS 的
  `crypto/tls` 依赖，把认证二进制从 8.1MB 压到约 5–6MB。

不建议对 16MB 机型承诺 UA3F；UA2F 是这个容量段的正确工具。

## 7. 工程改造建议

### 7.1 目标矩阵

新增 `openwrt/targets.json`（或 `targets/*.env`），至少包含：

```json
{
  "version": "24.10.0",
  "targets": [
    {
      "id": "ramips-mt7621",
      "target": "ramips/mt7621",
      "arch": "mipsel_24kc",
      "goarch": "mipsle",
      "ua": ["ua3f", "ua2f"],
      "profiles": {
        "full": ["xiaomi_mi-router-3g", "xiaomi_mi-router-3-pro"],
        "standard": ["d-team_newifi-d2", "lenovo_newifi-d1"],
        "lite": ["xiaomi_mi-router-4a-gigabit", "phicomm_k2p"]
      }
    }
  ]
}
```

`fetch-imagebuilder.sh` 接受 `TARGET/SUBTARGET`；`build-image.sh` 接受
`TARGET/PROFILE/ARCH/TIER/UA`；`build-ipk.sh` 的 `GOARCH/PKG_ARCH` 已经
具备，只需从 matrix 透传。

### 7.2 分层包集

把 `DEFAULT_PACKAGES` 拆成：

- `packages/full.txt`：现有 R3G 包集；
- `packages/standard.txt`：认证 + UA3F + DoH + 基础 LuCI；
- `packages/lite.txt`：认证 + UA2F + DoH + 极简 LuCI；
- `packages/auth-only.txt`：认证 + CLI/极简 LuCI。

### 7.3 UA2F 集成

`ruijie-disguise` 增加 `ua2f` 模式：

- 启动/停止 `ua2f`；
- 设置 `ua2f.enabled=1`、`handle_fw/handle_tls/handle_intranet`；
- 设置 `custom_ua`；
- 关闭 flow offloading；
- 如果保留 UA3F 的 `ua3f-scope` 思路，为 UA2F 增加按校园出口限定的
  iptables/nft 规则，避免影响 USB/4G 出口。

### 7.4 每 target 的 kmod

`kmods-r3g.txt` 只适用于 mt7621 + R3G。每个 target 至少需要：

- 通用：`kmod-nft-queue`、`kmod-nft-tproxy`、`kmod-nfnetlink-queue`、
  `kmod-nf-conntrack-netlink`、`iptables` 相关模块、`ipset`；
- 设备：该 target 的 Wi-Fi/交换机/USB kmod；
- 不要跨 target 复制 mt76/USB 列表，否则 Image Builder 会因包不存在失败。

### 7.5 board overlay

把 `zz-r3g-defaults` 拆成：

- `files/etc/uci-defaults/90-ruijie-base`：认证、DoH、LuCI 中文、防火墙；
- `files/boards/<profile>/`：WAN 口名、LED、无线、mwan3/SQM 等设备默认值。

`ruijie-auth` 的 WAN 自动识别已经能覆盖大部分机型，设备 overlay 主要
处理多 WAN、USB 共享、交换机和无线差异。

### 7.6 验证

- 每个 target 至少构建 1 个 `full` 或 `standard` + 1 个 `lite`；
- x86_64 QEMU 做 LuCI/CLI 冒烟；
- 每个 Wave 1 机型至少一次真机认证；
- 记录镜像大小、overlay 余量、UA3F/UA2F 运行状态和 CPU 占用。

## 8. 数据来源

- OpenWrt 24.10.0 `profiles.json`（27 个 target，Tsinghua 镜像）。
- UA3F v3.7.0 release assets（10 个架构 ipk）。
- UA2F v5.2.0 release assets（9 个架构 24.10 ipk；缺 aarch64_cortex-a76）。
- `https-dns-proxy` 2026.05.06-r1 mipsel_24kc 包索引。
- 本仓库 `dist/openwrt/BUILD_INFO.txt`、`ruijie-auth` ipk、UA3F vendor ipk。
- 本地 `MiR4A_V1-Openwrt` 固件、备份和 `固件刷写教程.txt`。

> 备注：profile 数来自官方 24.10.0 元数据；Flash/RAM 分层是按 target
> 和常见机型的估算，不是逐设备实测。正式适配前应以具体机型的
> `ubus call system board`、分区表和实测镜像大小为准。
