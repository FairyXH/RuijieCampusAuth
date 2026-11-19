//go:build linux

package transport

import (
	"encoding/json"
	"fmt"
	"net"
	"os/exec"
	"strings"
	"sync"

	"golang.org/x/sys/unix"
)

type linuxTransport struct {
	fd        int
	ifindex   int
	mac       string
	packets   chan []byte
	done      chan struct{}
	closeOnce sync.Once
}

// Open opens an AF_PACKET raw socket bound to ETH_P_PAE.
func Open(device string) (Transport, error) {
	name, err := ResolveDevice(device)
	if err != nil {
		return nil, err
	}
	iface, err := net.InterfaceByName(name)
	if err != nil {
		return nil, fmt.Errorf("interface %q: %w", name, err)
	}
	protocol := int(htons(uint16(unix.ETH_P_PAE)))
	fd, err := unix.Socket(unix.AF_PACKET, unix.SOCK_RAW, protocol)
	if err != nil {
		return nil, fmt.Errorf("socket AF_PACKET: %w", err)
	}
	if err := unix.Bind(fd, &unix.SockaddrLinklayer{
		Protocol: htons(uint16(unix.ETH_P_PAE)),
		Ifindex:  iface.Index,
	}); err != nil {
		_ = unix.Close(fd)
		return nil, fmt.Errorf("bind %s: %w", name, err)
	}

	promisc := &unix.PacketMreq{
		Ifindex: int32(iface.Index),
		Type:    unix.PACKET_MR_PROMISC,
	}
	if err := unix.SetsockoptPacketMreq(fd, unix.SOL_PACKET, unix.PACKET_ADD_MEMBERSHIP, promisc); err != nil {
		// Fall back to the standard PAE multicast group.
		multicast := &unix.PacketMreq{
			Ifindex: int32(iface.Index),
			Type:    unix.PACKET_MR_MULTICAST,
			Alen:    6,
			Address: [8]uint8{0x01, 0x80, 0xc2, 0x00, 0x00, 0x03},
		}
		if err2 := unix.SetsockoptPacketMreq(fd, unix.SOL_PACKET, unix.PACKET_ADD_MEMBERSHIP, multicast); err2 != nil {
			_ = unix.Close(fd)
			return nil, fmt.Errorf("promiscuous mode: %v; PAE multicast: %v", err, err2)
		}
	}

	t := &linuxTransport{
		fd:      fd,
		ifindex: iface.Index,
		mac:     strings.ToLower(iface.HardwareAddr.String()),
		packets: make(chan []byte, 64),
		done:    make(chan struct{}),
	}
	go t.readLoop()
	return t, nil
}

func (t *linuxTransport) readLoop() {
	defer close(t.packets)
	buffer := make([]byte, 65536)
	for {
		n, from, err := unix.Recvfrom(t.fd, buffer, 0)
		if err != nil {
			return
		}
		if n <= 0 {
			continue
		}
		if link, ok := from.(*unix.SockaddrLinklayer); ok && link.Pkttype == unix.PACKET_OUTGOING {
			continue
		}
		frame := make([]byte, n)
		copy(frame, buffer[:n])
		select {
		case t.packets <- frame:
		case <-t.done:
			return
		}
	}
}

// Send writes one complete Ethernet frame.
func (t *linuxTransport) Send(frame []byte) error {
	select {
	case <-t.done:
		return errClosed
	default:
	}
	return unix.Sendto(t.fd, frame, 0, &unix.SockaddrLinklayer{Ifindex: t.ifindex})
}

// Packets returns the receive channel.
func (t *linuxTransport) Packets() <-chan []byte {
	return t.packets
}

// HardwareAddr returns the interface MAC.
func (t *linuxTransport) HardwareAddr() string {
	return t.mac
}

// Close releases the socket.
func (t *linuxTransport) Close() error {
	t.closeOnce.Do(func() {
		close(t.done)
		_ = unix.Close(t.fd)
	})
	return nil
}

// ResolveDevice maps a UCI logical interface to its Linux netdev.
func ResolveDevice(device string) (string, error) {
	if _, err := net.InterfaceByName(device); err == nil {
		return device, nil
	}
	output, err := exec.Command("ubus", "call", "network.interface."+device, "status").Output()
	if err != nil {
		return "", fmt.Errorf("interface %q not found: %w", device, err)
	}
	var status struct {
		L3Device string `json:"l3_device"`
		Device   string `json:"device"`
	}
	if err := json.Unmarshal(output, &status); err != nil {
		return "", fmt.Errorf("parse ubus status for %q: %w", device, err)
	}
	name := status.L3Device
	if name == "" {
		name = status.Device
	}
	if name == "" {
		return "", fmt.Errorf("interface %q has no l3_device", device)
	}
	if _, err := net.InterfaceByName(name); err != nil {
		return "", fmt.Errorf("resolved device %q: %w", name, err)
	}
	return name, nil
}

func htons(value uint16) uint16 {
	return value<<8 | value>>8
}
