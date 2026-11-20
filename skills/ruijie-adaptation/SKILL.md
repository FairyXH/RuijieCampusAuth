---
name: ruijie-adaptation
description: >
  一条龙完成锐捷校园网认证客户端的逆向分析与新学校适配：用 REA/Ghidra/rizin
  分析原版客户端（8021x.exe），用 Unicorn 差分校对 C# 算法实现，编写学校配置
  JSON 档案并接入 GUI/服务验证。适用于本项目仓库内的一切适配任务。
---

# 锐捷原版客户端逆向与学校适配（一条龙）

本仓库是一个与锐捷官方协议兼容的第三方认证客户端（C# / .NET 10）。
把一台装了官方锐捷客户端的机器适配成自研客户端，按下面五个阶段走，
每个阶段都有可复现的产物与验收标准。**不要跳过差分校对**——仓库里所有
协议算法都锁定在原版二进制的输出上，跳过校对的实现一律不接受。

## 阶段 0：环境与工具链（全部自动探测，禁止写死路径）

所有工具路径按下面的顺序探测，探测不到才提示用户安装；任何一步都不要
把盘符或安装目录写死：

1. **环境变量优先**：`GHIDRA_INSTALL_DIR`、`REA_RIZIN_COMMAND`、
   `REA_IDA_MCP_CONFIG`、`JAVA_HOME`、`REA_WABT_BIN_DIRECTORY` 等用户级
   变量（REA 体系安装时写入）。PowerShell 探测：
   `[Environment]::GetEnvironmentVariable('GHIDRA_INSTALL_DIR','User')`。
2. **PATH 探测**：`where rizin`、`where analyzeHeadless`、`where ghidraRun`。
3. **模式匹配兜底**：在检测到的磁盘根下通配搜索，例如
   `Get-ChildItem <盘>:\ -Recurse -Filter analyzeHeadless.exe -ErrorAction SilentlyContinue`
   （先浅后深：先扫一二级目录，再按 `Ghidra*/support/`、`rizin*/bin/` 模式收窄），
   驱动器枚举用 `[System.IO.DriveInfo]::GetDrives()`。
4. **原版客户端定位**：默认路径
   `%ProgramFiles%\Ruijie Networks\Ruijie Supplicant\8021x.exe`；
   探测不到就搜注册表卸载键
   `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` 里的
   "Ruijie"，或查运行中的 `8021x.exe` 进程路径。目标只是拿到一份
   `8021x.exe` 样本，之后一切分析在副本上进行（原文件只读）。
5. **Python 依赖**：`python -m pip install pefile unicorn`（差分调用器依赖）。
6. **分析产物复用**：历史产物目录（Ghidra 反编译/RTTI/字符串/PE 画像）按
   `<工具链根>/_rea_analysis` 探测；不存在也没关系，仓库内的
   `docs/REVERSE_ENGINEERING.md` 与 `docs/protocols/*.md` 是权威汇总，
   大部分结论已经沉淀，直接引用即可。

| 工具 | 用途 |
|---|---|
| Ghidra headless（analyzeHeadless） | 批量反编译、RTTI/字符串提取 |
| rizin | 交叉引用、快速反汇编（调用是 E8 相对调用） |
| Unicorn 2.x + pefile | `tools/re_diff/pe_call.py` 按 RVA 直调、`full_diff.py` 批量差分 |
| C# 对照端 | 仓库 `tools/csharp_diff/`（DiffRunner：stdin 吃 JSON op、stdout 吐 JSON 结果） |
| tshark / Wireshark | 真实抓包（可选） |

关键结论（不要重新发明）：原版 `8021x.exe` 无壳无混淆、无导出表，只能按
RVA 直调（`ImageBase 0x400000`）；`.text` 里 `push ebp; mov ebp,esp` 序言
~7000 个；443 个 RTTI 类名 + 276 个 `类::方法` 日志标签可定位业务代码。

## 阶段 1：静态分析（定位协议与算法）

1. 用 rizin 找交叉引用（注意：调用是 E8 相对调用，绝对地址扫描搜不到；
   用 `axt` 或对 `.text` 做相对调用扫描）。
