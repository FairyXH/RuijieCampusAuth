# 服务器私有属性（Server Private Properties）

状态：P2 已实现（解析器 + 调试页 + 服务重认证定时）。
依据：`8021x.exe` 反汇编 `CRGPrivateProc::ReadRGVendorSeg`（0x51a890）与
`fcn.00537ee0`。

## 1. 位置

- 服务器在 **EAP-Success** 中携带私有属性块。
- 调用方传入的指针是 EAP 数据区（EAP 头之后），长度 = EAP 数据长度。
- 长度上限 `0x578`（1400 字节）。
- C# 从 `eap[4..]` 开始解析（EAP 头 4 字节之后）。

## 2. TLV 结构

```
+------+------+----------------+
| type | len  | value (len)    |
| 1B   | 1B   | 0..254 bytes   |
+------+------+----------------+
```

规则（与原版 `ReadRGVendorSeg` 一致）：

- `type == 0` 非法，终止解析。
- `len > 0xFE` 非法。
- `offset + len > total` 非法。
- 循环直到不足 2 字节。
- 未知 type 跳过 value，不中断（原版打印“出现无法识别的属性，跳过”）。

## 3. RC4 范围

整块 TLV 是**明文**。只有以下两个字段的 8 字节 value 用 RC4 加密：

| type | 名称 | 处理 |
|---|---|---|
| `0x5C` | `ENCRYPT_KEY` | `RC4(key="com.ruijie.www", 8 bytes)` |
| `0x5D` | `ENCRYPT_IV` | `RC4(key="com.ruijie.www", 8 bytes)` |

证据：`fcn.00537ee0` case 37/38 调用 `LIBEAY32.dll_Ordinal_479`
（OpenSSL `RC4_set_key`，key 长度 14）后再做 8 字节变换。

## 4. 类型表

| type | 十进制 | 名称 | 解码 |
|---|---|---|---|
| `0x05` | 5 | `SU_NEWEST_VER` | 4B 大端 |
| `0x14` | 20 | `PROXY_AVOID` | 1B/数值 |
| `0x15` | 21 | `DIALUP_AVOID` | 1B/数值 |
| `0x37` | 55 | `SU_UPGRADE_URL` | GB18030 字符串 |
| `0x3C` | 60 | `ACCOUNT_INFO` | 字符串 |
| `0x3D` | 61 | `PERSIONAL_INFO`（原版拼写） | 字符串 |
| `0x42` | 66 | `SERVER_LIST_UPDATE` | 字符串（“更新服务列表”） |
| `0x52` | 82 | `BROADCASE_INFO` | 字符串 |
| `0x53` | 83 | `REJECT_REASON` | 字符串 |
| `0x56` | 86 | `SU_REAUTH_INTERV` | 4B 大端，秒 |
| `0x59` | 89 | `HELLO_INTERV` | 4B 大端 |
| `0x5A` | 90 | `INDICATE_PORT` | 4B 大端 |
| `0x5B` | 91 | `INDICATE_SERV_IP` | 4B 大端，`ntohl` |
| `0x5C` | 92 | `ENCRYPT_KEY` | 8B，RC4 |
| `0x5D` | 93 | `ENCRYPT_IV` | 8B，RC4 |
| `0x5E` | 94 | `SERVER_UTC_TIME` | 4B 大端 |
| `0x61` | 97 | `RADIUS_SERV_TYPE` | 数值 |
| `0x66` | 102 | `SERVER_LIST_UPDATE_LEGACY` | 字符串/列表 |
| `0x68` | 104 | `USER_LOGIN_URL` | 字符串 |
| `0x6E` | 110 | `MSG_CLIENT_PORT` | 4B 大端 |
| `0x72` | 114 | `UTRUST_URL` | 字符串 |
| `0x73` | 115 | `SMP_IPADDRESS` | 4B 大端 |
| `0x74` | 116 | `PROXY_DECTECT_KINDS` | 数值/位图 |
| `0x75` | 117 | `CLIENT_MSG_PERIOD` | 4B 大端 |
| `0x77` | 119 | `AUTO_OPEN_SSO_URL` | 1B 开关 |
| `0x79` | 121 | `DIRECT_COMM_HIGHEST_VERSION` | 1B |
| `0x80` | 128 | `DIRECT_COMM_HEARTBEAT_FLAGS` | 4B |
| `0x98` | 152 | `WIFI_CNNT_NUM_FLAGS` | 4B |

## 5. 实现

- `Core/Protocols/Rc4.cs` — 标准 RC4。
- `Core/Protocols/PrivateProperties.cs` — TLV 解析、字段解码、RC4 字段。
- `Core/Debug/PrivatePropertiesDebugFeature.cs` — 调试页输入 hex，输出字段表。
- `Core/Supplicant.cs` — EAP-Success 时解析并暴露
  `LastPrivateProperties` / `ServerReauthIntervalSeconds`。
- `Service/AuthWorker.cs` — 读取 `SU_REAUTH_INTERV`，到点后重新认证；
  `use_server_reauth=false` 可关闭。
- 测试：`Core.Tests/PrivatePropertiesTests.cs`（RC4 标准向量、TLV、越界）。

## 6. 已知未确认

1. `SU_REAUTH_INTERV` 单位按秒处理；需抓包对照原版实际重认证间隔。
2. `SERVER_LIST_UPDATE` 的 value 是 URL 还是列表，需真实样本。
3. `PROXY_DECTECT_KINDS` 位图含义。
4. EAP-Success 中私有块是否始终从 `eap[4]` 开始；当前解析失败时会记录告警但不影响认证。
