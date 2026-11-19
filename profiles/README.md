# 配置档案（School Profile）

程序默认不内置任何学校配置。学校差异全部放在本目录的 JSON 档案里，通过
`profile import` 导入到当前安装目录的 `settings.json`。

## 用法

```bash
# 查看档案
dotnet RuijieCampusAuth.Cli.dll profile show profiles/hxxy.json

# 导入到当前目录（程序目录）
dotnet RuijieCampusAuth.Cli.dll profile import profiles/hxxy.json

# 导入到指定安装目录（会先备份 settings.json）
dotnet RuijieCampusAuth.Cli.dll profile import profiles/hxxy.json --root "D:\Storageredirect\Program\RuijieCampusAuth"
```

导入只覆盖档案中声明的字段，账号、密码、网卡、接管与 Guardian 开关保持不变。
导入前会在目标目录生成 `settings.json.bak-<时间戳>`。

GUI 的「档案」页会扫描程序目录/当前目录的 `profiles/`，选择后点击“导入/切换”即可；
可以放多个学校档案，随时切换。CLI `profile list` 默认扫描同样的目录。

OpenWrt 端使用完全相同的 JSON：

```sh
# 路由器上执行
ruijie-auth profile list
ruijie-auth profile import --file /tmp/hxxy.json
```

也可以把档案放到 `/etc/ruijie-auth/profiles/`，或直接在 LuCI
「锐捷认证器 → 学校档案」页上传/粘贴。导入会先备份
`/etc/config/ruijie-auth`，账号池保持不变。

## 字段

| 字段 | 说明 |
|---|---|
| `name` | 学校/档案名称，仅用于显示 |
| `school_code` | 锐捷学校代码，例如 `HPVTC` |
| `auth_mode` | `EAPMD5` / `PEAP` / `EAPTLS`（当前 C# 已实现 EAPMD5） |
| `service_name` | 私有尾包 0x39 服务名 |
| `static_id` | 私有尾包 0x54 静态 ID |
| `self_service_url` | 自助服务地址 |
| `update_url` | 升级检查地址 |
| `portal_url` | Portal 地址，留空表示不使用 |
| `sam_host` / `sam_port` | SAM 直通地址，留空/0 表示不使用 |
| `dhcp_mode` | `before` / `after` / `none` |
| `dhcp_timeout` | DHCP 超时秒数 |
| `dhcp_option82` | 是否启用 Option 82 |
| `ad_domain` | AD 域，留空表示不使用 |
| `ad_timeout` | AD 域统一认证超时 |
| `engine_version` | 对应原版引擎版本，用于协议能力判断 |
| `notes` | 备注 |

未识别的字段会被忽略并在 `extra` 中保留，便于后续协议阶段扩展。
