# Wave 1 多机型构建结果（2026-11-23）

> 环境：Ubuntu 26.04.1、Go 1.25.5、OpenWrt 24.10.0、
> Image Builder `ramips/mt7621`。
> 构建入口：`openwrt/scripts/build-target.sh`，批量脚本
> `openwrt/scripts/build-wave1.sh`。
> 产物目录：`openwrt/firmware/ramips/mt7621/<profile>/0.11.4/`。

## 1. 结果

15 个 profile 全部构建成功；SHA256SUMS 逐目录校验通过。

| Profile | 层 | UA | sysupgrade 字节 | 大小 | 包数 | SHA256 前 16 位 |
|---|---|---|---:|---:|---:|---|
| `d-team_newifi-d2` | standard | ua3f | 15,139,905 | 14.4 MiB | 207 | `0c3b456532cc2388…` |
| `lenovo_newifi-d1` | standard | ua3f | 15,205,463 | 14.5 MiB | 209 | `bfeea8430f401caf…` |
| `mikrotik_routerboard-750gr3` | lite | ua2f | 9,962,632 | 9.5 MiB | 163 | `984b6ce3f4c581df…` |
| `phicomm_k2p` | lite | ua2f | 11,273,276 | 10.8 MiB | 187 | `b645cd17a2cf8a27…` |
| `tplink_archer-a6-v3` | lite | ua2f | 11,273,290 | 10.8 MiB | 188 | `1b603ecd95ed5d49…` |
| `tplink_archer-c6-v3` | lite | ua2f | 11,273,290 | 10.8 MiB | 188 | `bd44028d28a2daf0…` |
| `xiaomi_mi-router-3-pro` | standard | ua3f | 15,565,931 | 14.8 MiB | 209 | `4d3122e366236e4a…` |
| `xiaomi_mi-router-3g-v2` | standard | ua3f | 15,008,885 | 14.3 MiB | 201 | `af87be82faabcf9a…` |
| `xiaomi_mi-router-4` | lite | ua2f | 10,835,011 | 10.3 MiB | 187 | `10bb4ed3c6b5d4ec…` |
| `xiaomi_mi-router-4a-gigabit-v2` | lite | ua2f | 11,273,323 | 10.8 MiB | 188 | `4c0e5b41b8cb2bab…` |
| `xiaomi_mi-router-4a-gigabit` | lite | ua2f | 10,880,098 | 10.4 MiB | 186 | `284900f7402c2884…` |
| `xiaomi_mi-router-ac2100` | standard | ua3f | 15,494,226 | 14.8 MiB | 203 | `5ddf8ec46748f22b…` |
| `xiaomi_mi-router-cr6606` | standard | ua3f | 15,330,386 | 14.6 MiB | 203 | `f2cb1ca3f4bc4e60…` |
| `xiaomi_mi-router-cr6608` | standard | ua3f | 15,330,386 | 14.6 MiB | 203 | `0e1e1f429c1fac28…` |
| `xiaomi_mi-router-cr6609` | standard | ua3f | 15,330,386 | 14.6 MiB | 203 | `12dbbb7734a7dd2d…` |

合计 15 个 sysupgrade，199,176,488 字节（约 190 MiB）。
完整 SHA256 见各版本目录的 `SHA256SUMS`。

> 构建中遇到一个环境问题：Image Builder 的 `make clean` 会清空本地
> 包索引，批量重打时 CR6606/8/9 因 `downloads.openwrt.org` 拉包失败。
> 已把 `repositories.conf` 切到清华镜像；`build-target.sh` 现在默认
> 使用 `https://mirrors.tuna.tsinghua.edu.cn/openwrt`，需要官方源时
> 传 `MIRROR=official`。CR660x 已用镜像源重打并通过校验。

部分 profile 还生成了 `-squashfs-factory.bin`（Archer A6/C6 v3、
R3P），已一并放入对应目录。

## 2. 分层

