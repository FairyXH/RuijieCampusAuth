# SAM 直通心跳（DirComm Heartbeat）逆向档案

> 2026-11-23 从官方 `8021x.exe`（引擎 5.0.2014.108，与仓库逆向基准同一
> 二进制，`C:\Program Files\Ruijie Networks\Ruijie Supplicant\8021x.exe`）
> 静态逆向恢复。工具：本地 capstone 注解反汇编（会话脚本）+ 字符串/
> 立即数交叉引用扫描。Ghidra(REA) 与 Unicorn 全函数模拟两条路均受
> 阻（见文末"方法学记录"），字节格式由静态读码完整恢复。

## 1. 这是什么

官方客户端认证成功后，除 802.1X 会话外，还会与 SAM 服务器维持一条
**UDP 直通（DirComm）通道**：

- 客户端绑定本地 `MSG_CLIENT_PORT`（属性 0x6E，实测 138）；
- 目的为 `INDICATE_SERV_IP : INDICATE_PORT`（属性 0x5B/0x5A，实测
  SAM IP、端口 53）；
- 加密材料复用认证私有属性下发的 `ENCRYPT_KEY/ENCRYPT_IV`
  （0x5C/0x5D，8 字节 DES key/IV）；
- 心跳间隔由 `HELLO_INTERV`（0x59）驱动（内部参数名 `dwTimerToSAM`）；
- **心跳超时（全局重试计数 >3）→ 客户端主动重新认证**
  （日志"直通心跳超时重认证"）。SAM 亦可经此通道推强制下线
  （`ONSAMWantLogOff` → "SAM要求重认证，开始StartStateMachine"）。

即：这是官方客户端的会话保活 + 服务器反向控制通道；不发心跳不影响
本校会话存活（实测无心跳稳定持有数小时），但官方客户端靠它在丢包/
被踢时自愈。

## 2. 报文格式（已恢复）

### 2.1 明文 TLV 体（发送前构建）

心跳构建函数 `sub_4a2a30`（thiscall，this=CDirTranThread）按
`[type:1][len:1][value:len]` 追加：

| type | len | value | 来源/含义 |
|---|---|---|---|
| 0x01 | 1 | `02` | 直通协议版本 2 |
| 0x03 | N | GBK 字符串 | **服务名**（`m_adapterInfo.cServiceName`，ctx+0x1488 → 线程对象 +0x74；空则 InitDirThread 直接失败） |
| 0x04 | 4 | dword | 适配器信息（线程对象 +0xf4；对应 dircomm.log 的 "SU IP"，即本机 IPv4） |
| 0x05 | 6 | 字节串 | **本机 MAC**（线程对象 +0xf8） |

### 2.2 发送端封装（`sub_4a6e30`，sendMessage）

1. 按套接字查协议参数表（key/IV；查不到 → "sendMessage>找不到相应的
   协议参数"）。
2. 序列号 = `para+0x14cc`，自增；`htonl(seq)` 得 4 字节。
3. 组 20 字节选择头：`seq_be(4) + 16 字节运行时槽(0x755818，默认全零)`。
4. 混淆块选择：`idx = (Σ(header[0..20]) × 0x2d7) mod 53`，取
   `0x7343c0 + idx×128` 处 128 字节表项（表已提取：
   `build/capture/dircomm-obfuscation-table.bin`，53×128=6784 字节）。
5. 追加 TLV：`type=0x14, len=0x80, value=表项`。
6. 整个 TLV 体补零到 8 字节倍数（DES 块对齐）。
7. `sub_4a9380`（CSuDES，key/IV=认证下发）加密，进发送队列
   （临界区 + SetEvent 唤醒发送线程）。
8. 重传 2 次 / 超时 2000ms（`para+0x12c/+0x128`）。

### 2.3 关键函数地址（ImageBase 0x400000）

