package config

import "testing"

func TestProbeAndQuietDefaults(t *testing.T) {
	cfg := Default()
	if cfg.ProactiveReauth {
		t.Fatal("proactive reauth must be off by default")
	}
	if cfg.ReauthInterval != 0 {
		t.Fatalf("reauth interval default = %d, want 0", cfg.ReauthInterval)
	}
	if !cfg.QuietWindow || cfg.QuietStart != 0 || cfg.QuietEnd != 360 {
		t.Fatalf("quiet window default = %v %d-%d, want true 0-360",
			cfg.QuietWindow, cfg.QuietStart, cfg.QuietEnd)
	}
	if cfg.ConnectivityDomain != "www.baidu.com" {
		t.Fatalf("connectivity domain = %q", cfg.ConnectivityDomain)
	}
	if len(cfg.ConnectivityFallbacks) < 2 {
		t.Fatalf("fallback count = %d, want at least 2", len(cfg.ConnectivityFallbacks))
	}
}

func TestParseProbeAndQuiet(t *testing.T) {
	cfg, err := Parse(`
config auth 'main'
	option proactive_reauth '1'
	option reauth_interval '7200'
	option quiet_window '0'
	option quiet_start '30'
	option quiet_end '420'
	option connectivity_domain 'www.qq.com'
	option connectivity_url 'https://www.baidu.com/'
	list connectivity_fallbacks 'https://www.bing.com/'
	list connectivity_fallbacks 'https://www.qq.com/'
	list connectivity_targets '223.5.5.5:53'
	list connectivity_targets '119.29.29.29:53'
`)
	if err != nil {
		t.Fatal(err)
	}
	if !cfg.ProactiveReauth || cfg.ReauthInterval != 7200 {
		t.Fatalf("proactive=%v interval=%d", cfg.ProactiveReauth, cfg.ReauthInterval)
	}
	if cfg.QuietWindow || cfg.QuietStart != 30 || cfg.QuietEnd != 420 {
		t.Fatalf("quiet=%v %d-%d", cfg.QuietWindow, cfg.QuietStart, cfg.QuietEnd)
	}
	if cfg.ConnectivityDomain != "www.qq.com" {
		t.Fatalf("domain=%q", cfg.ConnectivityDomain)
	}
	if len(cfg.ConnectivityFallbacks) != 2 {
		t.Fatalf("fallbacks=%v", cfg.ConnectivityFallbacks)
	}
	if len(cfg.ConnectivityTargets) != 2 {
		t.Fatalf("targets=%v", cfg.ConnectivityTargets)
	}
}
