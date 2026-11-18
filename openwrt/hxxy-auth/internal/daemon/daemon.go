package daemon

import (
	"context"
	"fmt"
	"net"
	"net/url"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/config"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/control"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/logging"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/network"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/protocol"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/servers"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/status"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/supplicant"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/transport"
)

// Paths are the daemon runtime paths.
type Paths struct {
	Config  string
	Socket  string
	Status  string
	DataDir string
	Log     string
}

// DefaultPaths returns the OpenWrt defaults.
func DefaultPaths() Paths {
	return Paths{
		Config:  config.DefaultPath,
		Socket:  control.DefaultSocket,
		Status:  status.DefaultPath,
		DataDir: "/etc/hxxy-auth",
		Log:     "/var/log/hxxy-auth.log",
	}
}

type runner struct {
	paths    Paths
	opener   transport.Opener
	log      *logging.Logger
	commands chan string
	statusMu sync.Mutex
}

// Run starts the resident daemon.
func Run(ctx context.Context, paths Paths) error {
	if paths.Config == "" {
		paths = DefaultPaths()
	}
	initial, _ := config.Load(paths.Config)
	logger := logging.New(paths.Log, initial.LogLevel)
	defer logger.Close()

	ctx, stop := signal.NotifyContext(ctx, os.Interrupt, syscall.SIGTERM)
	defer stop()

	d := &runner{
		paths:    paths,
		opener:   transport.Open,
		log:      logger,
		commands: make(chan string, 8),
	}
	go func() {
		if err := control.Serve(ctx, paths.Socket, d.commands); err != nil {
			logger.Errorf("控制接口启动失败: %v", err)
		}
	}()

	d.setStatus("starting", "正在启动", "", "", "")
	d.loop(ctx)
	d.setStatus("stopped", "服务停止", "", "", "")
	logger.Infof("认证服务停止")
	return nil
}

func (d *runner) loop(ctx context.Context) {
	for {
		cfg, err := config.Load(d.paths.Config)
		if err != nil {
			d.setStatus("error", "配置读取失败: "+err.Error(), "", "", "")
			if !d.waitRetry(ctx, cfg, 5*time.Second) {
				return
			}
			continue
		}
		d.log.SetLevel(cfg.LogLevel)
		device := d.device(cfg)
		accountName := activeName(cfg)

		if !cfg.Enabled {
			d.setStatus("disabled", "认证已停用", accountName, device, "")
			if !d.waitRetry(ctx, cfg, 5*time.Second) {
				return
			}
			continue
		}

		accounts := cfg.EnabledAccounts()
		d.log.Infof("账号池加载: %d 个可用账号", len(accounts))
		if len(accounts) == 0 {
			d.setStatus("error", "没有可用账号", "", device, "")
			if !d.waitRetry(ctx, cfg, retryInterval(cfg)) {
				return
			}
			continue
		}

		result := d.runAccounts(ctx, cfg, accounts)
		d.record(cfg, result)
		switch result.Kind {
		case supplicant.KindCanceled:
			return
		case supplicant.KindReload:
			continue
		case supplicant.KindPaused:
			switch d.manualPauseLoop(ctx, cfg) {
			case "stop":
				return
			case "logoff":
				if !d.idleLoop(ctx, cfg) {
					return
				}
			}
			continue
		case supplicant.KindLogoff:
			if !d.idleLoop(ctx, cfg) {
				return
			}
			continue
		case supplicant.KindNoServer:
			if cfg.PauseOnHome && d.probe(ctx, cfg.ConnectivityURL) {
				d.setStatus("paused", "未检测到认证服务器，当前网络在线，暂停认证",
					activeName(cfg), device, "")
				switch d.pauseLoop(ctx, cfg) {
				case "stop":
					return
				case "logoff":
					if !d.idleLoop(ctx, cfg) {
						return
					}
				}
				continue
			}
			d.setStatus("retrying", "未找到认证服务器，等待重试", activeName(cfg), device, "")
			if !d.waitRetry(ctx, cfg, retryInterval(cfg)) {
				return
			}
		case supplicant.KindTimeout:
			d.setStatus("retrying", "认证超时，等待重试", activeName(cfg), device, "")
			if !d.waitRetry(ctx, cfg, retryInterval(cfg)) {
				return
			}
		case supplicant.KindTransport:
			message := "网卡/传输错误"
			if result.Err != nil {
				message += ": " + result.Err.Error()
			}
			d.setStatus("error", message, activeName(cfg), device, "")
			if !d.waitRetry(ctx, cfg, retryInterval(cfg)) {
				return
			}
		case supplicant.KindFailure:
			message := "认证失败"
			if len(result.Messages) > 0 {
				message += ": " + strings.Join(result.Messages, " | ")
			}
			if isPolicyFailure(result.Messages) {
				d.setStatus("paused", message+"（等待认证时段）", activeName(cfg), device, result.ServerMAC)
				if !d.waitRetry(ctx, cfg, 30*time.Minute) {
					return
				}
				continue
			}
			d.setStatus("retrying", message+"，等待重试", activeName(cfg), device, result.ServerMAC)
			if !d.waitRetry(ctx, cfg, retryInterval(cfg)) {
				return
			}
		}
	}
}

