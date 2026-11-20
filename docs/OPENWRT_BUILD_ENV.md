# OpenWrt 构建与 QEMU 测试环境

日期：2026-11-18

> **状态说明（2026-11-20）**：本文保留 2026-11-18/19 的早期原型构建
> 环境与踩坑记录。文中的包名与路径已按当前命名统一为 `ruijie-auth`；
> 构建脚本、产物与 LuCI 路径以 `openwrt/README.md`、
> `docs/MANUAL_OPENWRT.md`、`docs/OPENWRT_PORT.md` 为准。数值与大小
> 来自当时构建，不代表当前版本。

主机：Ubuntu 26.04.1 LTS（VMware），8 vCPU，7.2GB RAM，约 30GB 可用。
本文只记录环境与命令，不保存任何登录凭据。

## 0. 源码与编译工作流

- **源码统一放在实体机仓库**：`openwrt/`（Go daemon、LuCI、UCI、构建脚本）。
- **Windows 实体机只做编辑、提交、推送**；不装 Go，不跑 Image Builder。
- **Ubuntu VM 只做编译、测试、组镜像**：
  ```sh
  cd ~/src/RuijieCampusAuth/openwrt   # 按实际 clone 目录调整
  sh scripts/build-go.sh
  sh scripts/build-image.sh
  ```
- 构建产物在 `openwrt/build/`，不提交。
- 仓库是私有的，Ubuntu 直接 `git clone/pull` 需要认证。三选一：
  1. 在 Ubuntu 生成只读 deploy key，加到 GitHub 仓库的 Deploy keys；
  2. 在 Ubuntu 上 `gh auth login`（使用 repo 权限）；
  3. 离线/临时：Windows 侧 `git bundle create`，把 bundle 拷到 Ubuntu 后
     `git clone`/`git fetch`。
- 本轮编译使用源码同步到 Ubuntu 的临时工作副本；canonical 流程仍是
  GitHub pull。deploy key 或 `gh auth login` 配置好后，Ubuntu 侧直接
  `git pull` 即可。

## 1. 工具链现状

| 组件 | 路径/版本 | 状态 |
| --- | --- | --- |
| Go | `/usr/local/go`，go1.25.5，`/usr/local/bin/go` 软链 | 已装，mipsle 交叉编译通过 |
| Image Builder | `/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64` | 已解压，profile `xiaomi_mi-router-3g` 可用 |
| Image Builder 包 | `/opt/openwrt/imagebuilder-24.10.0-mt7621.tar.zst` | SHA256 `37b9bff3...571b3e` |
| SDK | 未装 | 只有做 `.ipk` 时才需要；`FILES=` 不需要 |
| bison / genext2fs | apt 安装 | Image Builder 前置检查通过 |
| Docker | 29.1.3，镜像为空 | 可用作 Image Builder 兜底 |
| QEMU | 10.2.1，system x86/mipsel + qemu-user | 可用；无 `/dev/kvm`，走 TCG |

### Ubuntu 26.04 的坑：uutils `install`

Ubuntu 26.04 默认 `/usr/bin/install` 指向 uutils coreutils，`install --version`
不含 `GNU coreutils`，OpenWrt Image Builder 的前置检查会直接失败：

```text
Checking 'install'... failed.
Build dependency: Please install GNU 'install'
```

系统里已有 GNU 版本 `/usr/bin/gnuinstall`。只对构建生效的修复：

```sh
mkdir -p /opt/openwrt/gnu-bin
ln -sf /usr/bin/gnuinstall /opt/openwrt/gnu-bin/install
```

构建时把该目录放到 PATH 前面：

```sh
PATH=/opt/openwrt/gnu-bin:$PATH make image ...
```

不要全局替换 `/usr/bin/install`，避免影响系统其他工具。

## 2. R3G 镜像构建

```sh
cd /opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64
PATH=/opt/openwrt/gnu-bin:$PATH make image \
  PROFILE=xiaomi_mi-router-3g \
  PACKAGES="luci-ssl" \
  FILES=files/ -j4
```

输出：

