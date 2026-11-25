# 00:00-06:00 静默窗口失效修复（0.11.5）

> 2026-11-26。R3G 实测：校园网认证关闭时段（00:00-06:00，`quiet_start=0`,
> `quiet_end=360`）内守护进程仍每约 6 秒重试一轮，持续 40 分钟以上。

## 现象

路由器时间 00:29 已处在配置的静默窗口内，但 `logread` 持续输出：

```
00:28:11 [信息] authenticating | 正在寻找认证服务器... | S12016004 |
00:28:11 [信息] authenticating | 检测到掉线，重新认证... | S12016004 |
00:28:18 [信息] authenticating | 正在寻找认证服务器... | S12016004 |
00:28:18 [信息] authenticating | 检测到掉线，重新认证... | S12016004 |
```

`ruijie-auth status` 显示「认证中 | 检测到掉线，重新认证...」，而非
「已暂停 | 校园网认证关闭时段」。

## 证据链

1. 配置正确：`quiet_start=0`、`quiet_end=360`，`quiet_window` 缺省即 true
   （`internal/config/config.go` 默认值）。
2. 二进制含全部静默窗口代码：`inQuietWindow`/`quietLoop`/`quietClock`
   符号存在，`账号池加载`、`校园网认证关闭时段`、`暂停认证` 等中文字符串
   均在（`grep -a` 命中）。
3. 完整日志里 `账号池加载` 只出现 14 次，全部集中在 2026-11-25
   12:32–13:24 与 23:23–23:24 两个时段；`关闭时段`/`暂停认证` 出现 0 次。
4. 时间线：23:24:06 认证成功建立会话 → 之后日志空白 → 00:02:54 突然
   出现「检测到掉线，重新认证」→ 此后每 6 秒一轮，直到人工重启。

结论：`daemon.loop()` 从 23:24 起就再没迭代过，静默窗口检查
（`loop()` 顶部 `if d.inQuietWindow(cfg)`）根本没有被求值。

## 根因

静默窗口只在**会话之间**检查，而会话是驻的：

- `daemon.loop()` → `runAccounts()` → `supplicant.Session.Run()`，
  EAP-MD5 路径在 `runEAPMD5()` 的 `for { select { ... } }` 里自持认证、
  探活、重启认证状态机，**正常情况永不返回**。
- 00:00 窗口开始时会话正持有（23:24 建立）；00:02:54 autoguard 探测失败
  → 内部 `restartAuth("检测到掉线，重新认证...")` → 在此循环里继续重试。
  整个过程 `daemon.loop()` 没有机会重新 `config.Load` 并判断窗口。

即：窗口边界只能被「会话结束」这一事件触发，而常驻会话恰恰不会结束。
这也解释了此前 20:06-20:08 的临时窗口实测为何「正常」——当时守护进程
处于会话结束后的 loop 迭代中，属于窗口检查可达的路径。

## 修复

让常驻会话自己感知窗口，而不是只依赖 daemon 轮询：

1. `internal/supplicant`：
   - 新增 `KindQuiet` 结果类型。
   - 新增 `Options.Quiet func() bool` 与 `Options.QuietPoll`
     （默认 30s）。
   - `runEAPMD5()` 主 select 增加静默 ticker：窗口打开即
     `status("paused", "校园网认证关闭时段，暂停认证")` 并返回 `KindQuiet`。
   - `runTunnel()`：每次尝试前检查一次；认证持有循环（`hold`）内增加
     同样的 ticker。
2. `internal/daemon`：
   - 构造 `Options` 时注入 `Quiet` 闭包，每次重新 `config.Load`，因此
     运行期改配置也能立刻生效。
   - `loop()` 增加 `case supplicant.KindQuiet: continue`，回到顶部后由
     既有 `inQuietWindow` 分支进入 `quietLoop`。
   - 手动 `auth`/`resume` 置 `quietForced`，本轮会话不再被 ticker 打断；
     窗口结束后自动复位，`reload` 也复位。

## 验证

- 单元测试：`TestSessionStopsWhenQuietWindowOpens`（窗口打开后会话必须
  以 `KindQuiet` 退出）、`TestSessionIgnoresClosedQuietWindow`（窗口关闭时
  必须保持常驻）。Windows 与 Ubuntu 两个环境 `go test` 均通过。
- 交叉编译：`GOOS=linux GOARCH=mipsle GOMIPS=softfloat`，stripped 7.8MB。
- 路由器实测（00:58 重启后 30 秒观察）：
  `status` = 「已暂停 | 校园网认证关闭时段（00:00-06:00），暂停认证」；
  重启后 `正在寻找认证服务器` 计数为 0，日志仅每 30 秒一次 `paused`。

## 部署

```
cp /usr/sbin/ruijie-auth /usr/sbin/ruijie-auth.bak-20261126
# 替换为新二进制后
/etc/init.d/ruijie-auth restart
```

回滚：把 `ruijie-auth.bak-20261126` 覆盖回 `/usr/sbin/ruijie-auth` 并重启。
