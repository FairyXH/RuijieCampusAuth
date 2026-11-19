package profile

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const sampleProfile = `{
  "name": "示例大学",
  "school_code": "DEMO",
  "auth_mode": "PEAP",
  "service_name": "校园宽带",
  "static_id": "Static:00112233",
  "self_service_url": "http://self.example/",
  "update_url": "http://update.example/",
  "portal_url": "",
  "sam_host": "",
  "sam_port": 0,
  "dhcp_mode": "before",
  "dhcp_timeout": 45,
  "dhcp_option82": true,
  "ad_domain": "",
  "ad_timeout": 120,
  "engine_version": "5.0.2014.108",
  "notes": "unit test",
  "future_field": {"x": 1}
}`

func TestParseProfileSchema(t *testing.T) {
	parsed, err := Parse([]byte(sampleProfile), "fallback")
	if err != nil {
		t.Fatal(err)
	}
	if parsed.Name != "示例大学" || parsed.AuthMode != "PEAP" ||
		parsed.ServiceName != "校园宽带" || parsed.StaticID != "Static:00112233" {
		t.Fatalf("profile = %+v", parsed)
	}
	if parsed.DHCPMode != "before" || parsed.DHCPTimeout != 45 || !parsed.DHCPOption82 {
		t.Fatalf("dhcp fields = %+v", parsed)
	}
	if parsed.SamPort != 0 || parsed.ADDomain != "" {
		t.Fatalf("empty fields = %+v", parsed)
	}
	if parsed.Extra["future_field"] == nil {
		t.Fatalf("unknown field was dropped: %+v", parsed.Extra)
	}
}

func TestParseProfileDefaultsName(t *testing.T) {
	parsed, err := Parse([]byte(`{"service_name":"x"}`), "from-file")
	if err != nil {
		t.Fatal(err)
	}
	if parsed.Name != "from-file" {
		t.Fatalf("name = %q", parsed.Name)
	}
}

func TestImportKeepsAccountsAndOtherSections(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "ruijie-auth")
	original := `config auth 'main'
	option enabled '1'
	option device 'auto'
	option service_name 'old service'
	option static_id 'old id'
	option custom_unknown 'keep me'

config account 'a1'
	option username 'user1'
	option password 'pass1'
	option enabled '1'
	option active '1'
`
	if err := os.WriteFile(path, []byte(original), 0o600); err != nil {
		t.Fatal(err)
	}

	loaded, err := Parse([]byte(sampleProfile), "sample")
	if err != nil {
		t.Fatal(err)
	}
	result, err := Import(loaded, path, "/tmp/sample.json")
	if err != nil {
		t.Fatal(err)
	}
	if result.BackupPath == "" {
		t.Fatal("expected a backup")
	}
	if _, err := os.Stat(result.BackupPath); err != nil {
		t.Fatalf("backup missing: %v", err)
	}

	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	text := string(data)
	for _, want := range []string{
		"option profile_name '示例大学'",
		"option auth_mode 'PEAP'",
		"option service_name '校园宽带'",
		"option static_id 'Static:00112233'",
		"option dhcp_mode 'before'",
		"option dhcp_timeout '45'",
		"option dhcp_option82 '1'",
		"option engine_version '5.0.2014.108'",
		"option profile_path '/tmp/sample.json'",
	} {
		if !strings.Contains(text, want) {
			t.Fatalf("missing %q in:\n%s", want, text)
		}
	}
	if strings.Contains(text, "old service") || strings.Contains(text, "old id") {
		t.Fatalf("old values survived:\n%s", text)
	}
	if !strings.Contains(text, "option custom_unknown 'keep me'") {
		t.Fatalf("unknown option was dropped:\n%s", text)
	}
	if !strings.Contains(text, "config account 'a1'") ||
		!strings.Contains(text, "option username 'user1'") {
		t.Fatalf("account section was damaged:\n%s", text)
	}
	if strings.Contains(text, "option portal_url") {
		t.Fatalf("empty profile field should not be written:\n%s", text)
	}
}

func TestImportCreatesSectionWhenMissing(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "missing")
	loaded, err := Parse([]byte(`{"name":"X","auth_mode":"EAPMD5"}`), "x")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := Import(loaded, path, ""); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(data), "config auth 'main'") ||
		!strings.Contains(string(data), "option profile_name 'X'") {
		t.Fatalf("generated config:\n%s", data)
	}
}

func TestImportQuotesSingleQuotes(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cfg")
	loaded := Profile{Name: "O'Brien", AuthMode: "EAPMD5", ServiceName: "it's"}
	if _, err := Import(loaded, path, ""); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(path)
	if !strings.Contains(string(data), `O'\''Brien`) {
		t.Fatalf("quote escaping wrong:\n%s", data)
	}
}

func TestImportResultSerialises(t *testing.T) {
	result := ImportResult{ProfileName: "x", Applied: []string{"a"}}
	if _, err := json.Marshal(result); err != nil {
		t.Fatal(err)
	}
}
