# REA / 原版函数差分调用器

用于直接调用 `8021x.exe` 内部函数，验证 C# 实现与官方二进制逐字节一致。

## 安装

```bash
python -m pip install pefile unicorn
```

## 用法

```python
import sys
sys.path.insert(0, "tools/re_diff")
from pe_call import PeCall

img = PeCall(r"C:\Program Files\Ruijie Networks\Ruijie Supplicant\8021x.exe")
ptr = img.alloc(16)
img.write(ptr, bytes(range(16)))
result = img.call(0x13dc20, [ptr, 16])  # CRC16, RVA = VA - 0x400000
print(result.ok, hex(result.eax & 0xFFFF))
```

注意：

- `call` 接收 **RVA**（`VA - ImageBase`），Ghidra/rizin 显示的是 VA。
- 参数按 cdecl 布置；`__thiscall` 用 `ecx=` 传 `this`。
- 只适合纯算法函数；UI/对象/设备相关函数需要额外状态。

## 已验证函数

| 功能 | VA | RVA |
|---|---|---|
| CRC16 | `0x53dc20` | `0x13dc20` |
| 字节混淆 | `0x53de90` | `0x13de90` |
| 私有头 | `0x53dc80` | `0x13dc80` |
| 头+混淆 | `0x53cbb0` | `0x13cbb0` |
| MD5 | `0x53cb50` | `0x13cb50` |
| 凭据加密 | `0x53c970` | `0x13c970` |

结果与 `RuijieCampusAuth.Core.Tests/OriginalDifferentialTests.cs` 对应。