```text
bin/targets/ramips/mt7621/openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-sysupgrade.bin
```

当前 stock 构建结果：

- sysupgrade 约 7.1MB
- kernel1 约 3.1MB
- rootfs0 约 5.0MB
- clean stock sysupgrade SHA256：
  `c4742ea371e7053933a308e1ef92761dad2dfd77aa4f13fc58992d10ec133e4a`
- `sha256sum -c sha256sums` 全部 OK
- manifest 包含 `luci-ssl`、`luci-base`、`luci-mod-*`、`luci-theme-bootstrap`

`FILES=files/` 覆盖已验证：放一个 `/etc/ruijie-smoke` 进
`files/etc/`，重新构建后 `unsquashfs -l root.squashfs` 能看到
`squashfs-root/etc/ruijie-smoke`，LuCI 静态资源也正常。测试 marker 已删除。

### 自定义 R3G 镜像（2026-11-19）

源码树里的脚本会先交叉编译 mipsle，再组装 `files/` 覆盖并调用 Image Builder：

```sh
cd openwrt
sh scripts/build-image.sh
```

结果：

- `openwrt-24.10.0-ramips-mt7621-xiaomi_mi-router-3g-squashfs-sysupgrade.bin`
  约 14MB
- SHA256 `001fb2df06f47760fe6e90a32f3e343fbc10ae0432e7e81ffb029ea6dfc1373a`
- `kernel1.bin` 3.1MB，SHA256
  `f1718d099cadae68bd6d24c4555e9fac8dcf5878bc4dc64d5b359255d4d5bf9a`
- `rootfs0.bin` 12MB，SHA256
  `4aa9a27d571e71f11fba704b1d81fbc71aeda43d3f26986bdba84a76d99f1255`
- 插件 ipk：`openwrt/build/ipk/ruijie-auth_0.1.0_mipsel_24kc.ipk`
  （1.4MB，外层 tar.gz 格式）
- rootfs squashfs 约 10MB（UA3F 17MB 压缩后 + 其他包）
- `root.squashfs` 已确认包含：
  - `/usr/sbin/ruijie-auth`（3.9MB，mipsle 静态，opkg 所有）
  - `/usr/bin/ua3f`（17MB，mipsle musl 动态，UA3F 3.7.0）
  - `/etc/config/ua3f`（NFQUEUE，L3 开，UA 改写关）
  - `/etc/init.d/ua3f`（来自 UA3F ipk）
  - `/etc/config/https-dns-proxy`（AliDNS + Tencent DoH，IPv4）
  - `/etc/init.d/https-dns-proxy`
  - `/etc/config/ruijie-auth`（`device=wan`、`disguise=full`，0600）
  - `/etc/init.d/ruijie-auth`
  - `/etc/init.d/ruijie-disguise`（nft TTL 128 + MSS 1460）
  - `/etc/sysctl.d/90-ruijie.conf`
  - `/etc/hotplug.d/iface/90-ruijie-auth`
  - `/etc/hotplug.d/net/90-ruijie-usbwan`（USB 共享自动接管）
  - `/etc/uci-defaults/90-ruijie-auth`（usbwan + firewall zone）
  - `/etc/rc.d/S20https-dns-proxy`、`S95ruijie-auth`、`S99ruijie-disguise`
  - `/usr/share/luci/menu.d/luci-app-ruijie-auth.json`
  - `/usr/share/rpcd/acl.d/luci-app-ruijie-auth.json`
  - `/www/luci-static/resources/view/ruijie-auth/{overview,accounts,servers,mac,settings,logs}.js`
  - `/usr/lib/opkg/info/ruijie-auth.{control,list,conffiles}`
  - `/usr/lib/opkg/status` 中有 `Package: ruijie-auth` +
    `Status: install ok installed`

包清单（manifest）确认包含：

