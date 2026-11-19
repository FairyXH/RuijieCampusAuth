# 服务器私有属性（Server Private Properties）

状态：P2 已实现；2026-11-19 全量校对中**重写为原版分段格式**，与原版二进制
逐字段一致（含真实抓包锁定向量）。
依据：`8021x.exe` `ParsePrivateProperty`（0x537ee0）反编译 + Unicorn 实测 +
本机 HXXY 服务器真实抓包（`build/capture/live-auth.pcap`）。

## 1. 位置

- 服务器在 **EAP-Success / EAP-Failure** 中携带私有属性块。
- 块起点 = 帧偏移 0x16（= EAP 头之后，即 `eap[4..]`）。
- C# 从 `eap.AsSpan(4)` 开始解析，与原版 `ParsePrivateProperty(arg+0x16,
  eapol_len-4)` 对齐。

## 2. 分段结构（原版实际格式，非平铺 TLV）

```
[00 00 13 11][2B len][GBK 消息]                          段 1：欢迎/提示消息
[00 00 13 11][2B skip][4B →+0x618][100B 零][1B count]    段 2：固定 0x6F 布局
[00 00 13 11][01][00]                                    段 3：版本对 (1,0)
[00 00 13 11][01][01]                                    段 4：版本对 (1,1)
[15 字节混淆块]                                          段 5：解混淆后
    [00 00 13 11][0x0A 门控][1B][4B hash][4B 秒][1B]
[00 00 13 11][type][len=value+2][value] × N              属性 TLV 循环
```

- 段 5 的 15 字节用与私有尾相同的逐字节位反转+取反变换原位解混淆
  （`FUN_0053df40` ≡ `FUN_0053de90`，已实测：`ffff3777afff6765cda1ffffffd7ff`
  → `000013110a0019594c7a0000001400`）。
- 门控字节 = 0x0A 时才进入属性循环。
- 属性 TLV 的 len **包含 type+len 两字节**（value = len - 2），每个 TLV
  前面都有 `00 00 13 11` 标记。

## 3. RC4 范围

只有以下两个字段的 8 字节 value 用 RC4 加密（OpenSSL 序号导入 479/477）：

| type | 名称 | 处理 |
|---|---|---|
| `0x5C` | `ENCRYPT_KEY` | `RC4(key="com.ruijie.www", 8 bytes)` |
| `0x5D` | `ENCRYPT_IV` | `RC4(key="com.ruijie.www", 8 bytes)` |

**实测（HXXY 真实服务器）**：`ENCRYPT_KEY = 7C73533126403871`、
`ENCRYPT_IV = 4163332331215164` —— 与 dircomm.log 中的 SAM DES key/IV
完全一致（SAM 直通复用这两个值）。

## 4. 类型表（含原版长度约束）

| type | 名称 | 原版要求 | 解码 |
|---|---|---|---|
| `0x05` | SU_NEWEST_VER | len==4 | 4B 大端 |
| `0x14` | PROXY_AVOID | len==4 | 4B 大端 |
| `0x15` | DIALUP_AVOID | len==4 | 4B 大端 |
| `0x37` | SU_UPGRADE_URL | **len==0xF7 恰好** | GB18030 字符串 |
| `0x3C` | ACCOUNT_INFO | 任意 | 字符串（原版追加到消息缓冲） |
| `0x3D` | PERSIONAL_INFO | 任意 | 字符串 |
| `0x42` | BROADCASE_INFO | 任意 | 字符串 |
| `0x53` | REJECT_REASON | 任意 | 字符串 |
| `0x56` | SU_REAUTH_INTERV | len==4 | 4B 大端秒 |
| `0x59` | HELLO_INTERV | len==4 | 4B 大端 |
| `0x5A` | INDICATE_PORT | len==4 | 4B 大端 |
| `0x5B` | INDICATE_SERV_IP | len==4 | 4B 大端 |
| `0x5C` | ENCRYPT_KEY | len==8 | RC4 |
| `0x5D` | ENCRYPT_IV | len==8 | RC4 |
| `0x5E` | SERVER_UTC_TIME | **len==8** | 8B 原样 |
| `0x61` | RADIUS_SERV_TYPE | len==1 | 1B |
| `0x65` | SvrSwitchResult | 任意 | 字符串 |
| `0x66` | SERVER_LIST_UPDATE_LEGACY | 任意 | 字符串（0x40 分隔） |
| `0x68` | USER_LOGIN_URL | 任意 | 字符串 |
| `0x6E` | MSG_CLIENT_PORT | len==4 | 4B 大端 |
| `0x72` | UTRUST_URL | 任意 | 字符串 |
| `0x73` | SMP_IPADDRESS | len==4 | 4B 大端 |
| `0x74` | PROXY_DECTECT_KINDS | len==4 | 4B 大端 |
| `0x75` | CLIENT_MSG_PERIOD | **len==2** | 2B 大端 |
| `0x77` | AUTO_OPEN_SSO_URL | len==1 | 1B 开关 |
| `0x79` | DIRECT_COMM_HIGHEST_VERSION | len==1 | 1B |
| `0x80` | DIRECT_COMM_HEARTBEAT_FLAGS | len==4 | 4B 大端 |
| `0x98` | WIFI_CNNT_NUM_FLAGS | len==4 | 4B 大端 |

长度不符时原版**停止解析**（不是跳过）。默认 type 跳过 value 继续。

## 5. 实测值（HXXY 真实服务器，2026-11-19 抓包）

| 字段 | 值 |
|---|---|
| 段 1 消息 | `欢迎使用厦门华厦学院网络`（GBK） |
| 段 5 hash | `0x19594C7A` |
| 段 5 interval | 20 × 1000 = 20000ms |
| SU_REAUTH_INTERV | 0（服务器不驱动重认证） |
| INDICATE_PORT | 0x35 = 53 |
| INDICATE_SERV_IP | 0xAC160B01 = 172.22.11.1 |
| ENCRYPT_KEY | 7C73533126403871 |
| ENCRYPT_IV | 4163332331215164 |
| SERVER_UTC_TIME | 00 00 00 00 6A FE F0 17（8B） |
| MSG_CLIENT_PORT | 0x8A = 138 |
| DIRECT_COMM_HIGHEST_VERSION | 2 |
| DIRECT_COMM_HEARTBEAT_FLAGS | 0 |
| WIFI_CNNT_NUM_FLAGS | 01 00 01 00 |

MD5-Challenge 包尾部还携带 `[1311][0x2E][3][01]` 与
`[1311][0x66][0x0E]["电信宽带服务"]`，由 `ReadRGVendorSeg`（0x51a890，
平铺 TLV）解析，但受全局 `+0x33D4` 门控（默认关闭）——C# 不解析挑战包
尾部，与原版默认行为一致。

## 6. 实现

- `Core/Protocols/PrivateProperties.cs` — 分段解析器（重写）。
- `Core/Supplicant.cs` — EAP-Success/Failure 时解析，`Messages` 驱动
  服务器消息显示，`SU_REAUTH_INTERV` 驱动重认证。
- 测试：`Core.Tests/PrivatePropertiesTests.cs` — 真实抓包 554 字节
  锁定向量（与原版二进制输出逐字段一致）+ 合成分段向量 + 边界。

## 7. 已知未确认

1. 段 2 的 4B 值（+0x618）与段 5 hash 的语义（疑似服务器版本/会话哈希）。
2. `0x66` 服务列表在原版受三重条件门控，C# 直接当字符串记录。
3. `password-change` / `config-update` 端点无证据（维持未实现）。
