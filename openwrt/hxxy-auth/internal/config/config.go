package config

import (
	"os"
	"strconv"
	"strings"
)

const (
	// DefaultPath is the OpenWrt UCI configuration.
	DefaultPath = "/etc/config/hxxy-auth"
	// MaxAccounts is the account-pool limit.
	MaxAccounts = 5
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
type Config struct {
	Enabled         bool
	Device          string
	ServiceName     string
	StaticID        string
	Timeout         float64
	Retries         int
	Repeats         int
	StartTimeout    float64
	ReauthInterval  int
	CheckInterval   float64
	RetryInterval   float64
	PauseOnHome     bool
	ConnectivityURL string
	Disguise        string
	LogLevel        string
	Autoguard       bool
	Preempt         bool
	DiscoveryCache  bool
	LogoffOnStop    bool
	Accounts        []Account
}

// Default returns the built-in defaults.
func Default() Config {
	return Config{
		Enabled:         true,
		Device:          "wan",
		ServiceName:     "电信宽带服务",
		StaticID:        "Static:AB45A862",
		Timeout:         20,
		Retries:         4,
		Repeats:         3,
		StartTimeout:    3,
		ReauthInterval:  21600,
		CheckInterval:   2,
		RetryInterval:   3,
		PauseOnHome:     true,
		ConnectivityURL: "http://www.baidu.com/",
		Disguise:        "basic",
		LogLevel:        "info",
		Autoguard:       true,
		Preempt:         true,
		DiscoveryCache:  true,
		LogoffOnStop:    false,
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
		case "option":
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
	return cfg, nil
}

func applyAuthOption(cfg *Config, key, value string) {
	switch key {
	case "enabled":
		cfg.Enabled = boolValue(value)
	case "device":
		cfg.Device = value
	case "service_name":
		cfg.ServiceName = value
	case "static_id":
		cfg.StaticID = value
	case "timeout":
		cfg.Timeout = floatValue(value, cfg.Timeout)
	case "retries":
		cfg.Retries = intValue(value, cfg.Retries)
	case "repeats":
		cfg.Repeats = intValue(value, cfg.Repeats)
	case "start_timeout":
		cfg.StartTimeout = floatValue(value, cfg.StartTimeout)
	case "reauth_interval":
		cfg.ReauthInterval = intValue(value, cfg.ReauthInterval)
	case "check_interval":
		cfg.CheckInterval = floatValue(value, cfg.CheckInterval)
	case "retry_interval":
		cfg.RetryInterval = floatValue(value, cfg.RetryInterval)
	case "pause_on_home":
		cfg.PauseOnHome = boolValue(value)
	case "connectivity_url":
		cfg.ConnectivityURL = value
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