```text
ruijie-auth, https-dns-proxy, luci-app-https-dns-proxy,
luci-i18n-https-dns-proxy-zh-cn, luci-i18n-base-zh-cn,
kmod-nft-queue, kmod-nft-tproxy, kmod-nfnetlink-queue,
kmod-nf-conntrack-netlink, iptables-nft, iptables-mod-tproxy,
iptables-mod-extra, iptables-mod-ipopt, iptables-mod-nfqueue,
iptables-mod-conntrack-extra, ipset, luci-compat,
kmod-usb-net, kmod-usb-net-rndis, kmod-usb-net-cdc-ether,
kmod-usb-net-cdc-ncm, kmod-usb-net-cdc-subset,
kmod-usb-net-ipheth
```

### 插件 ipk 与免刷更新

`build-image.sh` 先调用 `build-ipk.sh` 生成 `ruijie-auth` 包，再把本地 ipk
路径放进 `PACKAGES`，Image Builder 用 opkg 安装，所以固件里的应用文件
由 opkg 拥有。后续更新只需：

```sh
opkg install --force-reinstall /tmp/ruijie-auth_0.1.0_mipsel_24kc.ipk
```

`/etc/config/ruijie-auth` 是 conffile，账号和设置保留；`postinst` 重启
`ruijie-auth` 与 `ruijie-disguise`。

构建 ipk 时踩到一个坑：Image Builder 自带的 host opkg
（`38eccbb1`, 2024-10-16）读取 GNU `ar` 格式的 ipk 会段错误，
`opkg install` 直接 core dump。改成和 UA3F 一样的外层 `tar.gz`
（`./debian-binary`、`./control.tar.gz`、`./data.tar.gz`）后安装和升级
都正常，conffile 也按预期保留。不要把 `build-ipk.sh` 改回 `ar rc`。

### Breed 镜像

标准 sysupgrade 是 OpenWrt 的 tar，Breed 升级页不认。用
`eg15/mir3g-breed-image-maker` 重打包：

```sh
cd /opt/openwrt/breed
./make_mir3g_breed_image.sh \
  <...>-squashfs-kernel1.bin <...>-squashfs-rootfs0.bin
```

产物 `openwrt_for_breed.bin`：

- 大小 20,185,088 B（8MB 头部 + 11,796,480 B rootfs）
- SHA256 `eb4e152b8412d9af8581ffc3a0bfb17f0ca689140b8ca32e0924514b57249a65`
- 布局：kernel1 在偏移 0 和 4M，rootfs0 从 8M 开始
- 输入 kernel1 3,181,020 B，小于 4MB 槽位

Breed 里刷到第二个分区，环境变量设
`autoboot.command = boot flash 0x600000`。工具脚本来自 Windows 时是
CRLF，Ubuntu 上先 `sed -i 's/\r$//'`。该镜像尚未在真机验证。

默认配置：

- `disguise=full`：`ruijie-disguise` 应用 nft TTL/MSS，并启动 UA3F L3
  （TTL 128、IPID 0、删除 TCP timestamp、SYN window 65535）。
- UA 改写关闭（`rewrite_mode DIRECT`），不强制改微信/企业微信 UA。
- sysctl：`ip_default_ttl=128`、`tcp_timestamps=0`、`tcp_ecn=0`、IPv6 关闭。
- `https-dns-proxy` 默认 AliDNS `dns.alidns.com` + Tencent `doh.pub`，
  IPv4 bootstrap，`force_dns` 开启。
- MAC 页/CLI 可写 `network.wan.macaddr`、`ifup wan` 并 reload daemon。
- 概览页在无认证服务器且外网在线时显示 `paused`。

构建时 `FILES` 覆盖层只放第三方服务配置：

```text
build/files/etc/config/ua3f
build/files/etc/config/https-dns-proxy
build/files/usr/bin/ua3f                  # 构建时从 UA3F ipk 抽取
build/files/etc/init.d/ua3f               # 构建时从 UA3F ipk 抽取
```

`ruijie-auth` 的二进制、init/hotplug 脚本、UCI 默认配置和 LuCI 文件都在
`ruijie-auth_<version>_mipsel_24kc.ipk` 里，由 opkg 安装并拥有。源码树对应：