func (d *runner) runAccounts(ctx context.Context, cfg config.Config, accounts []config.Account) supplicant.Result {
	store, _ := servers.Load(d.serversPath())
	locked := store.LockedMAC()
	device := d.device(cfg)
	var last supplicant.Result

	for index, account := range accounts {
		if ctx.Err() != nil {
			return supplicant.Result{Kind: supplicant.KindCanceled}
		}
		d.setStatus("authenticating", fmt.Sprintf("尝试账号: %s", account.Username),
			account.Username, device, "")
		session := supplicant.New(supplicant.Options{
			Device:         cfg.Device,
			Username:       account.Username,
			Password:       account.Password,
			ServiceName:    cfg.ServiceName,
			StaticID:       cfg.StaticID,
			Timeout:        seconds(cfg.Timeout),
			Retries:        cfg.Retries,
			Repeats:        cfg.Repeats,
			StartTimeout:   seconds(cfg.StartTimeout),
			ReauthInterval: reauthInterval(cfg),
			CheckInterval:  seconds(cfg.CheckInterval),
			Preempt:        cfg.Preempt,
			Autoguard:      cfg.Autoguard,
			LogoffOnStop:   cfg.LogoffOnStop,
			LockedServer:   locked,
			Network: func() protocol.NetworkInfo {
				return network.Collect(cfg.Device)
			},
			Probe: func(ctx context.Context) bool {
				return d.probe(ctx, cfg.ConnectivityURL)
			},
			Status: func(state, message, server string) {
				d.setStatus(state, message, account.Username, device, server)
			},
		}, d.opener)
		result := session.Run(ctx, d.commands)
		last = result
		d.record(cfg, result)

		if result.Kind == supplicant.KindFailure && result.Connected &&
			index+1 < len(accounts) && !isPolicyFailure(result.Messages) {
			d.log.Infof("账号 %s 认证失败，尝试下一个账号", account.Username)
			continue
		}
		return result
	}
	return last
}

func (d *runner) pauseLoop(ctx context.Context, cfg config.Config) string {
	device := d.device(cfg)
	for {
		d.setStatus("paused", "未检测到认证服务器，当前网络在线，暂停认证",
			activeName(cfg), device, "")
		command, ok := d.wait(ctx, 30*time.Second)
		if !ok {
			return "stop"
		}
		switch command {
		case "resume", "auth":
			return "resume"
		case "reload":
			return "reload"
		case "logoff":
			return "logoff"
		case "timeout":
			if !d.probe(ctx, cfg.ConnectivityURL) {
				return "resume"
			}
		}
	}
}

func (d *runner) manualPauseLoop(ctx context.Context, cfg config.Config) string {
	device := d.device(cfg)
	for {
		d.setStatus("paused", "认证已暂停", activeName(cfg), device, "")
		command, ok := d.wait(ctx, 10*time.Second)
		if !ok {
			return "stop"
		}
		switch command {
		case "resume", "auth":
			return "resume"
		case "reload":
			return "reload"
		case "logoff":
			return "logoff"
		}
	}
}

func (d *runner) idleLoop(ctx context.Context, cfg config.Config) bool {
	device := d.device(cfg)
	d.setStatus("idle", "已注销，等待操作", activeName(cfg), device, "")
	for {
		command, ok := d.wait(ctx, 10*time.Second)
		if !ok {
			return false
		}
		switch command {
		case "auth", "resume", "reload":
			return true
		}
	}
}

