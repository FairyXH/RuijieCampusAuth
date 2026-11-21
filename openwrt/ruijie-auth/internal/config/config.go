package config

import (
	"os"
	"strconv"
	"strings"
)

const (
	// DefaultPath is the OpenWrt UCI configuration.
	DefaultPath = "/etc/config/ruijie-auth"
	// MaxAccounts is the account-pool limit.
	MaxAccounts = 5
	// AutoDevice tells the daemon to pick the campus WAN interface itself.
	AutoDevice = "auto"
)

// Account is one UCI `config account` section.
type Account struct {
	Section  string
	Username string
	Password string
	Note     string
	Enabled  bool
	Active   bool
}

// Config is the daemon configuration read from UCI.
//
// School-specific values (service name, static ID, school code, endpoints)
// are empty by default and only arrive through a profile import, matching the
// C# UserStore defaults and docs/GENERICIZATION.md.
type Config struct {
	Enabled bool

	// Devices is the ordered candidate list from `option/list device`.
	// `auto` (or an empty list) means: detect the campus WAN port.
	Devices        []string
	AuthMode       string
	ProfileName    string
	ProfilePath    string
	SchoolCode     string
	ServiceName    string
	StaticID       string
	SelfServiceURL string
	UpdateURL      string
	PortalURL      string
	SamHost        string
	SamPort        int
	DHCPMode       string
	DHCPTimeout    float64
	DHCPOption82   bool
	ADDomain       string
	ADTimeout      float64
	EngineVersion  string

	Timeout         float64
	Retries         int
	Repeats         int
	StartTimeout    float64
	ResponseDelay   float64
	ReauthInterval  int
	// ProactiveReauth schedules a re-authentication after the server's
	// interval even while the session is healthy. Off by default: on this
	// campus that timer itself can drop a working session.
	ProactiveReauth bool
	// QuietWindow pauses automatic authentication during the campus
	// maintenance window (default 00:00-06:00), when the auth server is off.
	QuietWindow bool
	QuietStart  int
	QuietEnd    int
	CheckInterval   float64
	RetryInterval   float64
	PauseOnHome     bool
	ConnectivityURL string
	// ConnectivityTargets are host:port TCP probes run in parallel. Any reply
	// (including RST) proves the egress path is alive. The defaults mirror
	// NetworkGuardian's four public DNS targets on TCP 53.
	ConnectivityTargets []string
	// ConnectivityDomain is resolved through every target as a real DNS
	// query before the TCP/HTTP fallbacks. Any successful answer is online.
	ConnectivityDomain string
	// ConnectivityFallbacks are well-known HTTPS sites used only after the
	// DNS quorum and TCP probes both fail.
	ConnectivityFallbacks []string
	ConnectivityTimeout float64
	Disguise            string
	LogLevel            string
	Autoguard           bool
	Preempt             bool
	DiscoveryCache      bool
	LogoffOnStop        bool
	PrivateTail         bool
	LogoffFirst         bool
	Accounts            []Account
}

// Default returns the built-in defaults. No school values are baked in.
func Default() Config {
	return Config{
		Enabled:         true,
		Devices:         []string{AutoDevice},
		AuthMode:        "EAPMD5",
		DHCPMode:        "none",
		DHCPTimeout:     30,
		ADTimeout:       120,
		Timeout:         20,
		Retries:         4,
		Repeats:         3,
		StartTimeout:    3,
		ResponseDelay:   0.04,
		ReauthInterval:  0,
		ProactiveReauth: false,
		QuietWindow:     true,
		QuietStart:      0,
		QuietEnd:        360,
		CheckInterval:   2,
		RetryInterval:   3,
		PauseOnHome:     true,
		ConnectivityURL: "https://www.baidu.com/",
		ConnectivityTargets: []string{
			"223.5.5.5:53",
			"119.29.29.29:53",
			"180.76.76.76:53",
			"114.114.114.114:53",
		},
		ConnectivityDomain: "www.baidu.com",
		ConnectivityFallbacks: []string{
			"https://www.bing.com/",
			"https://www.qq.com/",
		},
		ConnectivityTimeout: 2,
		Disguise:            "basic",
		LogLevel:            "info",
		Autoguard:           true,
		Preempt:             true,
		DiscoveryCache:      true,
		LogoffOnStop:        false,
		PrivateTail:         true,
		LogoffFirst:         true,
	}
}