### lite（UA2F）

`scripts/packages/lite.txt` + `ua2f` ipk + `ruijie-auth` ipk：

- LuCI + 中文、DoH + LuCI/中文；
- `kmod-nft-queue`、`kmod-nft-tproxy`、`kmod-nfnetlink-queue`、
  `kmod-nf-conntrack-netlink`、`ipset`、`ip-full`；
- UA2F 由自己的 init 启停；`ruijie-disguise` 保持 `basic`（nft
  TTL/MSS/IPID）。

实测 9.96–11.27MB，适配 16MB 机型。R4A 的 profile `IMAGE_SIZE` 为
14,848KB，10.88MB 镜像有约 4MB 余量。

### standard（UA3F）

`scripts/packages/standard.txt` + `ua3f` ipk + `ruijie-auth` ipk：

- 在 lite 基础上加 `iptables`/`iptables-mod-*`/`ipset`（UA3F 依赖）；
- `ruijie-disguise` 设为 `full`，启动 UA3F；
- `desync_reorder=0`、`desync_inject=0`，只保留 TTL/TCP timestamp/window。

实测 15.0–15.6MB，适配 32MB+ 机型。R4A 的 14,848KB 上限装不下，
不要给 16MB 机型出 standard 镜像。

## 3. 首启默认值

`files/boards/common/etc/uci-defaults/zz-common-defaults` 统一设置：

- `ruijie-auth.main.device=wan`、`reauth_interval=0`、
  `proactive_reauth=0`、`quiet_window=1`、`heartbeat=0`；
- `luci.main.lang=zh_cn`；
- `uhttpd` 监听 `0.0.0.0:80/443`；
- 关闭 flow offloading；
- `dnsmasq filter_aaaa=1`；
- DoH：`https://doh.pub/dns-query`（bootstrap `119.29.29.29,223.5.5.5`），
  监听 `127.0.0.1:5053`，dnsmasq 转发到该端口；
- UA2F：启用、`handle_fw/handle_tls/handle_intranet=1`、自定义 Chrome UA；
- UA3F：`disguise=full`、`desync_reorder=0`、`desync_inject=0`。

矩阵构建只复制通用 overlay：
`ruijie-auth`、`ruijie-disguise`、hotplug、sysctl、`99-luci-cache-bust`。
R3G 专用的 `r3g-defaults`、`ua3f-scope`、`mwan3`、SQM、USB/IP、
custom-packages 不会进入 lite/standard 镜像。

## 4. 校验

- 15 个 `SHA256SUMS` 全部通过 `sha256sum -c`。
- 每个 manifest 都含 `ruijie-auth`、`https-dns-proxy` 和对应的
  `ua2f` 或 `ua3f`。
- lite rootfs：有 `ua2f`，没有 `ua3f`、`r3g-defaults`、`ua3f-scope`。
- standard rootfs：有 `ua3f`，没有 `ua2f`、`r3g-defaults`、`ua3f-scope`。
- R4A lite：squashfs 7.46MB，sysupgrade 10.88MB，186 包。

## 5. 待实机验证

- **R4A V1**：本地已有 Breed、编程器备份和刷机教程，优先上机。先备份
  factory/eeprom，再刷 `-squashfs-sysupgrade.bin`；如用 Breed 分区写入，
  注意 profile 的 `IMAGE_SIZE` 是 14,848KB，教程里的 `0x800000` 只是
  保守擦除范围。
- R4A 验证项：EAP-MD5 认证、UA2F 启动、DoH 解析、LuCI 中文、重启后
  配置保留、overlay 余量。
- 其他机型：至少各选 1 个 lite（K2P 或 Archer C6 v3）和 1 个 standard
  （Newifi D2 或 AC2100）做真机回归。
- 通过后再进入 Wave 2（ARM/AArch64：AX3000T、AX6S、AX3600、R7800、
  NanoPi R2S/R4S/R5S、x86_64）。
