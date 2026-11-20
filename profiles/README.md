# 配置档案（School Profile）

程序本体不写死任何学校配置。学校差异全部放在本目录的 JSON 档案里，通过
`profile import` 导入到当前安装目录的 `settings.json`。

本目录可以放多份学校档案，每校一个 `<学校>.json`。仓库附带一份已按
`SchoolProfile` schema 核对、可直接导入的正式档案；新增学校按同一 schema
添加文件即可。

## 用法

```bash
# 查看档案
dotnet RuijieCampusAuth.Cli.dll profile show "profiles/<学校>.json"

# 导入到当前目录（程序目录）
dotnet RuijieCampusAuth.Cli.dll profile import "profiles/<学校>.json"

# 导入到指定安装目录（会先备份 settings.json）
dotnet RuijieCampusAuth.Cli.dll profile import "profiles/<学校>.json" --root "<安装目录>"
```

导入只覆盖档案中声明的字段，账号、密码、网卡、接管与 Guardian 开关保持不变。
导入前会在目标目录生成 `settings.json.bak-<时间戳>`。

GUI 的「档案」页会扫描程序目录/当前目录的 `profiles/`，选择后点击“导入/切换”即可；
可以放多个学校档案，随时切换。CLI `profile list` 默认扫描同样的目录。

OpenWrt 端使用完全相同的 JSON：

```sh
# 路由器上执行
ruijie-auth profile list
ruijie-auth profile import --file "/tmp/<学校>.json"
```

也可以把档案放到 `/etc/ruijie-auth/profiles/`，或直接在 LuCI
「锐捷认证器 → 学校档案」页上传/粘贴。导入会先备份
`/etc/config/ruijie-auth`，账号池保持不变。

## 字段

| 字段 | 说明 |
|---|---|
| `name` | 学校/档案名称，仅用于显示 |
| `school_code` | 锐捷学校代码，例如 `<学校代码>` |
| `auth_mode` | `EAPMD5` / `PEAP` / `EAPTLS`（实现状态见根 README） |
| `service_name` | 私有尾包 0x39 服务名 |
| `static_id` | 私有尾包 0x54 静态 ID |
| `self_service_url` | 自助服务地址 |
| `update_url` | 升级检查地址 |
