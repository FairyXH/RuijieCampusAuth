# 通用化与配置档案

> 目标：把项目从“某学校定制客户端”改造成“通用锐捷校园网开放认证器”。
> 产品本身不内置任何学校配置；学校差异全部放在 `profiles/` 的 JSON 档案里。

## 1. 产品定位

- 名称统一为“锐捷认证器 / 锐捷校园网开放认证器”。
- 支持官方协议（EAPOL / EAP-MD5，后续 PEAP/TLS/Portal/SAM），不依赖官方进程。
- 默认不写死学校代码、服务名、静态 ID、自助服务地址。
- 学校适配 = 导入一个 profile；协议层按 `auth_mode` 选择实现。

## 2. 配置档案

| 位置 | 用途 |
|---|---|
| `profiles/*.json` | 学校档案，随仓库分发，不编进二进制 |
| `profiles/厦门华厦学院.json` | 正式档案（已核对，可直接导入） |
| `profiles/README.md` | 字段说明与导入方法 |

导入命令：

```bash
dotnet RuijieCampusAuth.Cli.dll profile show "profiles/厦门华厦学院.json"
dotnet RuijieCampusAuth.Cli.dll profile import "profiles/厦门华厦学院.json"
dotnet RuijieCampusAuth.Cli.dll profile import "profiles/厦门华厦学院.json" --root "<安装目录>"
```

导入语义：

- 只覆盖档案中非空的字段；账号、密码、网卡、接管、Guardian 开关不动。
- 导入前备份 `settings.json.bak-<时间戳>`。
- 同时把自助/升级/Portal 地址写入 `debug.endpoints`，供调试页使用。
- `profile_name`、`auth_mode`、`school_code`、`service_name`、`static_id`、
  `dhcp_*`、`ad_*`、`engine_version` 会写入 `settings.json`，服务与 GUI 共用。

## 3. 本次已完成的通用化（C#）

| 项 | 之前 | 现在 |
|---|---|---|
| GUI 标题/托盘 | 含学校缩写 | `锐捷认证器` |
| 服务名 | `HXXY-CampusAuth` | `RuijieCampusAuth`（安装时移除旧服务） |
| Linux unit | `hxxy-campus-auth.service` | `ruijie-campus-auth.service` |
| IFEO 标记名 | `HXXYTakeover` | `RuijieTakeover`（恢复时兼容旧标记） |
| 调试管道 | `HXXY-CampusAuth-Debug` | `RuijieCampusAuth-Debug` |
| GUI 互斥锁 | `HXXY-CampusAuth-GUI` | `RuijieCampusAuth-GUI` |
| 默认服务名 | `电信宽带服务` | 空，必须由 profile 提供 |
| 默认静态 ID | `Static:AB45A862` | 空，必须由 profile 提供 |
| 快捷方式 | `HXXY-CampusAuth.lnk` | `RuijieAuth.lnk` |

兼容说明：

- 旧服务名、旧 IFEO 标记名只保留在迁移代码里，不再作为产品标识。
- 已部署环境需要重新执行 `service install` 才会切换到新服务名；旧服务会在安装时被删除。
- 外部计划任务/VBS 名称不属于 C# 代码，迁移步骤见第 5 节。

## 4. 尚未通用化的部分（明确延期）

### Python 实现（`python/`）

- 仍含学校命名的包 `ruijie_hxxy`、服务名与默认值。
- 计划：改为 `ruijie_auth.generic`，读取同一套 `profiles/*.json`；保留 `ruijie_hxxy`
  作为兼容别名。
- 状态：未改动，等待 C# 协议层稳定后同步。

### OpenWrt 插件（`openwrt/`）

已完成（2026-11-20）：

1. 包名/目录：`ruijie-auth`、`luci-app-ruijie-auth`，配置
   `/etc/config/ruijie-auth`，零 `hxxy` 字样。
2. 默认配置不含学校代码/服务名/静态 ID；`auth_mode` 默认 EAPMD5。
3. 学校档案走 `profiles/*.json`，与 C# 同一 schema；LuCI 可上传/粘贴，
   CLI 为 `ruijie-auth profile import`。
4. 算法层逐函数移植（私有尾、CRC、混淆、凭据加密、私有属性、RC4、
   MSCHAPv2、PEAP/EAP-TLS），测试向量与 C# 共用。
5. 纯用户态、零 kmod 依赖；UA3F/NFQUEUE 已移除，防检测只保留 nft
   TTL/MSS。
6. 构建脚本改名并加入 `.po → .lmo` 编译；ipk/固件均已验证可构建。

## 5. 部署迁移（现有 Windows 安装）

1. 停止旧 GUI 与服务。
2. 部署新二进制（D 盘目录可保持 `RuijieCampusAuth`）。
3. 执行 `RuijieCampusAuth.Cli.exe profile import "profiles/厦门华厦学院.json" --root <安装目录>`。
4. 执行 `service install`：安装 `RuijieCampusAuth` 并删除旧 `HXXY-CampusAuth` 服务。
5. 更新计划任务/VBS 名称时，保留旧名称作为一次性迁移来源，确认新任务可用后再删除。
6. 重启 GUI，确认标题、托盘、服务状态与认证均正常。

## 6. 验证

```bash
dotnet build RuijieCampusAuth.slnx -c Release
dotnet test RuijieCampusAuth.Core.Tests -c Release
dotnet test RuijieCampusAuth.Service.Tests -c Release
dotnet RuijieCampusAuth.Cli.dll profile show "profiles/厦门华厦学院.json"
dotnet RuijieCampusAuth.Cli.dll profile import "profiles/厦门华厦学院.json" --root <安装目录>
```

预期：导入后 `settings.json` 的 `profile_name` 为档案名，`service_name`/`static_id`
与原版客户端一致，现有账号与网卡配置不变。
