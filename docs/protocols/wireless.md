# 无线 WPA2-Enterprise

状态：P7 已实现（Windows WLAN Profile 管理）。实测只读列表通过；创建/连接需现场 AP 验证。

## 1. 原版证据

- `Conf/Wireless/baseXml/base.rg`：WLANProfile 模板，`WPA`/`TKIP`、`useOneX=true`、
  EAP type 25、AuthorId 29115。
- `Conf/Wireless/baseXml/userCredential.xml`：`EapHostUserCredentials`，
  EAP type 25，Identity/Password。
- RTTI/日志：`CWLanWrap::SetUserInfo/UpdateProFile`、`CWiFiInterface::SendCMDtoWifi`、
  `CWlanStateThread`、`CWlanWrap::WLanConnect OS1xProc`、`CRefreshWirelessConfigThread`。
- 配置项：`WLAN` 管理器、`WPA/WPA2`、`TKIP/CCMP`、SSID 列表、`userCredential.xml`。

## 2. C# 实现

`Core/Protocols/WlanProfileManager.cs`：

- WLAN API：`WlanOpenHandle`、`WlanEnumInterfaces`、`WlanGetProfileList`、
  `WlanSetProfile`、`WlanSetProfileEapXmlUserData`、`WlanDeleteProfile`、
  `WlanConnect`、`WlanDisconnect`。
- `BuildProfileXml`：
  - 默认 `WPA2` + `AES` + `useOneX=true` + `authMode=user`。
  - EAP 外层 25（PEAP），内层 26（MSCHAPv2）。
  - 可选 `ServerNames` / `TrustedRootCA`；默认不强制服务器校验（与原版一致）。
- `BuildUserCredentialXml`：EAP type 25，Identity/Password。
- 接口匹配：NPF GUID / 网卡描述；默认第一个 WLAN 接口。
- 名称/XML 做转义。

`Core/Debug/WirelessProfileDebugFeature.cs`（`wireless-profile`）：

| action | 说明 |
|---|---|
| `list` | 列出 WLAN 接口与配置 |
| `create` | `ssid`/`identity`/`password`/`authentication`/`encryption`/`auto`/`server_names`/`trusted_root_ca` |
| `delete` | `profile` 或 `ssid` |
| `connect` | `profile` 或 `ssid` |
| `disconnect` | 断开 WLAN |

测试：`Core.Tests/WlanProfileTests.cs` 验证 XML 内容与转义；不做真实创建/删除。

## 3. 实测（2026-11-19）

- `wireless-profile list`：1 个接口（Intel AX201），5 个配置
  （ZCF、ZhangAndroid、HXXY-WiFi、ZXH、ZXH-WiFi4）。
- 未执行 create/connect/delete，避免改动现有网络。

## 4. 未确认 / 后续

1. 真实 AP + RADIUS 下的 PEAP 连接（与 P6 共用）。
2. EAP-TLS 无线配置（证书凭据 XML）。
3. 原版 `WPA_do_*`/PMKID 处理是否只用于无线密钥，不参与 802.1X 认证。
4. 小锐 WiFi 分享/NAT/ICS（`xiaorui-share`，P8 计划）。
5. 机器认证 `authMode=machineOrUser` 与用户认证的差异。
