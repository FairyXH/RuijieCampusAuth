# SAM 直通与升级检查

状态：P8 已实现探针/检查工具。SAM 完整报文布局未确认，未伪造握手；升级只检查不安装。

## 1. SAM 直通

### 原版证据

`dircomm.log`（`SAM直通协议参数如下`）：

- 协议版本 2
- 重传次数 2
- 超时时间 2000ms
- DES key：`7C73533126403871`
- IV key：`4163332331215164`
- UTC time（8 字节）
- SU PORT：138
- GetWay IP / SU IP
- SAM IP：172.22.11.1，侦听端口 53
- `bUseHandshake2Sam`、`dwTimerToSAM`

相关类：`CDirectTranSrv`、`CDirTranThread`、`CSuDES`、`CContextControlThread::ONSAMWantLogOff`。

### 已知

- UDP 直通；SU 侧端口 138，SAM 侧端口 53（部署相关）。
- DES key/IV 为 8 字节，可能用于报文体或字段加密。
- 协议版本 2，带重传与 2 秒超时。

### C# 实现

`Core/Protocols/SamUpgradeClients.cs`：

- `SamDirectClient.ProbeAsync(host, port, payload, timeout)`：发 UDP，收响应，返回双向 hex。
- `SamDirectClient.DesTransform(data, key, iv, cbc/ecb, encrypt)`：DES 变换工具，无填充。
- `sam-direct` 调试功能：
  - `host`/`port`（缺省用档案 `sam_host`/`sam_port`）
  - `payload_hex`
  - `des_key`/`des_iv`/`des_mode`/`encrypt`
  - `timeout_ms`

实测：本机 UDP echo 返回 `pong:00001311`，15ms。

### 未确认

1. 报文头/字段布局、版本 2 的具体结构。
2. DES 作用范围（整包/字段）与填充方式。
3. UTC/重传/序号字段位置。
4. SAM 返回结果如何映射到认证结果。

在拿到真实 SAM 交互抓包前，不实现“完整 SAM 认证”；探针只用于取证。

## 2. 升级检查

### 原版证据

- `CContextControlThread::DoUpgrade`
- `update.rghall.com.cn/das/update`
- `CDownloadManager` HTTP/FTP 下载、`CFtpWorkThread`
- `SU_NEWEST_VER` / `SU_UPGRADE_URL` 私有属性

### C# 实现

- `UpgradeClient.CheckAsync(url)`：GET 升级地址，返回状态、body、JSON。
- `upgrade-check` 调试功能：`url`（缺省用档案 `update_url`）。
- **不下载、不安装**；包校验/安装留给后续。

实测：本机 HTTP 假服务返回 `{"version":"1.2.3"}`，解析正常。

### 未确认

1. 升级响应格式（版本号/下载地址/签名）。
2. 包校验方式（哈希/签名）与安装流程。
3. FTP 分支。

## 3. 调试参数

| 功能 | 参数 |
|---|---|
| `sam-direct` | `host`、`port`、`payload_hex`、`des_key`、`des_iv`、`des_mode`(cbc/ecb)、`encrypt`、`timeout_ms` |
| `upgrade-check` | `url` |
