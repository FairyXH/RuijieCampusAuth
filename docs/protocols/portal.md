# Portal / 客户端中心 / 自助服务

状态：P5 已实现（Portal HTTP、客户端中心、自助服务、服务列表更新、Portal UDP、用户消息）。
依据：`PortalAuth.dll` 反汇编、`8021x.exe` 字符串与运行日志。

## 1. Portal（eportal）

### 证据

- `PortalAuth.dll` 导出：`StartPortalAuth`（0x10003560）、`SendLogout`（0x10004d80）。
- 端点字符串：
  - `eportal/inferface/authAPI/login`
  - `eportal/inferface/authAPI/logout`
  - `eportal/inferface/authAPI/getOnlineUserInfo`
  - `eportal/inferface/authAPI/pageInfo`
- HTTP 头：`Accept: application/json`、`Content-Type: application/json`；
  方法 `POST`；User-Agent `HttpCall`；使用 WinINet。
- 字段字符串：`userId`、`password`、`service`、`queryString`、`userAgent`、
  `userip`、`userIndex`、`portalUrl`、`selfUrl`、`announcement`、`result`、`message`、
  `keepaliveInterval`、`success`。
- 登录 service 值拼接 `PC` 后缀（代码在 service 字符串后追加 `"PC"`）。

### 实现

- `Core/Protocols/PortalClient.cs`
  - `login`: `{"userId","password","service","queryString","userAgent","userip"}`
  - `logout`: `{"userip","userIndex"}`
  - `getOnlineUserInfo` / `pageInfo`: `{"userip","userIndex"}`
  - 允许通过 `extra` 合并/覆盖字段，便于现场修正。
- `Core/Debug/PortalDebugFeatures.cs` 的 `portal-auth` 调试功能。
- 测试：本地 TCP 假服务器验证路径、JSON 字段、Content-Type。

### 未确认

1. `logout`/`getOnlineUserInfo`/`pageInfo` 是否还包含 `userId`/`queryString`；
   当前按反汇编中出现的 `userip`/`userIndex` 实现，支持 `extra` 覆盖。
2. 响应字段（`success/message/keepaliveInterval/announcement`）语义未逐项验证。
3. `password-change` 的实际端点与字段未找到；保持计划状态。

## 2. 客户端中心

- 原版查询串（`CClientCenterPeerManager` 日志）：
  `mac=...&ipv4=...&ipv61..4=0&product=...&mainver=...&subver=...&schoolcode=...&osbits=32&releasedate=2019.07.24`
- 实现：`Core/Protocols/ClientCenterClient.cs`，`client-center` 调试功能。
- 未确认：实际 endpoint（原版从服务器下发/配置组合），需抓包或现场配置。

## 3. 自助服务

- 配置键 `SelfSvrURL`；原版解析“ball JSON”并刷新余额/套餐/在线时长。
- 实现：`Core/Protocols/ClientCenterClient.cs` 的 `SelfServiceClient`，
  `self-service` 调试功能返回原始 body 与 JSON。
- 未确认：ball JSON 的字段结构（由学校自助系统决定）。

## 4. 服务列表更新 / 用户消息

- 服务列表：私有属性 type `0x42`（66）/`0x66`（102）/`0x68`（104）/`0x77`（119）。
  实现：`server-list-update` 调试功能，解析并展示。
- 用户消息：私有属性 type `0x3C`（60）/`0x3D`（61）/`0x52`（82）/`0x53`（83）。
  实现：`user-messages` 调试功能。
- 文本编码按 GB18030 解码。

## 5. Portal UDP

- 原版：`CUDPListenThread`、`CContextControlThread::OnRecvePortalUDPPacketReturn`、
  `CPortalMsgDlg`；端口来自服务器下发的 `INDICATE_PORT` / `MSG_CLIENT_PORT`。
- 实现：`portal-udp` 调试功能，指定端口限时监听，返回来源、文本与 hex。
- 未确认：报文格式（JSON/私有二进制）与端口分配策略。

## 6. 调试功能一览

| name | 参数 |
|---|---|
| `portal-auth` | `action`(login/logout/online/pageinfo)、`portalUrl`、`userId`、`password`、`service`、`queryString`、`userIp`、`userAgent`、`userIndex`、`extra` |
| `client-center` | `url`、`mac`、`ipv4`、`product`、`mainver`、`subver`、`schoolcode`、`osbits`、`releasedate` |
| `self-service` | `url` |
| `server-list-update` | `hex` |
| `user-messages` | `hex` |
| `portal-udp` | `port`、`duration` |

## 7. 安全

- Portal 请求可能携带密码：审计日志只记录脱敏后的参数；响应 body 可能包含会话信息，
  调试页默认只在本地展示。
- 所有 HTTP 客户端 `UseProxy=false`，避免系统代理干扰；TLS 校验保持默认。
- 不自动执行 Portal/UDP；必须手动触发。
