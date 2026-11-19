package netcfg

import (
	"crypto/rand"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"ruijie-auth/internal/protocol"
	"ruijie-auth/internal/transport"
)

// MACInfo describes the configured and effective MAC of a network section.
type MACInfo struct {
	Section    string `json:"section"`
	Device     string `json:"device"`
	Configured string `json:"configured"`
	Effective  string `json:"effective"`
}

// Show reads the UCI override and the current interface MAC.
func Show(section string) (MACInfo, error) {
	section = strings.TrimSpace(section)
	if section == "" {
		section = "wan"
	}
	device := section
	if resolved, err := transport.ResolveDevice(section); err == nil {
		device = resolved
	}
	configured, _ := uciGet(section, "macaddr")
	effective := ""
	if data, err := os.ReadFile("/sys/class/net/" + device + "/address"); err == nil {
		effective = strings.ToLower(strings.TrimSpace(string(data)))
	}
	return MACInfo{
		Section:    section,
		Device:     device,
		Configured: configured,
		Effective:  effective,
	}, nil
}

// Set writes network.<section>.macaddr and brings the interface up.
func Set(section, mac string) error {
	section = strings.TrimSpace(section)
	if section == "" {
		section = "wan"
	}
	normalized := protocol.NormalizeMAC(mac)
	if normalized == "" {
		return fmt.Errorf("invalid MAC address: %s", mac)
	}
	if err := uciSet(section, "macaddr", normalized); err != nil {
		return err
	}
	if err := run("uci", "commit", "network"); err != nil {
		return err
	}
	if err := run("ifup", section); err != nil {
		return err
	}
	return nil
}

// Clear removes the UCI override and brings the interface up.
func Clear(section string) error {
	section = strings.TrimSpace(section)
	if section == "" {
		section = "wan"
	}
	if err := runIgnore("uci", "-q", "delete", "network."+section+".macaddr"); err != nil {
		return err
	}
	if err := run("uci", "commit", "network"); err != nil {
		return err
	}
	return run("ifup", section)
}

// Random returns a locally administered unicast MAC.
func Random() (string, error) {
	buffer := make([]byte, 6)
	if _, err := rand.Read(buffer); err != nil {
		return "", err
	}
	buffer[0] = (buffer[0] & 0xFC) | 0x02
	parts := make([]string, 6)
	for i, value := range buffer {
		parts[i] = fmt.Sprintf("%02x", value)
	}
	return strings.Join(parts, ":"), nil
}

func uciGet(section, option string) (string, error) {
	output, err := exec.Command("uci", "-q", "get", "network."+section+"."+option).Output()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(output)), nil
}

func uciSet(section, option, value string) error {
	return run("uci", "set", "network."+section+"."+option+"="+value)
}

func run(name string, args ...string) error {
	path, err := commandPath(name)
	if err != nil {
		return err
	}
	output, err := exec.Command(path, args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s %s: %v: %s", name, strings.Join(args, " "), err, strings.TrimSpace(string(output)))
	}
	return nil
}

func runIgnore(name string, args ...string) error {
	path, err := commandPath(name)
	if err != nil {
		return err
	}
	_ = exec.Command(path, args...).Run()
	return nil
}

func commandPath(name string) (string, error) {
	if path, err := exec.LookPath(name); err == nil {
		return path, nil
	}
	for _, dir := range []string{"/sbin", "/usr/sbin", "/bin", "/usr/bin"} {
		path := filepath.Join(dir, name)
		if _, err := os.Stat(path); err == nil {
			return path, nil
		}
	}
	return "", fmt.Errorf("%s not found", name)
}
