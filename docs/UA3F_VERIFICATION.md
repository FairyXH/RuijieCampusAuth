# UA3F 校园网防检测实机验证

日期：2026-11-21
设备：小米路由器 3G 原版（MT7621A / OpenWrt 24.10.0 / kernel 6.6.73）
固件：精简全功能 0.10.0（提交 `7f0043c`）
网络：真实校园网，WAN 为 `wan`，公网出口由 EAP-MD5 认证

结论：**UA3F 与 nftables 伪装对出站报文生效，UA3F 开启的功能均正常；
仅 `desync_inject` 因会破坏本校园网 TLS 握手而保持关闭。**

## 当前配置

```text
ruijie-auth.main.disguise = full
ua3f.main.l3_rewrite_ttl   = 1
ua3f.main.l3_rewrite_tcpts = 1
ua3f.main.l3_rewrite_tcpwin= 1
ua3f.main.l3_rewrite_ipid  = 0    # 由 nftables 在内核做
ua3f.main.desync_reorder   = 1    # 1500 字节 / 8 包，端口 80,443
ua3f.main.desync_inject    = 0
ua3f.main.desync_ports     = 80,443
```

nftables：

```text
table inet ruijie {
  chain post {
    type filter hook postrouting priority mangle; policy accept;
    tcp flags syn tcp option maxseg size set 1460
    ip ttl set 128
    ip id set 0
  }
}

table inet UA3F_HELPER {
  chain HOOK_TCP_SYN {
    ...
    tcp flags syn counter packets 76 bytes 3952 queue flags bypass to 10301
  }
}
```

## 报文级证明

临时安装 `tcpdump`、把全局 sysctl 临时改回内核默认（TTL 64、
tcp_timestamps=1），然后抓路由器发往公网的同一个 SYN：

| 状态 | IP TTL | IP ID | 初始窗口 | TCP 选项 |
|---|---:|---:|---:|---|
| 全部关闭 | 64 | 6938 | 64240 | `mss 1460,sackOK,TS val ...` |
| 仅 nftables | 128 | 0 | 64240 | `mss 1460,sackOK,TS val ...` |
| nftables + UA3F | 128 | 0 | 65535 | `mss 1460,sackOK,wscale 5,eol`（无 TS） |

原始抓包（节选）：

```text
# 全部关闭
IP (ttl 64, id 6938, ...)
  ... Flags [S], win 64240,
  options [mss 1460,sackOK,TS val 1080533319 ecr 0,nop,wscale 5]

# 仅 nftables
IP (ttl 128, id 0, ...)
  ... Flags [S], win 64240,
  options [mss 1460,sackOK,TS val 861751771 ecr 0,nop,wscale 5]

# nftables + UA3F
IP (ttl 128, id 0, ...)
  ... Flags [S], cksum 0x1d36 (correct), win 65535,
  options [mss 1460,sackOK,wscale 5,eol]
```

逐项对应：

- TTL 64 → 128：nftables `ip ttl set 128` 生效。
- IP ID 6938 → 0：nftables `ip id set 0` 生效，不再由 UA3F 全量排队。
- TCP timestamp 存在 → 消失：UA3F `Delete TCP Timestamp` 生效。
- 初始窗口 64240 → 65535：UA3F `Set TCP Initial Window` 生效。
- 校验和从 offload 的 `incorrect` 变为 `correct`：UA3F 重算并发出报文。
- MSS 始终为 1460：nftables `maxseg size set 1460`。

测试完成后已恢复 `ip_default_ttl=128`、`tcp_timestamps=0`。

## desync reorder 证明

同一个 HTTP GET（`http://connect.rom.miui.com/generate_204`），抓
WAN 出站 PSH 数据段：

关闭 reorder：

```text
Flags [P.], seq 88900033:88900089, length 56
GET /generate_204 HTTP/1.1
```

开启 reorder：

```text
Flags [P.], seq ...:..., length 28
Flags [P.], seq 42:57,     length 15
Flags [P.], seq ...:57,    length 85
GET /generate_204 HTTP/1.1
```

单个 56 字节请求被拆成 28/15/85 三段并乱序发送，证明
`Desync.Reorder=true`、`reorder_bytes=1500`、`reorder_packets=8` 已生效。
UA3F 日志同时输出：

```text
"Desync.Reorder"=true "Desync.Reorder Bytes"=1500
"Desync.Reorder Packets"=8 Desync.Inject=false
msg="TCP Desync server started" reorder_bytes=1500 reorder_packets=8
```

## 功能与资源

- `wget https://www.baidu.com`：成功。开启 reorder 后 HTTPS 正常。
- 校园网 EAP-MD5：已认证；`ruijie-auth probe --device wan`：在线。
- CPU：`97% idle`，UA3F 进程 `0% CPU`；只在新连接阶段处理。
- NFQUEUE 规则只匹配 `tcp flags syn`，未把全部 TCP 包送入用户态。
- `desync_inject` 实测 TTL 1/2/3、端口 80/443 均导致 TLS 握手 EOF，
  因此默认关闭；这是本校园网兼容性取舍，不是资源问题。

## 复现

```sh
# 临时装抓包工具（本固件默认不预装）
opkg install tcpdump

# 抓一个出站 SYN
tcpdump -i wan -c 1 -nn -vvv \
  'src host <wan-ip> and tcp[tcpflags] & tcp-syn != 0' &
wget -T 2 -O /dev/null http://223.5.5.5/

# 查看 nft 规则与计数
nft list table inet ruijie
nft list ruleset | grep -A4 HOOK_TCP_SYN

# 对比 reorder
uci set ua3f.main.desync_reorder=0; uci commit ua3f
/etc/init.d/ua3f restart
# 抓 HTTP GET 数据段
uci set ua3f.main.desync_reorder=1; uci commit ua3f
/etc/init.d/ua3f restart
```

验证完成后 `tcpdump`/`libpcap1` 已卸载，overlay 占用回到约 1.6MB。
