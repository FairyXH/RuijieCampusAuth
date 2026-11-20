package netcfg

import (
	"encoding/json"
	"net"
	"os"
	"os/exec"
	"sort"
	"strings"
)

// InterfaceInfo is one selectable campus-WAN candidate. The daemon resolves
// the configured list (which may contain logical UCI names or netdevs) into
// these records on every retry cycle, so hotplug and MAC changes are picked
// up without a restart.
type InterfaceInfo struct {
	Name        string `json:"name"`
	Logical     string `json:"logical"`
	MAC         string `json:"mac"`
	Carrier     bool   `json:"carrier"`
	IsWAN       bool   `json:"is_wan"`
	Description string `json:"description"`
}

type ubusInterface struct {
	Interface string `json:"interface"`
	Up        bool   `json:"up"`
	L3Device  string `json:"l3_device"`
	Device    string `json:"device"`
	Proto     string `json:"proto"`
}

type ubusDump struct {
	Interface []ubusInterface `json:"interface"`
}

// Interfaces enumerates usable EAPOL candidates. Campus-WAN interfaces come
// first, then other carrier-up devices.
func Interfaces() []InterfaceInfo {
	logicalMap := map[string]string{}
	wanSet := map[string]bool{}
	if output, err := exec.Command("ubus", "call", "network.interface", "dump").Output(); err == nil {
		var dump ubusDump
		if json.Unmarshal(output, &dump) == nil {
			for _, item := range dump.Interface {
				device := item.L3Device
				if device == "" {
					device = item.Device
				}
				if device == "" {
					continue
				}
				logicalMap[device] = item.Interface
				if isWANLogical(item.Interface) {
					wanSet[device] = true
				}
			}
		}
	}
	defaultRoute := defaultRouteInterface()

	var result []InterfaceInfo
	seen := map[string]bool{}
	nics, err := net.Interfaces()
	if err != nil {
		return result
	}
	for _, nic := range nics {
		if nic.Flags&net.FlagLoopback != 0 || len(nic.HardwareAddr) != 6 {
			continue
		}
		name := nic.Name
		if seen[name] {
			continue
		}
		seen[name] = true
		logical := logicalMap[name]
		carrier := carrierUp(name)
		isWAN := wanSet[name] || name == defaultRoute || isWANLogical(name) ||
			isWANLogical(logical) || strings.HasPrefix(name, "wan")
		result = append(result, InterfaceInfo{
			Name:        name,
			Logical:     logical,
			MAC:         strings.ToLower(nic.HardwareAddr.String()),
			Carrier:     carrier,
			IsWAN:       isWAN,
			Description: describe(name, logical, carrier, isWAN),
		})
	}
	sort.SliceStable(result, func(i, j int) bool {
		a, b := result[i], result[j]
		if a.IsWAN != b.IsWAN {
			return a.IsWAN
		}
		if a.Carrier != b.Carrier {
			return a.Carrier
		}
		return a.Name < b.Name
	})
	return result
}

// ResolveCandidates maps the configured device list onto concrete netdevs,
// preserving the user's order. `auto` expands to every detected WAN. The
// returned list is what the daemon tries in order.
func ResolveCandidates(configured []string) []InterfaceInfo {
	interfaces := Interfaces()
	byName := map[string]InterfaceInfo{}
	for _, item := range interfaces {
		byName[item.Name] = item
		if item.Logical != "" {
			byName[item.Logical] = item
		}
	}

	explicit := false
	var chosen []InterfaceInfo
	seen := map[string]bool{}
	add := func(item InterfaceInfo) {
		if seen[item.Name] {
			return
		}
		seen[item.Name] = true
		chosen = append(chosen, item)
	}
	for _, raw := range configured {
		name := strings.TrimSpace(raw)
		if name == "" {
			continue
		}
		if strings.EqualFold(name, "auto") {
			for _, item := range interfaces {
				if item.IsWAN && item.Carrier {
					add(item)
				}
			}
			continue
		}
		explicit = true
		if item, ok := byName[name]; ok {
			add(item)
			continue
		}
		// Unknown name: still try it as a netdev, so a typo produces a clear
		// transport error instead of silently authenticating elsewhere.
		add(InterfaceInfo{Name: name, Description: name})
	}
	if !explicit && len(chosen) == 0 {
		for _, item := range interfaces {
			if item.IsWAN {
				add(item)
			}
		}
	}
	if len(chosen) == 0 {
		for _, item := range interfaces {
			if item.Carrier && item.IsWAN {
				add(item)
			}
		}
	}
	return chosen
}

func isWANLogical(name string) bool {
	switch strings.ToLower(name) {
	case "wan", "wan6", "wan_6", "usbwan", "wwan", "modem", "lte":
		return true
	}
	return strings.HasPrefix(strings.ToLower(name), "wan")
}

func carrierUp(name string) bool {
	if data, err := os.ReadFile("/sys/class/net/" + name + "/carrier"); err == nil {
		return strings.TrimSpace(string(data)) == "1"
	}
	if nic, err := net.InterfaceByName(name); err == nil {
		return nic.Flags&net.FlagRunning != 0
	}
	return false
}

func defaultRouteInterface() string {
	data, err := os.ReadFile("/proc/net/route")
	if err != nil {
		return ""
	}
	lines := strings.Split(string(data), "\n")
	for i, line := range lines {
		if i == 0 {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 || fields[1] != "00000000" {
			continue
		}
		return fields[0]
	}
	return ""
}

func describe(name, logical string, carrier, isWAN bool) string {
	label := name
	if logical != "" && logical != name {
		label += "（" + logical + "）"
	}
	parts := []string{label}
	if isWAN {
		parts = append(parts, "校园网 WAN")
	}
	if carrier {
		parts = append(parts, "已连接")
	} else {
		parts = append(parts, "无链路")
	}
	return strings.Join(parts, " ")
}