| VA | 角色 |
|---|---|
| 0x4a2a30 | 心跳报文构建（内含"发SAM心跳报文"日志 0x6c0244） |
| 0x4a2e10 | TLV 追加到流 |
| 0x4a2ea0 | 字符串 TLV 构建 |
| 0x4a6e30 | sendMessage：封装/加密/入队 |
| 0x4a9380 | CSuDES 加密（未逐字节还原） |
| 0x4a3e80 | InitDirThread（创建 CDirTranThread 0x14d0 字节） |
| 0x4a5c20 | CDirTranThread::Init（"\device\" 网卡名、事件、服务名装配） |
| 0x4a40ac | OnInit_SAM（参数 dump：版本/重传/超时/DES key/IV/UTC/端口/IP） |
| 0x47c352 / 0x47d546 | 心跳调用点（`ctx->[0x1b24]` 为线程对象指针；全局重试计数 0x7449c0，>3 触发重认证） |
| 0x6c0244..0x6c0480 | DirComm 日志字符串区（GBK） |
| 0x7343c0 | 53×128 混淆表（静态数据） |
| 0x755818 | 16 字节选择头魔数槽（.data，默认全零，运行时填充，来源未确认） |

## 3. 待验证项

1. 20 字节选择头是否随密文上无线（`sub_4a9380` 内部未完全还原：
   DES 模式（CBC/ECB）、填充、以及 seq 的最终位置）。
2. TLV4=本机 IP 为强推断（字段序 SU IP/MAC 与 dircomm.log 参数一致），
   未做运行时字节级确认。
3. 0x755818 魔数槽的运行时填充来源。
4. SAM 回应报文/强制下线报文的完整解析（`强制下线解析接口`）。

## 4. 实现路线（ruijie-auth 补心跳）

1. 认证成功后开 UDP socket，绑 `MSG_CLIENT_PORT`（138）；
2. 每 `HELLO_INTERV`（服务器未下发则缺省，官方默认值未从二进制确认）
   构建并发送：TLV(01,版本=02)+TLV(03,服务名)+TLV(04,本机IP)+
   TLV(05,MAC)+TLV(14,表项)，8 字节对齐后 DES 加密，目的
   `INDICATE_SERV_IP:INDICATE_PORT`；
3. 维护 4 字节自增序列号（htonl）参与表项选择；
4. 监听本机 138 端口接收 SAM 推送（强制下线→重认证，保持现有行为）；
5. 心跳重试计数 >3 → 主动重认证（对齐官方行为，替代纯探测判定）。

## 5. 实现状态（2026-11-23）

两端已按本档案落地（提交 f244af1 / 73a7342 / 1a13bd1）：

- **OpenWrt（Go）**：`internal/dircomm` 包（TLV 构建、53×128 表嵌入、
  DES-CBC、Monitor 循环）；supplicant 常驻会话在 EAP 成功且服务器下发
  key/IV/IP/端口后启动，`HELLO_INTERV` 覆盖缺省间隔，连续 3 次无响应
  触发重认证。UCI：`ruijie-auth.main.heartbeat='1'`（默认关）、
  `heartbeat_interval`（秒，缺省 30）。测试：9 项含 DES 实网向量；
  `go test ./...` 全绿，mipsle 交叉编译通过。
- **Windows C#**：`Core/Protocols/DirCommHeartbeat.cs`（同一格式，
  表作为嵌入资源）+ `DirCommHeartbeatMonitor`；AuthWorker 在 Hold
  阶段启动，设置 `heartbeat: true`（settings.json，默认关）、
  `heartbeat_interval`。测试 8 项；Core.Tests 73/73。
- 待实网验证：SAM 对我们构造的心跳是否回应/是否改变会话行为；
  下行推送报文解析（当前仅记录字节数）。

## 6. 方法学记录

- REA/Ghidra（12.1.4）打开该 PE 正常，但 MCP 客户端 30s 超时取消
  会终止临时库并重新导入，默认分析（3.4MB PE）永远跑不完——**该
  会话内 Ghidra 路线不可用**，改用本地 capstone 注解反汇编 +
   立即数交叉引用（字符串 VA → `push imm32` 扫描）定位代码。
- Unicorn（仓库 tools/re_diff PeCall）全函数模拟受阻：CRT 的
  FLS/TLS 回退链在桩化环境下无限递归（TlsSetValue 桩不落盘导致
  ptd 反复重分配），栈打穿。如需字节级验证，需给 TlsGetValue/
  TlsSetValue/Fls* 实现真实的每线程槽存储。
- 字符串引用定位法：PE 节表 RAW==RVA 直映射，`struct.pack('<I', va)`
  在 .text 内搜索即得 `push 字符串地址` 的代码点。
