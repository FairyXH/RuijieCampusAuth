<div align="center">
  <h1>About RuijieCampusAuth</h1>
  <p><strong>锐捷校园网 802.1X 认证的跨平台第三方实现</strong><br>
  A cross-platform, third-party implementation of Ruijie campus 802.1X authentication</p>
</div>

---

## 中文

### 一句话

用协议兼容替代官方客户端：同一套锐捷 802.1X 语义，三种可部署形态，学校参数全部外置。

### 这是什么

RuijieCampusAuth 是锐捷（Ruijie）校园网 802.1X 认证协议的第三方开源实现。它以锐捷官方 Windows 客户端 `8021x.exe` 的协议行为为参照，复现 EAPOL、EAP-MD5、569 字节私有尾、服务器私有属性、PEAP/MSCHAPv2 与 EAP-TLS，并在 Windows、Linux 与 OpenWrt 上提供可运行客户端。

项目把“协议实现”和“学校差异”分开：认证方式、服务名、静态 ID、Portal/自助/升级地址、DHCP 模式等参数放在 `profiles/*.json`，软件本体不写死任何学校信息。同一份档案可以在 C# 与 OpenWrt 实现之间复用。

OpenWrt 形态不以 R3G 为限：预编译 ipk 只要求同架构、版本相近的 OpenWrt，R3G 是当前实测载体，也是我们优先提供定制固件的机型；其他软路由可直接安装同架构 ipk，后续会补充更多机型固件。

### 三种形态

| 形态 | 技术栈 | 适用平台 | 入口 |
|---|---|---|---|
| C# 参考实现 | .NET 10 · Npcap · Avalonia | Windows | GUI、Windows 服务、CLI |
| Python 参考实现 | Python 3.10+ · scapy | Windows / Linux / macOS | 数字菜单、argparse CLI |
| OpenWrt 实现 | Go 1.25 · AF_PACKET · LuCI | OpenWrt 24.10（同架构设备；R3G 为实测载体） | 守护进程、LuCI 8 页 |

### 解决什么问题

- **学校参数不写死**：换学校只需换档案，不需要重新编译三套实现。
- **协议行为可验证**：用 Unicorn 直接调用原版二进制算法函数做逐字节差分，用真实服务器接受的抓包校验私有尾与私有属性，避免把“看起来能跑”当成“协议正确”。
- **部署形态可选**：Windows 上要 GUI 和服务，Linux 上要脚本和 API，路由器上要无 GUI 常驻，同一套协议语义都能覆盖。
- **不依赖官方进程**：认证链路自己完成，不要求本机安装或运行官方客户端。

### 不做什么

- 不修改、不破解、不绕过校园网认证服务器或其策略。
- 不复制官方客户端的界面皮肤、弹窗、公告、托盘、安装器等非协议组件。
- 不内置任何学校的账号、密码或登录凭据。
- 不把未经验证的协议行为包装成“已支持”；未知项在文档中显式标注。

### 当前状态

- C# 客户端 **1.0.0**、OpenWrt 插件 **0.11.5** 已发布；R3G 定制固件同步提供，作为当前实测载体。
- EAP-MD5 已在真实校园网完成认证；569 字节私有尾与服务器私有属性已用真实抓包逐字节校验。
- PEAP/MSCHAPv2 与 EAP-TLS 协议栈已实现，仍需真实 RADIUS 端到端验证。
- 详细状态、验证证据与未闭环项见 [README](README.md) 与 [`docs/AGENT_CONTEXT.md`](docs/AGENT_CONTEXT.md)。

### 许可与声明

MIT 许可证。本项目与锐捷网络无隶属或授权关系，仅用于协议互操作研究与合法网络接入。使用者应确保对目标校园网具有合法接入权限，并遵守所在学校的管理规定。

---

## English

### In one sentence

Protocol compatibility instead of the official client: one Ruijie 802.1X implementation, three deployable forms, and school parameters kept entirely external.

### What it is