// Load reads the UCI file; a missing file returns defaults.
func Load(path string) (Config, error) {
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return Default(), nil
	}
	if err != nil {
		return Default(), err
	}
	_ = os.Chmod(path, 0o600)
	return Parse(string(data))
}

// Parse parses the subset of UCI used by this package.
func Parse(data string) (Config, error) {
	cfg := Default()
	cfg.Devices = nil
	cfg.ConnectivityTargets = nil
	cfg.ConnectivityFallbacks = nil
	var current *Account
	authSection := false

	for _, raw := range strings.Split(data, "\n") {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		switch fields[0] {
		case "config":
			typ := fields[1]
			name := ""
			if len(fields) > 2 {
				name = unquote(strings.Join(fields[2:], " "))
			}
			switch typ {
			case "account":
				cfg.Accounts = append(cfg.Accounts, Account{
					Section: name,
					Enabled: true,
				})
				current = &cfg.Accounts[len(cfg.Accounts)-1]
				authSection = false
			case "auth":
				current = nil
				authSection = true
			default:
				current = nil
				authSection = false
			}
		case "option", "list":
			key := fields[1]
			value := ""
			if len(fields) > 2 {
				value = unquote(strings.Join(fields[2:], " "))
			}
			if current != nil {
				applyAccountOption(current, key, value)
			} else if authSection {
				applyAuthOption(&cfg, key, value)
			}
		}
	}
	if len(cfg.Devices) == 0 {
		cfg.Devices = []string{AutoDevice}
	}
	if len(cfg.ConnectivityTargets) == 0 {
		cfg.ConnectivityTargets = defaultConnectivityTargets()
	}
	if len(cfg.ConnectivityFallbacks) == 0 {
		cfg.ConnectivityFallbacks = []string{
			"https://www.bing.com/",
			"https://www.qq.com/",
		}
	}
	return cfg, nil
}

func defaultConnectivityTargets() []string {
	return []string{
		"223.5.5.5:53",
		"119.29.29.29:53",
		"180.76.76.76:53",
		"114.114.114.114:53",
	}
}

func applyAuthOption(cfg *Config, key, value string) {
	switch key {
	case "enabled":
		cfg.Enabled = boolValue(value)
	case "device":
		// `option device 'wan eth0'` and `list device 'eth0'` both work.
		for _, item := range strings.Fields(value) {
			if item != "" {
				cfg.Devices = append(cfg.Devices, item)
			}
		}
	case "auth_mode":
		cfg.AuthMode = value
	case "profile_name":
		cfg.ProfileName = value
	case "profile_path":
		cfg.ProfilePath = value
	case "school_code":
		cfg.SchoolCode = value
	case "service_name":
		cfg.ServiceName = value
	case "static_id":
		cfg.StaticID = value
	case "self_service_url":
		cfg.SelfServiceURL = value
	case "update_url":
		cfg.UpdateURL = value
	case "portal_url":
		cfg.PortalURL = value
	case "sam_host":
		cfg.SamHost = value
	case "sam_port":
		cfg.SamPort = intValue(value, cfg.SamPort)
	case "dhcp_mode":
		cfg.DHCPMode = value
	case "dhcp_timeout":
		cfg.DHCPTimeout = floatValue(value, cfg.DHCPTimeout)
	case "dhcp_option82":
		cfg.DHCPOption82 = boolValue(value)
	case "ad_domain":
		cfg.ADDomain = value
	case "ad_timeout":
		cfg.ADTimeout = floatValue(value, cfg.ADTimeout)
	case "engine_version":
		cfg.EngineVersion = value
	case "timeout":
		cfg.Timeout = floatValue(value, cfg.Timeout)
	case "retries":
		cfg.Retries = intValue(value, cfg.Retries)
	case "repeats":
		cfg.Repeats = intValue(value, cfg.Repeats)
	case "start_timeout":
		cfg.StartTimeout = floatValue(value, cfg.StartTimeout)
	case "response_delay":
		cfg.ResponseDelay = floatValue(value, cfg.ResponseDelay)
	case "reauth_interval":
		cfg.ReauthInterval = intValue(value, cfg.ReauthInterval)
	case "proactive_reauth":
		cfg.ProactiveReauth = boolValue(value)
	case "quiet_window":
		cfg.QuietWindow = boolValue(value)
	case "quiet_start":
		cfg.QuietStart = intValue(value, cfg.QuietStart)
	case "quiet_end":
		cfg.QuietEnd = intValue(value, cfg.QuietEnd)
	case "check_interval":
		cfg.CheckInterval = floatValue(value, cfg.CheckInterval)
	case "retry_interval":
		cfg.RetryInterval = floatValue(value, cfg.RetryInterval)
	case "pause_on_home":
		cfg.PauseOnHome = boolValue(value)
	case "connectivity_url":
		cfg.ConnectivityURL = value
	case "connectivity_domain":
		cfg.ConnectivityDomain = value
	case "connectivity_fallbacks":
		for _, item := range strings.FieldsFunc(value, func(r rune) bool {
			return r == ',' || r == ' ' || r == '\t'
		}) {
			if item != "" {
				cfg.ConnectivityFallbacks = append(cfg.ConnectivityFallbacks, item)
			}
		}
	case "connectivity_targets":
		for _, item := range strings.FieldsFunc(value, func(r rune) bool {
			return r == ',' || r == ' ' || r == '\t'
		}) {
			if item != "" {
				cfg.ConnectivityTargets = append(cfg.ConnectivityTargets, item)
			}
		}
	case "connectivity_timeout":
		cfg.ConnectivityTimeout = floatValue(value, cfg.ConnectivityTimeout)
	case "disguise":
		cfg.Disguise = value
	case "log_level":
		cfg.LogLevel = value
	case "autoguard":
		cfg.Autoguard = boolValue(value)
	case "preempt":
		cfg.Preempt = boolValue(value)
	case "discovery_cache":
		cfg.DiscoveryCache = boolValue(value)
	case "logoff_on_stop":
		cfg.LogoffOnStop = boolValue(value)
	case "private_tail":
		cfg.PrivateTail = boolValue(value)
	case "logoff_first":
		cfg.LogoffFirst = boolValue(value)
	}
}