func (d *runner) waitRetry(ctx context.Context, cfg config.Config, duration time.Duration) bool {
	command, ok := d.wait(ctx, duration)
	if !ok {
		return false
	}
	switch command {
	case "pause", "logoff":
		return d.idleLoop(ctx, cfg)
	default:
		return true
	}
}

func (d *runner) wait(ctx context.Context, duration time.Duration) (string, bool) {
	timer := time.NewTimer(duration)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return "", false
	case command := <-d.commands:
		return command, true
	case <-timer.C:
		return "timeout", true
	}
}

func (d *runner) record(cfg config.Config, result supplicant.Result) {
	if !result.Connected || result.ServerMAC == "" {
		if cfg.DiscoveryCache &&
			(result.Kind == supplicant.KindNoServer || result.Kind == supplicant.KindTransport) {
			_ = servers.ClearDiscovery(d.discoveryPath())
		}
		return
	}
	store, err := servers.Load(d.serversPath())
	if err != nil {
		d.log.Errorf("读取服务器列表失败: %v", err)
		return
	}
	device := d.device(cfg)
	store.Upsert(result.ServerMAC, device, device, result.Authenticated)
	if err := store.Save(d.serversPath()); err != nil {
		d.log.Errorf("写入服务器列表失败: %v", err)
	}
	if cfg.DiscoveryCache {
		if err := servers.SaveDiscovery(d.discoveryPath(), servers.Discovery{
			Interface:   device,
			Description: device,
			MAC:         result.SourceMAC,
			ServerMAC:   result.ServerMAC,
			UpdatedAt:   time.Now().Format(time.RFC3339),
		}); err != nil {
			d.log.Errorf("写入探测缓存失败: %v", err)
		}
	}
}

func (d *runner) probe(ctx context.Context, rawURL string) bool {
	rawURL = strings.TrimSpace(rawURL)
	if rawURL == "" {
		return true
	}
	parsed, err := url.Parse(rawURL)
	if err != nil || parsed.Host == "" {
		return true
	}
	host := parsed.Hostname()
	if host == "localhost" || host == "127.0.0.1" || host == "::1" {
		return true
	}
	port := parsed.Port()
	if port == "" {
		if parsed.Scheme == "https" {
			port = "443"
		} else {
			port = "80"
		}
	}
	dialer := net.Dialer{Timeout: 3 * time.Second}
	conn, err := dialer.DialContext(ctx, "tcp", net.JoinHostPort(host, port))
	if err != nil {
		return false
	}
	_ = conn.Close()
	return true
}

func (d *runner) device(cfg config.Config) string {
	name, err := transport.ResolveDevice(cfg.Device)
	if err != nil {
		return cfg.Device
	}
	return name
}

func (d *runner) setStatus(state, message, account, iface, server string) {
	d.statusMu.Lock()
	err := status.Write(d.paths.Status, status.Snapshot{
		State:     state,
		Message:   message,
		Account:   account,
		Interface: iface,
		Server:    server,
	})
	d.statusMu.Unlock()
	if err != nil {
		d.log.Errorf("写入状态失败: %v", err)
	}
	if state == "error" {
		d.log.Errorf("%s | %s", state, message)
	} else {
		d.log.Infof("%s | %s | %s | %s", state, message, account, server)
	}
}

func (d *runner) serversPath() string {
	return filepath.Join(d.paths.DataDir, servers.ServersFile)
}

func (d *runner) discoveryPath() string {
	return filepath.Join(d.paths.DataDir, servers.DiscoveryFile)
}

func activeName(cfg config.Config) string {
	if account := cfg.ActiveAccount(); account != nil {
		return account.Username
	}
	return ""
}

func isPolicyFailure(messages []string) bool {
	for _, message := range messages {
		if strings.Contains(message, "时段") || strings.Contains(message, "不在认证") {
			return true
		}
	}
	return false
}

func retryInterval(cfg config.Config) time.Duration {
	if cfg.RetryInterval <= 0 {
		return 3 * time.Second
	}
	return seconds(cfg.RetryInterval)
}

func reauthInterval(cfg config.Config) time.Duration {
	if !cfg.Preempt || cfg.ReauthInterval <= 0 {
		return 0
	}
	return time.Duration(cfg.ReauthInterval) * time.Second
}

func seconds(value float64) time.Duration {
	if value <= 0 {
		return 0
	}
	return time.Duration(value * float64(time.Second))
}