```text
files/etc/config/ruijie-auth
files/etc/init.d/ruijie-auth
files/etc/init.d/ruijie-disguise
files/etc/sysctl.d/90-ruijie.conf
files/etc/hotplug.d/iface/90-ruijie-auth
files/etc/hotplug.d/net/90-ruijie-usbwan
luci-app-ruijie-auth/root/etc/uci-defaults/90-ruijie-auth
luci-app-ruijie-auth/root/usr/share/luci/menu.d/luci-app-ruijie-auth.json
luci-app-ruijie-auth/root/usr/share/rpcd/acl.d/luci-app-ruijie-auth.json
luci-app-ruijie-auth/htdocs/luci-static/resources/view/ruijie-auth/*.js
```

### `ucode` 下载失败的恢复

`opkg` 通过 `wget` 下载单个包时，`downloads.openwrt.org` 偶发重置。
日志会出现：

```text
opkg_download: Failed to download .../ucode_...ipk, wget returned 4
Checksum or size mismatch for package ucode
```

从官方镜像源或其他镜像单独取包，放进 `dl/`，文件名必须与 index 一致：

```sh
cd /opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64
PKG=ucode_2025.07.18~3f64c808-r2_mipsel_24kc.ipk
curl -fL --http1.1 --retry 5 -o "dl/$PKG" \
  "https://mirrors.tuna.tsinghua.edu.cn/openwrt/releases/24.10.0/packages/mipsel_24kc/base/$PKG"
```

校验依据在 `dl/openwrt_base`（gzip）：

```sh
zcat dl/openwrt_base | awk '/^Package: ucode$/{f=1} f{print} f&&/^$/{exit}'
```

`Size`/`SHA256sum` 对上即可。重跑 `make image` 会直接命中缓存。

## 3. 构建验证记录（2026-11-19）

- stock R3G 镜像构建成功，sha256 全过。
- `FILES=files/` 覆盖构建成功，marker 和 LuCI 静态资源进入 squashfs。
- Go 协议核心：`go test ./...`、`go vet ./...` 通过；569 字节私有尾
  SHA256 与 C#/Python 一致
  `779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5`。
- 完整 R3G 固件构建成功（14MB，SHA256
  `001fb2df06f47760fe6e90a32f3e343fbc10ae0432e7e81ffb029ea6dfc1373a`），
  含 UA3F 3.7.0 mipsel、https-dns-proxy + LuCI 中文、MAC 页、disguise、
  DoH 配置、iptables/nft kmods、USB tethering kmods；`rc.d` 有
  S20https-dns-proxy、S95ruijie-auth、S99ruijie-disguise。
- `ruijie-auth` 已改为 opkg 包：manifest 有 `ruijie-auth - 0.1.0`，
  squashfs 里有 `/usr/lib/opkg/info/ruijie-auth.*`，status 为 installed。
- host opkg 安装/强制重装 ipk 通过；升级前写入 `/etc/config/ruijie-auth`
  的 marker 在重装后仍存在，文件权限 0600 保持。
- 用 qemu-mipsel 跑 Image Builder root 里的真实 MIPS `uci`，执行
  `uci-defaults`：生成 `network.usbwan`（dhcp/usb0/metric 100），
  向 `firewall.@zone[1].network` 追加 `usbwan`，重复执行不重复追加。
- Breed 镜像 20,185,088 B，SHA256
  `eb4e152b8412d9af8581ffc3a0bfb17f0ca689140b8ca32e0924514b57249a65`；
  kernel1 在 0/4M、rootfs0 在 8M 的布局已逐段校验。
- 二进制核对：UA3F 为 mipsle musl 动态（`ld-musl-mipsel-sf.so.1`），
  ruijie-auth 为 mipsle 静态；nft `ttl set 128` + `maxseg size set 1460`
  语法在宿主 nft 上验证通过。
- Go 功能测试：UCI/账号池上限、服务器/探测缓存、控制 socket、
  supplicant 假认证器全链路、网关解析、`go vet` 全通过。
- x86_64 QEMU daemon 实测：`br-lan` 上打开 AF_PACKET；无认证服务器且
  localhost 探测在线时进入 `paused`，概览页显示账号/接口；Resume 按钮
  能拉回认证流程并再次暂停；账号页、服务器/缓存页、设置页均正常渲染。