func applyAccountOption(account *Account, key, value string) {
	switch key {
	case "username":
		account.Username = value
	case "password":
		account.Password = value
	case "note":
		account.Note = value
	case "enabled":
		account.Enabled = boolValue(value)
	case "active":
		account.Active = boolValue(value)
	}
}

// DeviceList returns the ordered candidate list, defaulting to auto-detect.
func (c Config) DeviceList() []string {
	if len(c.Devices) == 0 {
		return []string{AutoDevice}
	}
	return c.Devices
}

// ActiveAccount returns the first enabled active account, or the first
// enabled account.
func (c Config) ActiveAccount() *Account {
	for i := range c.Accounts {
		if c.Accounts[i].Enabled && c.Accounts[i].Active &&
			c.Accounts[i].Username != "" && c.Accounts[i].Password != "" {
			return &c.Accounts[i]
		}
	}
	for i := range c.Accounts {
		if c.Accounts[i].Enabled &&
			c.Accounts[i].Username != "" && c.Accounts[i].Password != "" {
			return &c.Accounts[i]
		}
	}
	return nil
}

// EnabledAccounts returns enabled non-empty accounts, active first, capped at
// MaxAccounts.
func (c Config) EnabledAccounts() []Account {
	active := c.ActiveAccount()
	out := make([]Account, 0, MaxAccounts)
	if active != nil {
		out = append(out, *active)
	}
	for _, account := range c.Accounts {
		if len(out) >= MaxAccounts {
			break
		}
		if !account.Enabled || account.Username == "" || account.Password == "" {
			continue
		}
		if active != nil && account.Section == active.Section {
			continue
		}
		out = append(out, account)
	}
	return out
}

func unquote(value string) string {
	value = strings.TrimSpace(value)
	if len(value) >= 2 {
		if (value[0] == '\'' && value[len(value)-1] == '\'') ||
			(value[0] == '"' && value[len(value)-1] == '"') {
			value = value[1 : len(value)-1]
		}
	}
	value = strings.ReplaceAll(value, `'\''`, `'`)
	return value
}

func boolValue(value string) bool {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "1", "true", "yes", "on":
		return true
	default:
		return false
	}
}

func floatValue(value string, fallback float64) float64 {
	parsed, err := strconv.ParseFloat(strings.TrimSpace(value), 64)
	if err != nil {
		return fallback
	}
	return parsed
}

func intValue(value string, fallback int) int {
	parsed, err := strconv.Atoi(strings.TrimSpace(value))
	if err != nil {
		return fallback
	}
	return parsed
}