RuijieCampusAuth is a third-party, open-source implementation of the 802.1X authentication protocol used by Ruijie campus networks. It models the protocol behaviour of Ruijie's official Windows client, `8021x.exe`, and reproduces EAPOL, EAP-MD5, the 569-byte private tail, server private properties, PEAP/MSCHAPv2, and EAP-TLS. It ships runnable clients for Windows, Linux, and OpenWrt.

The project separates the protocol implementation from school-specific differences. Authentication method, service name, static ID, portal/self-service/upgrade endpoints, and DHCP mode live in `profiles/*.json`; the software contains no hard-coded school information. The same profile works across the C# and OpenWrt implementations.

The OpenWrt form is not limited to the R3G: the prebuilt ipk only requires a same-architecture OpenWrt of a similar version. The R3G is the current test vehicle and the first model we provide custom firmware for; other soft routers can install the same-architecture ipk directly, and more model-specific firmware will follow.

### Three forms

| Form | Stack | Platforms | Entry point |
|---|---|---|---|
| C# reference | .NET 10 · Npcap · Avalonia | Windows | GUI, Windows service, CLI |
| Python reference | Python 3.10+ · scapy | Windows / Linux / macOS | Numeric menu, argparse CLI |
| OpenWrt | Go 1.25 · AF_PACKET · LuCI | OpenWrt 24.10 (same-arch devices; R3G is the test vehicle) | Daemon, 8 LuCI pages |

### What it solves

- **No hard-coded school parameters**: switching schools means switching a profile, not rebuilding three implementations.
- **Verifiable protocol behaviour**: Unicorn calls into the original binary's algorithm functions for byte-for-byte differential testing, and real server-accepted captures validate the private tails and private properties. "It runs" is not treated as "the protocol is correct".
- **Deployment flexibility**: a GUI and service on Windows, scripts and an API on Linux, and a headless daemon on a router all share the same protocol semantics.
- **No dependency on the official process**: the client completes the authentication path itself and does not require the official client to be installed or running.

### What it does not do

- It does not modify, crack, or bypass campus authentication servers or their policies.
- It does not copy non-protocol components of the official client, such as skins, pop-ups, announcements, tray apps, or installers.
- It does not embed any school account, password, or login credential.
- It does not present unverified protocol behaviour as "supported"; open items are called out explicitly in the documentation.

### Current status

- C# client **1.0.0** and the OpenWrt plugin **0.11.5** are released; the R3G custom firmware ships alongside it as the current test vehicle.
- EAP-MD5 has completed authentication on a real campus network; the 569-byte private tails and server private properties have been validated byte for byte against real captures.
- The PEAP/MSCHAPv2 and EAP-TLS stacks are implemented but still need end-to-end validation against a real RADIUS server.
- See the [README](README.md) and [`docs/AGENT_CONTEXT.md`](docs/AGENT_CONTEXT.md) for detailed status, evidence, and open items.

### License and disclaimer

MIT License. This project is not affiliated with or endorsed by Ruijie Networks. It exists for protocol interoperability research and legitimate network access. Users are responsible for confirming that they are authorised to access the target campus network and for complying with their institution's policies.

---

## GitHub About（仓库简介）

适合直接填入 GitHub 仓库侧栏的简短双语文案：

> 锐捷校园网 802.1X 第三方客户端：C#/.NET 10（GUI/服务/CLI）、Python 参考实现、OpenWrt Go + LuCI。复现 EAP-MD5/PEAP/EAP-TLS、569 字节私有尾与服务器私有属性，学校参数外置。
>
> Cross-platform Ruijie campus 802.1X supplicant: C#/.NET 10, Python, OpenWrt Go + LuCI. EAP-MD5/PEAP/EAP-TLS, 569-byte private tail, profile-driven.

建议 Topics：

`802.1x` · `eapol` · `eap-md5` · `peap` · `mschapv2` · `eap-tls` · `ruijie` · `campus-network` · `openwrt` · `dotnet` · `avalonia` · `python` · `golang` · `luci` · `reverse-engineering` · `protocol-analysis`