- Go mipsle smoke：1.7MB 静态 ELF，`qemu-mipsel` 输出 `mipsle ok`。
- Go GB18030：`x/text` 编码 `校园宽带服务` 输出
  `b5e7d0c5bfedb4f8b7fecef1`，与 Python `gb18030` 一致；解码回原文正常。
- Go 1.25.5 在 `go get golang.org/x/text@latest` 时自动下载了
  go1.26.11 toolchain；这是 Go 的 toolchain 机制，不影响目标二进制。

## 4. QEMU x86_64 Web 测试环境

R3G 的 `sysupgrade.bin` **不能**直接在 QEMU 启动：QEMU 没有 MT7621 模型，
也没有对应的 DSA/MT7530 交换机。R3G 镜像只用于真机刷写；Web 后台和
OpenWrt 用户态用 x86_64 QEMU 验证。

镜像：

```text
/opt/openwrt/qemu/openwrt-x86-64.img
来源：openwrt-24.10.0-x86-64-generic-ext4-combined.img.gz
SHA256：a873d9f2e6e667d03dd06f75ed1f1b580beb7ce4337d5a44b60911a266965fb5
```

启动命令：

```sh
cd /opt/openwrt/qemu
qemu-system-x86_64 \
  -name openwrt-x86 \
  -m 512 -smp 2 \
  -drive file=openwrt-x86-64.img,format=raw \
  -netdev user,id=net0,net=192.168.1.0/24,\
hostfwd=tcp::18080-192.168.1.1:80,\
hostfwd=tcp::18443-192.168.1.1:443 \
  -device e1000,netdev=net0 \
  -display none -serial file:/opt/openwrt/qemu/serial.log \
  -daemonize -pidfile /opt/openwrt/qemu/qemu.pid
```

访问：

- VM 内部：`http://127.0.0.1:18080`、`https://127.0.0.1:18443`
- 从 Windows/宿主机：`http://<vm-ip>:28080`、`https://<vm-ip>:28443`
- 串口日志：`/opt/openwrt/qemu/serial.log`
- 停止：`kill $(cat /opt/openwrt/qemu/qemu.pid)`

QEMU 10.2.1 的 hostfwd 在非 loopback 来源上会出现“TCP 能连上、HTTP 不回包”
的现象（VM 内 `127.0.0.1:18080` 正常，`192.168.247.134:18080` 超时）。
用 socat 把外部端口转到 loopback 上的 hostfwd：

```sh
nohup socat TCP-LISTEN:28080,fork,reuseaddr TCP:127.0.0.1:18080 \
  >/tmp/socat-http.log 2>&1 & echo $! > /tmp/socat-http.pid
nohup socat TCP-LISTEN:28443,fork,reuseaddr TCP:127.0.0.1:18443 \
  >/tmp/socat-https.log 2>&1 & echo $! > /tmp/socat-https.pid
```

停止 socat：

```sh
kill "$(cat /tmp/socat-http.pid)" "$(cat /tmp/socat-https.pid)"
```

当前状态（2026-11-18）：QEMU 进程运行中，Windows 侧 `28080`/`28443`
返回 200，空密码登录返回 302 + `sysauth_http` cookie，Web 后台可用。

注意：

- VM 上 `bettercap` 占用了 `8081`，所以用 `18080/18443`。
- 没有 `/dev/kvm`，QEMU 走 TCG；OpenWrt 启动约 30–60 秒。
- x86 测试镜像（2026-11-19）：已注入完整 amd64 daemon + LuCI + procd
  init + 测试配置（`device=br-lan`、localhost 探测、一个测试账号）。
  实测 daemon 在 `br-lan` 上打开 AF_PACKET；无认证服务器时自动进入
  `paused`（“未检测到认证服务器，当前网络在线，暂停认证”），概览页
  显示账号/接口；Resume 按钮能把 paused 拉回认证并再次暂停；账号池、
  服务器/缓存、设置页均正常渲染。注入前的 stock 镜像备份在
  `/opt/openwrt/qemu/openwrt-x86-64.img.bak`。
