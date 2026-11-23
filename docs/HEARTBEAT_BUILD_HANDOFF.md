# SAM 直通心跳（DirComm）构建交接

> 交接时间：2026-11-23 凌晨。写给负责固件/ipk 构建的 Agent。
> 本文档只讲构建与部署所需信息；协议与逆向细节见
> `docs/protocols/dircomm-heartbeat.md`。
>
> **2026-11-23 固件侧变更（0.11.4）**：默认值已改为**关闭**
> （Go `Heartbeat: false` + UCI `option heartbeat '0'` + LuCI 开关默认
> 关闭）。本校园 SAM `172.22.11.1` 不可达，开启会每约 1 分 36 秒触发
> 一次重新认证；需要开启时设 `heartbeat='1'`，并先确认目标可达。
>
> **2026-11-23 LuCI 补全**：设置页新增“SAM 心跳”页签（开关 +
> 间隔）；Debug 页新增 `probe`（可选出口设备）与 `tail` 私有尾生成器；
> 学校档案页新增 `profile show` 的“查看”弹窗；全部中文化。

## 1. 已就绪的提交（main，已提交未推送）

| 提交 | 内容 |
|---|---|
| `35a25a3` | Go `internal/dircomm` 包：心跳报文构建 + Monitor |
| `9a440ff` | supplicant 会话接入 + UCI 开关（heartbeat / heartbeat_interval） |
| `0d1b631` | Windows C# 版（Core + Service），不影响 ipk 内容 |
| `45cb923` | 协议档案补实现状态 |

ipk 相关的是前两个提交；C# 提交虽不动 `openwrt/`，但同树构建无冲突。

## 2. 构建侧需要知道的

1. **新文件**（随 ipk 自动打包）：
   - `openwrt/ruijie-auth/internal/dircomm/dircomm.go`
   - `openwrt/ruijie-auth/internal/dircomm/dircomm_test.go`
   - `openwrt/ruijie-auth/internal/dircomm/table.bin`（53×128=6784 字节，
     官方 0x7343c0 混淆表，`go:embed` 内嵌——确认打包脚本没有把它
     当构建产物过滤掉即可）
   - 改动：`internal/supplicant/supplicant.go`、`internal/config/config.go`、
     `internal/daemon/daemon.go`
2. **验证已做过**：Ubuntu VM（root@192.168.247.134）上
   `go test ./...` 全绿（dircomm 9 项含 DES 实网向量），
   `GOOS=linux GOARCH=mipsle GOMIPS=softfloat` 交叉编译通过
   （产物 /tmp/ruijie-auth-mipsle-hb，11MB，未 strip）。
3. **Go 代码已是 LF + gofmt 干净**（VM 上 `gofmt -w` 后回写的版本），
   `gofmt -l` 应无输出；Windows 检出的 CRLF 警告是 autocrlf 正常现象。
4. **默认值**：`heartbeat` 缺省 0（关闭）。固件默认行为与 0.10.0 完全
   一致，不需要在 uci-defaults 里改任何东西；要开就部署后
   `uci set ruijie-auth.main.heartbeat='1'`。
5. **LuCI**：设置页暂未加 heartbeat 字段（UCI 可用但页面无入口），
   属可选跟进项，不阻塞构建。
6. **版本**：build-go.sh 会嵌入 VERSION/COMMIT，按你平常流程即可。

## 3. 你自己的未提交改动

工作树里 `openwrt/scripts/build-image.sh` 与 `docs/AGENT_CONTEXT.md`
有另一批未提交修改（不属于心跳工作）。构建前请先把它们按你的意图
提交或保留，避免混进心跳的构建产物说明。

## 4. 构建后验证清单（R3G）

1. `ruijie-auth selftest` 通过（新增包不影响自检路径）；
2. `opkg files ruijie-auth | grep dircomm` 或直接确认二进制内
   `strings` 含 "SAM 心跳"；
3. 认证成功后日志**不应**出现 "SAM 心跳已启动"（默认关）；
4. 开启后（`uci set ruijie-auth.main.heartbeat='1'` + reload）：
   - 认证成功应出现 `SAM 心跳已启动: <SAM IP>:<端口> 每 30s`
     （间隔取服务器 HELLO_INTERV，缺省 30s）；
   - 若服务器无响应，连续 3 次后出现
     `SAM 直通心跳超时，触发重新认证` 并自动重连；
   - `netstat -anu` 应看到本地 UDP 138（MSG_CLIENT_PORT）在发。
5. 心跳默认对准校园口（认证网卡）路由发送，无需额外配置。

## 5. 已知背景（与本构建相关的结论）

- dist/ 历史问题：上次 `dist/openwrt/ruijie-auth_0.10.0_amd64` 是旧
  源码树产物却带新版本号（COMMIT ldflags 覆盖）。这次请**同树重编
  全部架构**，避免再次出现 mipsle/amd64 源不一致。
- 逆向基准二进制：`C:\Program Files\Ruijie Networks\Ruijie
  Supplicant\8021x.exe`（SHA256 1a782d36...），与仓库 RE 文档同一份。
- 线上网络状态（截至交接）：R3G 运行 0.10.0（commit 4310534 构建），
  认证稳定；`desync_reorder` 已永久关闭（fdbcfa3）；无线 ch52/ch1，
  2.4G 拟挪 ch13；这些与心跳无关，但同机部署时别顺手回滚。

## 6. 实网待验证（部署后观察，不阻塞交付）

1. SAM 是否回应我们的心跳报文（收到 → 日志出现 "SAM 推送 N 字节"）；
2. 心跳开启后被踢时的恢复速度是否优于纯探测；
3. 下行推送报文的实际布局（目前只记长度，样本攒够再解）。
