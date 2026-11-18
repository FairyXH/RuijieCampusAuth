package config

import (
	"path/filepath"
	"testing"
)

func TestParseAuthAndAccounts(t *testing.T) {
	data := `
config auth 'main'
	option enabled '1'
	option device 'wan'
	option service_name '电信宽带服务'
	option static_id 'Static:AB45A862'
	option timeout '25'
	option retries '5'
	option repeats '2'
	option start_timeout '4'
	option reauth_interval '7200'
	option check_interval '3'
	option retry_interval '6'
	option pause_on_home '0'
	option connectivity_url 'http://example.com/'
	option disguise 'full'
	option log_level 'debug'
	option autoguard '1'
	option preempt '1'
	option discovery_cache '0'
	option logoff_on_stop '1'

config account 'a1'
	option username 'user1'
	option password 'pass1'
	option note '主账号'
	option enabled '1'
	option active '1'

config account 'a2'
	option username 'user2'
	option password 'pass2'
	option enabled '0'
`
	cfg, err := Parse(data)
	if err != nil {
		t.Fatal(err)
	}
	if !cfg.Enabled || cfg.Device != "wan" || cfg.ServiceName != "电信宽带服务" {
		t.Fatalf("auth fields not parsed: %+v", cfg)
	}
	if cfg.Timeout != 25 || cfg.Retries != 5 || cfg.Repeats != 2 || cfg.StartTimeout != 4 {
		t.Fatalf("timeouts not parsed: %+v", cfg)
	}
	if cfg.ReauthInterval != 7200 || cfg.CheckInterval != 3 || cfg.RetryInterval != 6 {
		t.Fatalf("intervals not parsed: %+v", cfg)
	}
	if cfg.PauseOnHome || cfg.Autoguard == false || cfg.Preempt == false ||
		cfg.DiscoveryCache || !cfg.LogoffOnStop {
		t.Fatalf("switches not parsed: %+v", cfg)
	}
	if len(cfg.Accounts) != 2 {
		t.Fatalf("accounts = %d, want 2", len(cfg.Accounts))
	}
	if cfg.Accounts[0].Section != "a1" || cfg.Accounts[0].Note != "主账号" || !cfg.Accounts[0].Active {
		t.Fatalf("account a1 = %+v", cfg.Accounts[0])
	}
	if cfg.Accounts[1].Enabled {
		t.Fatalf("account a2 should be disabled: %+v", cfg.Accounts[1])
	}
	active := cfg.ActiveAccount()
	if active == nil || active.Username != "user1" {
		t.Fatalf("active account = %+v", active)
	}
	enabled := cfg.EnabledAccounts()
	if len(enabled) != 1 || enabled[0].Username != "user1" {
		t.Fatalf("enabled accounts = %+v", enabled)
	}
}

func TestEnabledAccountsCapsAtFive(t *testing.T) {
	cfg := Default()
	for i := 0; i < 6; i++ {
		cfg.Accounts = append(cfg.Accounts, Account{
			Section:  "a" + string(rune('0'+i)),
			Username: "user" + string(rune('0'+i)),
			Password: "pass",
			Enabled:  true,
			Active:   i == 5,
		})
	}
	accounts := cfg.EnabledAccounts()
	if len(accounts) != MaxAccounts {
		t.Fatalf("enabled accounts = %d, want %d", len(accounts), MaxAccounts)
	}
	if accounts[0].Username != "user5" {
		t.Fatalf("active account should be first, got %q", accounts[0].Username)
	}
}

func TestLoadMissingUsesDefaults(t *testing.T) {
	cfg, err := Load(filepath.Join(t.TempDir(), "missing"))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Device != "wan" || !cfg.Autoguard || !cfg.Preempt {
		t.Fatalf("unexpected defaults: %+v", cfg)
	}
}
