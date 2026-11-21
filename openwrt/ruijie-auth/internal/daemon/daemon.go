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

	"ruijie-auth/internal/config"
	"ruijie-auth/internal/connectivity"
	"ruijie-auth/internal/control"
	"ruijie-auth/internal/logging"
	"ruijie-auth/internal/netcfg"
	"ruijie-auth/internal/network"
	"ruijie-auth/internal/protocol"
	"ruijie-auth/internal/servers"
	"ruijie-auth/internal/status"
	"ruijie-auth/internal/supplicant"
	"ruijie-auth/internal/transport"
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
		DataDir: "/etc/ruijie-auth",
		Log:     "/var/log/ruijie-auth.log",
	}
}

type runner struct {
	paths    Paths
	opener   transport.Opener
	log      *logging.Logger
	commands chan string
	statusMu sync.Mutex

	// Context copied into every status write.
	authMode    string
	profileName string
	devices     []string
	properties  []status.Property
	messages    []string
	reauth      int
	reauthSet   bool
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
		device, candidates := d.resolveDevice(cfg)
		d.setContext(cfg, candidates)
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

		// Campus authentication is closed 00:00-06:00 by default. Do not
		// hammer the server during that window; resume automatically when it
		// ends, or immediately on a manual auth/resume command.
		if d.inQuietWindow(cfg) {
			switch d.quietLoop(ctx, cfg, device) {
			case "stop":
				return
			case "reload":
				continue
			case "logoff":
				if !d.idleLoop(ctx, cfg) {
					return
				}
				continue
			}
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
			if cfg.PauseOnHome && d.probe(ctx, cfg, device) {
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
	device, _ := d.resolveDevice(cfg)
	var last supplicant.Result

	for index, account := range accounts {
		if ctx.Err() != nil {
			return supplicant.Result{Kind: supplicant.KindCanceled}
		}
		d.setStatus("authenticating", fmt.Sprintf("尝试账号: %s", account.Username),
			account.Username, device, "")
		session := supplicant.New(supplicant.Options{
			Device:         device,
			Username:       account.Username,
			Password:       account.Password,
			AuthMode:       cfg.AuthMode,
			ServiceName:    cfg.ServiceName,
			StaticID:       cfg.StaticID,
			Timeout:        seconds(cfg.Timeout),
			Retries:        cfg.Retries,
			Repeats:        cfg.Repeats,
			StartTimeout:   seconds(cfg.StartTimeout),
			ResponseDelay:  seconds(cfg.ResponseDelay),
			ReauthInterval: reauthInterval(cfg),
			ProactiveReauth: cfg.ProactiveReauth,
			CheckInterval:  seconds(cfg.CheckInterval),
			DisableTail:    !cfg.PrivateTail,
			NoLogoffFirst:  !cfg.LogoffFirst,
			Preempt:        cfg.Preempt,
			Autoguard:      cfg.Autoguard,
			LogoffOnStop:   cfg.LogoffOnStop,
			LockedServer:   locked,
			Network: func() protocol.NetworkInfo {
				return network.Collect(device)
			},
			Probe: func(ctx context.Context) bool {
				return d.probe(ctx, cfg, device)
			},
			Status: func(state, message, server string) {
				d.setStatus(state, message, account.Username, device, server)
			},
			Log: func(message string) {
				d.log.Infof("%s", message)
			},
		}, d.opener)
		result := session.Run(ctx, d.commands)
		last = result
		d.setResult(result)
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
	device, _ := d.resolveDevice(cfg)
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
			if !d.probe(ctx, cfg, device) {
				return "resume"
			}
		}
	}
}

func (d *runner) manualPauseLoop(ctx context.Context, cfg config.Config) string {
	device, _ := d.resolveDevice(cfg)
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
	device, _ := d.resolveDevice(cfg)
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
	device, _ := d.resolveDevice(cfg)
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

func (d *runner) probe(ctx context.Context, cfg config.Config, device string) bool {
	timeout := seconds(cfg.ConnectivityTimeout)
	if timeout <= 0 {
		timeout = 2 * time.Second
	}
	if len(cfg.ConnectivityTargets) > 0 {
		// Real DNS queries and TCP 53 handshakes run concurrently; either
		// proves the campus egress works. The TCP path catches networks that
		// filter UDP/53 but still answer TCP.
		fast := make(chan bool, 2)
		go func() {
			fast <- connectivity.DNSOn(ctx, device, cfg.ConnectivityTargets, cfg.ConnectivityDomain, timeout)
		}()
		go func() {
			fast <- connectivity.TCPOn(ctx, device, cfg.ConnectivityTargets, timeout)
		}()
		for i := 0; i < 2; i++ {
			if <-fast {
				return true
			}
		}

		// Second confirmation through well-known sites. Any one success is
		// online; a captive portal or total blackhole fails all of them.
		urls := make([]string, 0, len(cfg.ConnectivityFallbacks)+1)
		if isHTTPProbeURL(cfg.ConnectivityURL) {
			urls = append(urls, cfg.ConnectivityURL)
		}
		urls = append(urls, cfg.ConnectivityFallbacks...)
		return connectivity.HTTPAnyOn(ctx, device, urls, timeout)
	}
	return d.probeLegacy(ctx, cfg.ConnectivityURL)
}

func isHTTPProbeURL(raw string) bool {
	raw = strings.TrimSpace(raw)
	return strings.HasPrefix(raw, "http://") || strings.HasPrefix(raw, "https://")
}

func (d *runner) probeLegacy(ctx context.Context, rawURL string) bool {
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

// resolveDevice maps the configured candidate list (possibly `auto`) onto
// real netdevs, preferring one with carrier. The list is re-resolved on every
// retry cycle so hotplug and MAC changes need no restart.
func (d *runner) resolveDevice(cfg config.Config) (string, []string) {
	configured := cfg.DeviceList()
	candidates := netcfg.ResolveCandidates(configured)
	if len(candidates) == 0 {
		return configured[0], configured
	}
	names := make([]string, 0, len(candidates))
	chosen := candidates[0]
	foundCarrier := false
	for _, candidate := range candidates {
		names = append(names, candidate.Name)
		if candidate.Carrier && !foundCarrier {
			chosen = candidate
			foundCarrier = true
		}
	}
	device := chosen.Name
	if resolved, err := transport.ResolveDevice(device); err == nil {
		device = resolved
	}
	return device, names
}

// setContext copies the profile/mode/device context into every status write.
func (d *runner) setContext(cfg config.Config, devices []string) {
	d.statusMu.Lock()
	d.authMode = cfg.AuthMode
	d.profileName = cfg.ProfileName
	d.devices = devices
	d.statusMu.Unlock()
}

// setResult stores the server properties and messages from the last session.
func (d *runner) setResult(result supplicant.Result) {
	d.statusMu.Lock()
	defer d.statusMu.Unlock()
	if result.Properties != nil {
		properties := make([]status.Property, 0, len(result.Properties.Properties))
		for _, property := range result.Properties.Properties {
			properties = append(properties, status.Property{
				Name:  property.Name,
				Type:  int(property.Type),
				Value: property.DisplayValue(),
				Hex:   property.Hex,
			})
		}
		d.properties = properties
	}
	if len(result.Messages) > 0 {
		d.messages = result.Messages
	}
	if !cfg.ProactiveReauth {
		d.reauth = 0
	} else if result.ReauthInterval > 0 {
		d.reauth = int(result.ReauthInterval / time.Second)
	} else if result.Properties != nil {
		d.reauth = 0
	}
}

func (d *runner) setStatus(state, message, account, iface, server string) {
	d.statusMu.Lock()
	err := status.Write(d.paths.Status, status.Snapshot{
		State:          state,
		Message:        message,
		Account:        account,
		Interface:      iface,
		Devices:        d.devices,
		Server:         server,
		AuthMode:       d.authMode,
		Profile:        d.profileName,
		ReauthInterval: d.reauth,
		Messages:       d.messages,
		Properties:     d.properties,
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
	if !cfg.ProactiveReauth || !cfg.Preempt || cfg.ReauthInterval <= 0 {
		return 0
	}
	return time.Duration(cfg.ReauthInterval) * time.Second
}

// inQuietWindow reports whether the campus auth-off window is active.
// Minutes are local time; a start greater than end wraps midnight.
func (d *runner) inQuietWindow(cfg config.Config) bool {
	if !cfg.QuietWindow || cfg.QuietStart == cfg.QuietEnd {
		return false
	}
	now := time.Now()
	minute := now.Hour()*60 + now.Minute()
	start := ((cfg.QuietStart % 1440) + 1440) % 1440
	end := ((cfg.QuietEnd % 1440) + 1440) % 1440
	if start < end {
		return minute >= start && minute < end
	}
	return minute >= start || minute < end
}

// quietLoop waits out the auth-off window while still accepting control
// commands. A manual auth/resume overrides the window for this attempt.
func (d *runner) quietLoop(ctx context.Context, cfg config.Config, device string) string {
	for {
		d.setStatus("paused", fmt.Sprintf("校园网认证关闭时段（%s-%s），暂停认证",
			quietClock(cfg.QuietStart), quietClock(cfg.QuietEnd)),
			activeName(cfg), device, "")
		command, ok := d.wait(ctx, 30*time.Second)
		if !ok {
			return "stop"
		}
		switch command {
		case "auth", "resume":
			return "auth"
		case "reload":
			return "reload"
		case "logoff", "pause":
			return "logoff"
		}
		if !d.inQuietWindow(cfg) {
			return "auth"
		}
	}
}

func quietClock(minutes int) string {
	minutes = ((minutes % 1440) + 1440) % 1440
	return fmt.Sprintf("%02d:%02d", minutes/60, minutes%60)
}

func seconds(value float64) time.Duration {
	if value <= 0 {
		return 0
	}
	return time.Duration(value * float64(time.Second))
}
