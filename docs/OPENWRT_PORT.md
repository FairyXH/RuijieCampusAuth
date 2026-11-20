# C# → OpenWrt 移植对照

日期：2026-11-20
状态：算法层与认证核心已移植并测试；UI 用 LuCI 重写。

## 1. 移植原则

- 算法函数**同输入、同输出、同调用顺序**。Go 侧保留 C# 的函数边界与名字
  （`BuildTail`、`ParsePrivateProperties`、`RC4Transform`、`GenerateNTResponse`
  等），便于逐函数对照与复用测试向量。
- 平台差异只出现在传输层：C# 用 Npcap，Go 用 AF_PACKET；TLS 用各语言
  标准库（`SslStream` / `crypto/tls`），隧道分片逻辑自己实现，保持线上
  字节一致。
- 配置模型不照搬 `settings.json`，而是落到 UCI；导入语义与
  `ProfileStore.Import` 一致。

## 2. 文件对照

| C# | Go | 说明 |
|---|---|---|
| `Core/PrivateTail.cs` | `internal/protocol/privatetail.go` | 私有尾、CRC16、混淆、Pad16、凭据加密、MAC 规范化 |
| `Core/Eapol.cs` | `internal/protocol/eapol.go` | 组帧、Identity/MD5 响应、解析、GBK 消息提取 |
| `Core/Protocols/PrivateProperties.cs` | `internal/protocol/privateproperties.go` | 分段解析、TLV 循环、类型表、RC4 字段 |
| `Core/Protocols/Rc4.cs` | `internal/protocol/rc4.go` | 同 key schedule |
| `Core/Protocols/MschapV2.cs`（含 `Md4`） | `internal/protocol/mschapv2.go`、`md4.go` | RFC 1320/2759 |
| `Core/Protocols/EapTlsStream.cs` | `internal/protocol/eaptls.go` | net.Conn 桥接，单向通道 |
| `Core/Protocols/PeapSession.cs` | `internal/protocol/peap.go` | Start→分片→TLS→phase2→外层结果 |
| `Core/Protocols/PcapEapTransport.cs` | `internal/supplicant/supplicant.go`（`tunnelTransport`） | Identity 往返、Exchange、标识符替换 |
| `Core/Protocols/AuthMethod.cs` | `internal/protocol/authmethod.go` | `auth_mode` 解析与显示 |
| `Core/Supplicant.cs` | `internal/supplicant/supplicant.go` | EAP-MD5 状态机、私有属性捕获、重认证 |
| `Core/SchoolProfile.cs` + `ProfileStore` | `internal/profile/profile.go` | JSON 模型、UCI 导入、备份 |
| `Core/UserStore.cs`（settings 部分） | `internal/config/config.go` | UCI 解析 |
| `Core/Supplicant.cs` 网卡选择 | `internal/netcfg/interfaces.go` | WAN 自动识别与候选排序 |
| `Core/Protocols/DhcpControl.cs` | — | Windows API 专用，未移植 |
| `Core/Protocols/PortalClient.cs` 等 | — | 非 802.1X 认证核心，未移植 |
| GUI `Views/*.axaml` + ViewModels | `luci-app-ruijie-auth/.../view/ruijie-auth/*.js` | 页面重写，功能对齐 |

## 3. 行为对齐点

- 私有尾默认空服务名/空静态 ID，由档案注入；`TailOptions` 的随机 nonce
  （16 字节）、fingerprint（64 字节）、`8021x.exe`、版本 `06 56 00 00`、
  flag `01` 与 C# 默认一致。
- Control 尾：session nonce + 128 字节零 fingerprint；Identity 尾用
  session nonce；MD5 尾用 challenge 作 IV；PEAP Identity 尾用随机 nonce。
  `private_tail=0` 时全部不发。EAP-MD5 与 PEAP/EAP-TLS 两条路径的
  EAPOL-Start/Logoff 都使用 Control 尾；真实抓包已验证该组合。
- EAPOL-Start 目标 `01:d0:f8:00:00:03`，Logoff 目标
  `01:80:c2:00:00:03`；`logoff_first` 控制首包。