2. 用 Ghidra headless 反编译目标函数，产物存 `_rea_analysis\`。
3. 业务代码锚点：`CRGPrivateProc::*`、`CEapPEAPThread`、`CDhcpThread`、
   `CStateMachineThread` 等 RTTI 类名；调试日志字符串（GBK）直接给出
   `类::方法` 名。
4. 每个新结论写进 `docs/protocols/*.md`（证据 = 反编译片段 + 实测地址）。

已知核心函数地址（VA，见 `docs/REVERSE_ENGINEERING.md` 第 15 节）：
`0x53A590` 私有尾组装、`0x537EE0` 分段私有属性解析、`0x51A890` 平铺 TLV
解析、`0x53C970` 凭据加密、`0x53DC20` CRC16/XMODEM、`0x53DE90` 位反转混淆、
`0x53DF40` 解混淆（与混淆同一变换，自逆）。

## 阶段 2：Unicorn 差分校对（算法级）

用 `tools/re_diff/pe_call.py` 在 Unicorn 里映射整个 PE 镜像按 RVA 直调原版
函数，与 C# 实现比对输入输出。工程化入口是 `tools/re_diff/full_diff.py`
（Phase A 核心算法 / Phase B 解析器 / 真实抓包向量），C# 侧跑
`tools/csharp_diff` 的 DiffRunner。

调用约定与陷阱（全部踩过）：

- 多数算法函数是 cdecl；`ReadRGVendorSeg`/`GetServiceName` 等是
  **thiscall**（`ecx=` 传对象指针）；
- Win32 导入桩：KERNEL32 等 stdcall 需要按参数个数清栈
  （`full_diff.py` 里有 `STDCALL_ARGS` 表）；LIBEAY32 的 RC4 按序号导入
  （479=set_key, 477=RC4）；
- CRT 字符串对象布局：`this[0]=数据指针`，长度在 `ptr-0xC`，引用计数在
  `ptr+0xC`；构造空字符串直接调镜像内构造器 `0x40C100`（thiscall）；
  CRT 分配器单例在 `0x756A0C`，先用动态初始化 thunk `0x6AE489` 建好；
- 日志函数 `0x4D64C0/0x4D6220/0x4D6720` 与 CRT `_write 0x664481` 打 RET 桩。

验收：新增/修改的算法必须同时进
`RuijieCampusAuth.Core.Tests/OriginalDifferentialTests.cs`
（锁原版输出的向量）与差分报告 `build/capture/`。Phase A 参考基线：
21 向量 20 一致（唯一"不一致"是 len%16!=0 的调用契约差异）。

## 阶段 3：真实抓包交叉验证

Unicorn 里跑不通的引擎态函数（GetMACAddr、CEapPEAPThread 内部）用
**被动抓包 + 真实包回放**验证：

```powershell
tshark -i \Device\NPF_{网卡GUID} -f "ether proto 0x888e" -w capture.pcap
```

仓库已有一份实网抓包：`build/capture/live-auth.pcap`（EAP-Success 的
分段私有属性块 + 两份 569 字节私有尾，C# 重建 569/569 字节一致）。
解析 pcapng 用 Python `struct` 直接走 EPB，不要依赖 tshark 文本导出。

## 阶段 4：编写学校配置档案（JSON）

模板与字段权威文档：`profiles/README.md`。以下为通用占位示例，不包含
任何学校的实际域名、代码或标识：

```json
{
  "name": "<学校名称>",
  "school_code": "<school-code>",
  "auth_mode": "EAPMD5",
  "service_name": "<service-name>",
  "static_id": "<static-id>",
  "self_service_url": "http://<school-domain>/selfservice/",
  "update_url": "http://update.rghall.com.cn/das/update",
  "portal_url": "",
  "sam_host": "",
  "sam_port": 0,
  "dhcp_mode": "before",
  "dhcp_timeout": 30,
  "dhcp_option82": false,
  "ad_domain": "",
  "ad_timeout": 120,
  "engine_version": "5.0.2014.108"
}
```

来源优先级：目标机器的 `SuConfig.dat`（XOR 0xFF 可读）>
`SuConfig.txt` > 认证成功包里的服务器私有属性（INDICATE_PORT/SERV_IP、
ENCRYPT_KEY/IV、SU_REAUTH_INTERV）> 抓包。

字段语义要点：`auth_mode` 决定认证分发（EAPMD5/PEAP/PEAPV1/EAPTLS，
未知回退 MD5）；`dhcp_mode` 的 before/after 驱动认证前后自动续租；
`service_name`/`static_id` 进 569 字节私有尾的 0x39/0x54 属性；
ENCRYPT_KEY/IV 同时是 SAM 直通的 DES key/IV。

## 阶段 5：接入与验收

1. 档案放 `profiles/`，GUI「配置」页导入/切换，或
   `RuijieCampusAuth.Cli.exe profile import profiles/<学校>.json --root <安装目录>`。
   导入会自动同步 service.json（运行中的服务下一轮重试即生效）。
2. 跑全部测试：`dotnet test RuijieCampusAuth.Core.Tests`（含原版差分锁定
   向量）+ `RuijieCampusAuth.App.Tests`。
3. 部署：`powershell -File build-csharp.ps1 -Runtime win-x64 -Deploy`
   （部署目标目录用 -DeployDir 传入或跟随脚本默认；冻结还原机器上 C 盘的启动器与服务名是基线内容，禁止改动），然后
   `RuijieCampusAuth.Cli.exe service restart`（服务名保持原样，不要
   `service install` 迁移）。
4. 验收看 `logs/service.log`：`认证成功` + 服务器属性行出现即通过。

## 红线

- 原版二进制只读，一切修改在自研实现里做；
- 任何算法改动必须先有原版输出向量（Unicorn 或真实抓包），没有证据的
  "优化"会被差分测试当场打回；
- 不做认证绕过、不改服务器行为——本项目是协议兼容的第三方客户端。
