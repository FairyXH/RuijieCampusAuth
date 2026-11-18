package network

import (
	"bufio"
	"net"
	"os"
	"strconv"
	"strings"

	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/protocol"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/transport"
)

// Collect reads the IPv4 fields used in the private header.
func Collect(device string) protocol.NetworkInfo {
	name, err := transport.ResolveDevice(device)
	if err != nil {
		return protocol.NetworkInfo{}
	}
	iface, err := net.InterfaceByName(name)
	if err != nil {
		return protocol.NetworkInfo{}
	}
	addrs, err := iface.Addrs()
	if err != nil {
		return protocol.NetworkInfo{}
	}
	ip := ""
	netmask := ""
	for _, addr := range addrs {
		ipnet, ok := addr.(*net.IPNet)
		if !ok {
			continue
		}
		v4 := ipnet.IP.To4()
		if v4 == nil {
			continue
		}
		ip = v4.String()
		netmask = net.IP(ipnet.Mask).String()
		break
	}
	gateway := defaultGateway(name)
	return protocol.NetworkInfo{
		IP:       ip,
		Netmask:  netmask,
		Gateway:  gateway,
		DHCP:     gateway,
		IPString: ip,
	}
}

func defaultGateway(iface string) string {
	data, err := os.ReadFile("/proc/net/route")
	if err != nil {
		return ""
	}
	return parseDefaultGateway(string(data), iface)
}

func parseDefaultGateway(data, iface string) string {
	scanner := bufio.NewScanner(strings.NewReader(data))
	first := true
	for scanner.Scan() {
		if first {
			first = false
			continue
		}
		fields := strings.Fields(scanner.Text())
		if len(fields) < 3 || fields[0] != iface {
			continue
		}
		if fields[1] != "00000000" {
			continue
		}
		gateway, err := strconv.ParseUint(fields[2], 16, 32)
		if err != nil || gateway == 0 {
			continue
		}
		return net.IPv4(
			byte(gateway),
			byte(gateway>>8),
			byte(gateway>>16),
			byte(gateway>>24),
		).String()
	}
	return ""
}
