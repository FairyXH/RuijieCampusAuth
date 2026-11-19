# 原版协议档案

每个协议一份文档，必须包含：

- 抓包/反编译证据（文件、偏移、地址、日志行）
- 报文格式与字段偏移
- 调用链（原版类::方法顺序）
- 常量、密钥、超时、重试
- golden 样本（hex 或 pcap 路径）
- C# 实现位置与测试位置

## 状态

| 模块 | 文档 | 状态 |
|---|---|---|
| EAPOL / EAP-MD5 / 私有尾包 | `eap-md5.md`（待补） | 已有实现 |
| 服务器私有属性 TLV / RC4 | `private-properties.md` | P2 已实现 |
| DHCP + Option 82 | `dhcp.md` | P3 已实现 |
| ARP / 代理 / 拨号检测 | `diagnostics.md` | P4 已实现 |
| Portal eportal / Portal UDP | `portal.md` | P5 已实现 |
| 客户端中心 / 自助服务 | `portal.md` | P5 已实现 |
| PEAP / EAP-TLS | `peap.md` | P6 已实现，待实验服务器验证 |
| 无线 WPA2-Enterprise | `wireless.md` | P7 |
| SAM 直通 / 升级 | `sam-upgrade.md` | P8 |

## 规则

1. 先记录证据，再写实现；实现不得改变字段顺序、常量、重试与发包节奏。
2. 每个协议至少一个 golden 样本；改动协议代码必须跑对应测试。
3. 未确认的行为标记 `unknown`，不得用猜测补齐。
