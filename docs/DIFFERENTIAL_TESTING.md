# 原版函数直接调用与差分测试

状态：核心算法已完成逐函数差分，未发现不一致。
工具：`tools/re_diff/pe_call.py`（Unicorn 32 位 PE 调用器）。

## 1. 能否直接调用

`8021x.exe` **没有任何导出**（`iE` 为空），所以不能 `LoadLibrary + GetProcAddress`。

可行方式：

1. **Unicorn 映射整个 PE 镜像**，按 RVA 直接调用内部函数（本项目采用）。
2. Frida 注入运行中的进程后按地址调用（需要能启动原版进程；接管模式会阻止）。
3. 自写 32 位 loader 映射镜像（需处理导入/CRT，复杂度更高）。

Unicorn 方式不需要执行原版入口，不会触发 UI/网络/接管逻辑，适合纯算法函数。

## 2. 调用器

`tools/re_diff/pe_call.py`：

- `pefile` 读取 PE，映射 `SizeOfImage` 到 `ImageBase`。
- 映射栈、堆、导入桩区、返回哨兵；FS:[0] 用 0 页模拟，支持 MSVC SEH 序言。
- IAT 指向桩代码，桩实现 `memcpy/memset/memmove/strlen/strcpy/malloc/calloc/free`。
- `call(rva, args, ecx=None)`：按 cdecl 布置 `[esp]=返回地址`、`[esp+4]=arg0`。
- 返回 `eax`、错误、执行指令数。

## 3. 已差分函数

| 函数 | 地址 | 调用方式 | 验证 |
|---|---|---|---|
| CRC16/XMODEM | `0x53dc20` | `crc(ptr,len)` | 5 组输入一致 |
| 字节混淆 | `0x53de90` | `obf(ptr,len)` 原地 | 1/2/7/23/64/255 字节一致 |
| 私有头构建 | `0x53dc80` | `header(dest,this)` | 零 IP、真实 IP 均一致 |
| 头 + 混淆 | `0x53cbb0` | `header(dest,this)` | 23 字节完整一致 |
| MD5 | `0x53cb50` | `md5(data,len,out16)` | 空/abc/hello/64B 一致 |
| 凭据加密 | `0x53c970` | `crypt(key,16,user,plain,len,out)` | 16/32/48 字节、链式一致 |

字段偏移（私有头 `this`）：

| 偏移 | 含义 |
|---|---|
| `+0x648` | header flag |
| `+0x64c` | IPv4 |
| `+0x650` | netmask |
| `+0x654` | gateway |
| `+0x658` | DHCP |

## 4. 已发现但无法直接调用的函数

| 函数 | 原因 |
|---|---|
| `GetClientVersion` `0x518950` | 依赖 C++ 对象/std::string 全局状态，零对象调用失败 |
| `GetUserPasswd4Peap` `0x519c70` | 包装层依赖分配器/对象状态，调用中途跳到低地址；核心 `0x53c970` 已单独验证 |
| `GetMACAddr` `0x5195b0` | 通过设备/注册表查询真实网卡，输入不可控 |
| `GetServiceName` `0x519620` | 读取全局 `std::string` 对象，需要构造全局状态 |

## 5. 发现的 PEAP 段格式

`GetUserPasswd4Peap` 反编译：

- 段格式：`[0x5F][len][cipher]`
- 硬编码 IV：`!jierui9002pmsus`（16 字节，来自 `0x6c8320`）
- 加密复用 `0x53c970`：`cipher = plain XOR MD5(username || previous)`
- 已实现 `PeapVendorSegments.Credential`，并用原版固定 IV 输出锁定：
  - `user/pass` → `5f10b4b2fe1f0c17ba50d534831278f1a135`
  - `student01/16×x` → `5f1013b90055a7386ac235c8a73074dc03ba`

其他段：

| 类型 | 格式 | C# |
|---|---|---|
| `0x11` | `[0x11][0x24][36 bytes]` 客户端版本 | `ClientVersionSegment` |
| `0x2D` | `[0x2D][0x06][MAC]` | `Mac` |
| `0x2F` | `[0x2F][len][cipher]` 私有尾凭据 | `PrivateTail` |
| `0x39` | `[0x39][0x20/0x40][service]` | `ServiceName` |
| `0x5F` | `[0x5F][len][cipher]` PEAP 凭据 | `Credential` |
| `0x63` | `[0x63][0x01][phase]` | `DhcpPhase` |

## 6. 调用依赖顺序（EAP-MD5 私有尾）

反编译 `FUN_0053a590`：

```
FUN_0053cbb0()                      // 23 字节头（含 CRC + 位反转）
00 00 13 11                         // marker
"8021x.exe" padded 32               // client name
FUN_0053cc00() + 0x20
FUN_0053ccd0() + 4                  // client version
byte [this+0xa6c]                   // client flag
00 00 13 11                         // marker
2-byte attribute total length
attr 0x17 len 0x22 (32B nonce)
attr 0x18 len 0x06 (4B)
attr 0x2D len 0x08 (6B MAC)
attr 0x2F dynamic (credential cipher 0x53c970)
attr 0x35 ... 0x4D ...
```

与 C# `PrivateTail.BuildTail` 的前半段顺序一致：`0x17,0x18,0x2D,0x2F,0x35,0x36,0x38,0x4E,0x4D,0x39,0x54`。
尾部 `0x62/0x6B/0x7E/0x70/0x6F/0x79/0x76` 在函数中有条件分支，需构造完整对象做动态全量比对。

## 7. C# 锁定测试

`RuijieCampusAuth.Core.Tests/OriginalDifferentialTests.cs`：

- CRC16 4 组
- Obfuscate 2 组
- 凭据加密 1 块/多块
- PEAP 凭据段固定 IV 2 组
- MAC/Service/DHCP 段布局

这些测试直接断言原版二进制输出，不是对照 Python 重实现。

## 8. 结论

- 已测算法 **全部一致**，没有需要修复的偏差。
- 头构建/CRC/混淆/凭据加密的调用依赖与 C# 一致。
- 剩余缺口：
  1. 完整 569 字节私有尾的动态全量比对（需要构造 `this` 和全局对象）。
  2. PEAP 完整 vendor segment 组装（已还原 `0x5F`，其余段逐步补）。
  3. `GetClientVersion`/`GetServiceName` 需要对象状态注入。

## 9. 扩展方式

新增算法差分：

1. 用 Ghidra/rizin 定位函数地址与签名。
2. 在 `pe_call.py` 中准备输入内存，按 cdecl/thiscall 调用。
3. 用固定向量记录原版输出。
4. 在 `OriginalDifferentialTests.cs` 加断言。
5. 若不一致：先修 C#，再重跑全部向量，最后核对调用顺序。
