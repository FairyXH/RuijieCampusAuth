package profile

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// Profile is one school configuration file. The JSON schema is identical to
// the C# SchoolProfile / profiles/*.json so one file works on both the
// desktop client and this router build.
type Profile struct {
	Name           string  `json:"name"`
	SchoolCode     string  `json:"school_code"`
	AuthMode       string  `json:"auth_mode"`
	ServiceName    string  `json:"service_name"`
	StaticID       string  `json:"static_id"`
	SelfServiceURL string  `json:"self_service_url"`
	UpdateURL      string  `json:"update_url"`
	PortalURL      string  `json:"portal_url"`
	SamHost        string  `json:"sam_host"`
	SamPort        int     `json:"sam_port"`
	DHCPMode       string  `json:"dhcp_mode"`
	DHCPTimeout    float64 `json:"dhcp_timeout"`
	DHCPOption82   bool    `json:"dhcp_option82"`
	ADDomain       string  `json:"ad_domain"`
	ADTimeout      float64 `json:"ad_timeout"`
	EngineVersion  string  `json:"engine_version"`
	Notes          string  `json:"notes"`

	// Extra keeps unknown fields so a newer profile format round-trips.
	Extra map[string]json.RawMessage `json:"-"`
}

var knownFields = []string{
	"name", "school_code", "auth_mode", "service_name", "static_id",
	"self_service_url", "update_url", "portal_url", "sam_host", "sam_port",
	"dhcp_mode", "dhcp_timeout", "dhcp_option82", "ad_domain", "ad_timeout",
	"engine_version", "notes",
}

// Parse decodes a profile document.
func Parse(data []byte, fallbackName string) (Profile, error) {
	var parsed Profile
	if err := json.Unmarshal(data, &parsed); err != nil {
		return Profile{}, fmt.Errorf("档案 JSON 解析失败: %w", err)
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(data, &raw); err == nil {
		known := map[string]bool{}
		for _, name := range knownFields {
			known[name] = true
		}
		extra := map[string]json.RawMessage{}
		for key, value := range raw {
			if !known[key] {
				extra[key] = value
			}
		}
		if len(extra) > 0 {
			parsed.Extra = extra
		}
	}
	if strings.TrimSpace(parsed.Name) == "" {
		parsed.Name = fallbackName
	}
	return parsed, nil
}

// Load reads one profile file.
func Load(path string) (Profile, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return Profile{}, err
	}
	fallback := strings.TrimSuffix(filepath.Base(path), filepath.Ext(path))
	return Parse(data, fallback)
}

// List returns the profile files in a directory, sorted.
func List(directory string) []string {
	entries, err := os.ReadDir(directory)
	if err != nil {
		return nil
	}
	var files []string
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(strings.ToLower(entry.Name()), ".json") {
			continue
		}
		files = append(files, filepath.Join(directory, entry.Name()))
	}
	sort.Strings(files)
	return files
}

// ImportResult mirrors the C# ProfileImportResult.
type ImportResult struct {
	ProfileName string   `json:"profile_name"`
	ConfigPath  string   `json:"config_path"`
	BackupPath  string   `json:"backup_path,omitempty"`
	Applied     []string `json:"applied"`
}

// Import rewrites the `config auth` section of the UCI file with the profile
// values, leaving accounts and unrelated sections untouched. A timestamped
// backup is written first, like the C# ProfileStore.Import.
func Import(p Profile, configPath, profilePath string) (ImportResult, error) {
	if strings.TrimSpace(configPath) == "" {
		return ImportResult{}, errors.New("配置路径为空")
	}
	existing, err := os.ReadFile(configPath)
	if err != nil && !os.IsNotExist(err) {
		return ImportResult{}, err
	}
	result := ImportResult{
		ProfileName: p.Name,
		ConfigPath:  configPath,
		Applied:     []string{},
	}
	if err == nil {
		backup := fmt.Sprintf("%s.bak-%s", configPath, time.Now().Format("20060102150405"))
		if copyErr := os.WriteFile(backup, existing, 0o600); copyErr == nil {
			result.BackupPath = backup
		}
	}

	updates := orderedUpdates(p, profilePath)
	content := string(existing)
	if strings.TrimSpace(content) == "" {
		content = "config auth 'main'\n"
	}
	updated := setAuthOptions(content, updates)
	if err := os.MkdirAll(filepath.Dir(configPath), 0o755); err != nil {
		return result, err
	}
	if err := os.WriteFile(configPath, []byte(updated), 0o600); err != nil {
		return result, err
	}
	for _, item := range updates {
		result.Applied = append(result.Applied, item.key)
	}
	return result, nil
}