- 注意：x86 测试配置里有一个 dummy 账号，仅存在于 QEMU 测试镜像；
  R3G 镜像不包含任何账号，`device=wan`。
- 快速迭代方式：
  1. 用 x86_64 Image Builder 重新构建带 `files/` 的 x86 镜像；
  2. 给 QEMU 加 SSH hostfwd（例如 `12222->22`），把 `files/` 直接
     SCP 进运行中的 guest，重启 uhttpd/procd；
  3. 停 QEMU，`losetup -Pf` 挂载镜像 `p2`，复制 CLI/LuCI 文件后重启
     （本次使用）。
- 从 Windows 浏览器访问：
  - VM 的 `28080` 已经是 socat 到 QEMU `18080`，可以直接开
    `http://<vm-ip>:28080`；
  - 或在本机开 SSH 本地转发：
    ```sh
    ssh -N -L 127.0.0.1:28080:127.0.0.1:28080 root@<vm>
    ```
    然后开 `http://127.0.0.1:28080`。
- 默认 root 空密码只适用于这个本地 QEMU 测试，不要暴露到不可信网络。

## 5. MIPS32 测试

- `qemu-mipsel` 用户态已验证静态 Go 二进制和 GB18030。
- 可选：OpenWrt `malta/le` 镜像 + `qemu-system-mipsel -M malta`，
  用于验证 MIPS32 用户态下的 procd/UCI/init。它不是 R3G，也不覆盖
  MT7530/DSA，只是比 qemu-user 更完整的 MIPS 运行环境。
- R3G 专属的 `wan` AF_PACKET、DSA、DHCP 时序只能上真机验证。

## 6. EAPOL 传输模拟（可选）

技术上可以做，但优先级低于 Web/后端：

- QEMU guest 加第二块网卡，宿主侧用 `tap`/`veth`；
- 宿主用 Scapy 写一个假认证器：收 EAPOL-Start → 发
  EAP-Request/Identity → 收 Identity → 发 EAP-Request/MD5 → 收 MD5 →
  发 EAP-Success；
- 验证 daemon 的组帧、私有尾、状态机、重传/去重/超时；
- 不验证 MT7621 交换机驱动和真实校园网服务器。

等 Go supplicant 核心写好后，如果时间允许再做这个夹具；不做也不阻塞
LuCI/管理面/真机认证。

## 7. 常见坑

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| `Checking 'install'... failed` | Ubuntu 26.04 用 uutils install | `PATH=/opt/openwrt/gnu-bin:$PATH` |
| `ucode` 下载失败/校验错误 | 大文件/单包下载被重置 | 从镜像源补 `dl/$PKG`，按 index 校验 |
| x86 镜像 gunzip 截断 | downloads.openwrt.org 重置 | 用 USTC/TUNA/NJU 镜像，按官方 sha256 校验 |
| `Could not set up host forwarding rule` | 端口被占用（bettercap 8081） | 改用 18080/18443 |
| QEMU 启动慢 | 无 `/dev/kvm`，TCG | 等 30–60 秒，或换有 KVM 的宿主 |
| hostfwd TCP 连上但 HTTP 无响应 | QEMU 10.2.1 非 loopback hostfwd 问题 | 外部用 socat 转 `127.0.0.1:18080/18443` |
| R3G 镜像无法 QEMU 启动 | 没有 MT7621 模型 | 用 x86_64 测 Web，真机测 R3G |
| `go get x/text` 升级 toolchain | x/text 要求 go >= 1.26 | 正常；或固定旧版 x/text |

## 8. 下一步

1. Go 协议核心 + 569 字节 golden test。
2. LuCI 五页 + UCI/ACL + CLI JSON 契约。
3. 用 x86 QEMU 做 LuCI 快速迭代；需要时构建 x86 自定义镜像。
4. 生成 R3G 自定义镜像，真机到位后刷写并做 Phase 0。
5. 可选：malta/le MIPS QEMU；EAPOL tap/veth 假认证器。