- 私有属性：段 2 的 4 字节按有符号 int 读取、段 5 interval 按 32 位乘法
  后再扩展（与 C# 的溢出行为一致）；字符串类型表沿用 C# 的
  `PERSIONAL_INFO`、`BROADCASE_INFO`、`PROXY_DECTECT_KINDS` 拼写。
- `SU_REAUTH_INTERV > 0` 时覆盖本地 `reauth_interval`；EAP-MD5 与 tunnel
  两条路径都生效。
- PEAP 每次隧道响应都附加 Identity 尾（与 C# `Exchange(eap, IdentityTail)`
  一致）；分片首片带长度头，`More` 位与 ACK 语义一致。
- MSCHAPv2：用户名按 UTF-8 参与 ChallengeHash，密码按 UTF-16LE 做 MD4。

## 4. 有意偏离

| 项 | 说明 |
|---|---|
| `HeaderFlag` 零值 | Go 用 0 表示未设置并回落 1；所有调用方显式传 1，线上无差异 |
| 错误返回 | C# 抛异常，Go 返回 `error`；调用链与失败条件一致 |
| 状态输出 | C# 写 `status.json` + 日志；Go 额外把私有属性写进 status 供 LuCI 展示 |
| 重认证循环 | C# 由 Service/AuthWorker 外层驱动；Go 在 session 内驻留（行为等价） |
| PEAP 控制帧尾 | C# `PcapEapTransport` 把 identity 尾复用于 Start/Logoff；Go 用 Control 尾（会话 nonce + 零 fingerprint），与真实抓包及原版控制帧语义一致 |
| PEAP vendor segment | C#/Python 也只实现了协议辅助函数，`PeapSession` 活动路径不发送 0x5F 等段；Go 保持同样边界，待真实 RADIUS 抓包后再接入 |
| DH 续租 | `dhcp_mode` 仅记录，不驱动 udhcpc |

## 5. 验证

| 项 | 证据 |
|---|---|
| 私有尾 569 字节 | `selftest`（amd64 + mipsle/qemu）通过 |
| 私有尾三类语义 | `capture_vector_test.go`：真实抓包的 Start / Identity / MD5 Response 三份尾包均 569/569 逐字节一致（含 Control 尾全零指纹） |
| 真实抓包私有属性块 | `internal/protocol/vectors_test.go`：10 个属性、hash `0x19594C7A`、interval 20000、ENCRYPT_KEY/IV 与 C# 断言一致 |
| RC4 | 标准向量 `bbf316e8d940af0ad3` |
| MD4 | 空串/`abc` 向量 |
| MSCHAPv2 | RFC 2759 NT-Response `82309ecd...d6df`、ChallengeHash `d02e4386bce91226` |
| PEAP 分片 | 首片 `0xC0`、中间 `0x40`、末片 `0x00`，长度头 500、重组一致 |
| UCI 导入 | 保留账号与未知选项、写 backup、忽略空字段、单引号转义 |
| 中文 .lmo | 与 24.10 镜像的 `https-dns-proxy.zh-cn.lmo` 154/154 键值/偏移一致；本应用 187 条 PO 编译为 182 条 LMO（5 条与 msgid 相同的协议名省略），JS 侧 0 缺失 |
| QEMU 运行 | x86_64 OpenWrt：selftest、interfaces、EAPOL 循环、profile import、LuCI 页面均通过；运行时目录按 hash 校验 8 个菜单标题与 8 个页面全部中文命中 |

未验证：真实 RADIUS 上的 PEAP/EAP-TLS 端到端；真实校园网 WAN 上的
EAP-MD5 上线（需现场账号）。PEAP vendor segment（0x5F 凭据段等）尚未
接入活动会话；EAP-TLS 需要客户端证书，OpenWrt 版未开放证书配置入口。

## 6. 后续

1. 现场用 `auth_mode=EAPMD5` 跑通一次，核对 `status --json` 的
   properties 与桌面版日志。
2. 若学校用 PEAP，抓一次 tunnel 交互确认 phase2 分支。
3. 需要 SAM/Portal 时再补，端点与字段参考 `docs/protocols/`。