type update struct {
	key   string
	value string
}

// orderedUpdates builds the same field set, in the same order, that the C#
// profile importer applies.
func orderedUpdates(p Profile, profilePath string) []update {
	updates := []update{
		{"profile_name", p.Name},
		{"auth_mode", defaultAuthMode(p.AuthMode)},
	}
	if strings.TrimSpace(profilePath) != "" {
		updates = append(updates, update{"profile_path", strings.TrimSpace(profilePath)})
	}
	add := func(key, value string) {
		if strings.TrimSpace(value) == "" {
			return
		}
		updates = append(updates, update{key, strings.TrimSpace(value)})
	}
	add("school_code", p.SchoolCode)
	add("service_name", p.ServiceName)
	add("static_id", p.StaticID)
	add("self_service_url", p.SelfServiceURL)
	add("update_url", p.UpdateURL)
	add("portal_url", p.PortalURL)
	add("sam_host", p.SamHost)
	if p.SamPort > 0 {
		updates = append(updates, update{"sam_port", fmt.Sprintf("%d", p.SamPort)})
	}
	switch strings.ToLower(strings.TrimSpace(p.DHCPMode)) {
	case "before", "after", "none":
		updates = append(updates, update{"dhcp_mode", strings.ToLower(strings.TrimSpace(p.DHCPMode))})
	}
	if p.DHCPTimeout > 0 {
		updates = append(updates, update{"dhcp_timeout", trimFloat(p.DHCPTimeout)})
	}
	updates = append(updates, update{"dhcp_option82", boolText(p.DHCPOption82)})
	add("ad_domain", p.ADDomain)
	if p.ADTimeout > 0 {
		updates = append(updates, update{"ad_timeout", trimFloat(p.ADTimeout)})
	}
	add("engine_version", p.EngineVersion)
	return updates
}

func defaultAuthMode(value string) string {
	trimmed := strings.TrimSpace(value)
	if trimmed == "" {
		return "EAPMD5"
	}
	return trimmed
}

func boolText(value bool) string {
	if value {
		return "1"
	}
	return "0"
}

func trimFloat(value float64) string {
	text := fmt.Sprintf("%.2f", value)
	text = strings.TrimRight(text, "0")
	return strings.TrimRight(text, ".")
}

// setAuthOptions rewrites option lines inside the first `config auth`
// section, preserving comments, accounts, and unknown options.
func setAuthOptions(content string, updates []update) string {
	lines := strings.Split(content, "\n")
	start := -1
	end := len(lines)
	for i, line := range lines {
		fields := strings.Fields(strings.TrimSpace(line))
		if len(fields) >= 2 && fields[0] == "config" && fields[1] == "auth" {
			start = i
			for j := i + 1; j < len(lines); j++ {
				next := strings.Fields(strings.TrimSpace(lines[j]))
				if len(next) >= 2 && next[0] == "config" {
					end = j
					break
				}
			}
			break
		}
	}
	if start < 0 {
		if len(lines) > 0 && strings.TrimSpace(lines[len(lines)-1]) != "" {
			lines = append(lines, "")
		}
		lines = append(lines, "config auth 'main'")
		start = len(lines) - 1
		end = len(lines)
	}

	for _, item := range updates {
		replaced := false
		for i := start + 1; i < end; i++ {
			fields := strings.Fields(strings.TrimSpace(lines[i]))
			if len(fields) < 2 {
				continue
			}
			if fields[0] != "option" && fields[0] != "list" {
				continue
			}
			if fields[1] != item.key {
				continue
			}
			lines[i] = "\toption " + item.key + " " + quoteUCI(item.value)
			replaced = true
			break
		}
		if replaced {
			continue
		}
		newLine := "\toption " + item.key + " " + quoteUCI(item.value)
		lines = append(lines, "")
		copy(lines[start+2:], lines[start+1:])
		lines[start+1] = newLine
		start++
		end++
	}
	return strings.Join(lines, "\n")
}

func quoteUCI(value string) string {
	return "'" + strings.ReplaceAll(value, "'", `'\''`) + "'"
}
