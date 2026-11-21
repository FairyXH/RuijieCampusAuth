// Package connectivity implements fast multi-target egress probes inspired by
// NetworkGuardian: a TCP quorum over independent public endpoints plus an
// HTTP/204 fallback for networks where outbound TCP 53 is filtered.
package connectivity

import (
	"context"
	"errors"
	"net"
	"net/http"
	"strings"
	"syscall"
	"time"
)

// TCP probes every target concurrently. Any successful handshake or RST
// proves that the egress path reaches the public Internet. A timeout or
// network/host unreachable does not. This mirrors the Npcap SYN probe's
// "a TCP reply is enough" rule without needing raw sockets.
func TCP(ctx context.Context, targets []string, timeout time.Duration) bool {
	return TCPOn(ctx, "", targets, timeout)
}

// TCPOn is TCP bound to a specific egress device. With several active
// uplinks (campus WAN, USB tethering, Wi-Fi STA), the unbound probe follows
// the default route and can report "online" for the wrong uplink. Binding
// makes the answer belong to the interface the authenticator is using.
func TCPOn(ctx context.Context, device string, targets []string, timeout time.Duration) bool {
	if len(targets) == 0 {
		return false
	}
	if timeout <= 0 {
		timeout = 2 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	results := make(chan bool, len(targets))
	for _, target := range targets {
		go func(target string) {
			dialer := net.Dialer{Timeout: timeout, Control: deviceControl(device)}
			conn, err := dialer.DialContext(ctx, "tcp", target)
			if err == nil {
				_ = conn.Close()
				results <- true
				return
			}
			if errors.Is(err, syscall.ECONNREFUSED) || errors.Is(err, syscall.ECONNRESET) {
				results <- true
				return
			}
			results <- false
		}(target)
	}

	for range targets {
		if <-results {
			return true
		}
	}
	return false
}

// HTTP performs the fallback check. A generate_204 endpoint must answer 204;
// anything else (portal redirect, captive HTML, timeout) is treated as offline.
func HTTP(ctx context.Context, rawURL string, timeout time.Duration) bool {
	return HTTPOn(ctx, "", rawURL, timeout)
}

// HTTPOn is HTTP over a socket bound to device.
func HTTPOn(ctx context.Context, device, rawURL string, timeout time.Duration) bool {
	rawURL = strings.TrimSpace(rawURL)
	if rawURL == "" {
		return false
	}
	if timeout <= 0 {
		timeout = 2 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return false
	}
	dialer := &net.Dialer{Timeout: timeout, Control: deviceControl(device)}
	transport := &http.Transport{
		DialContext:       dialer.DialContext,
		DisableKeepAlives: true,
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Timeout:   timeout,
		Transport: transport,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	resp, err := client.Do(req)
	if err != nil {
		return false
	}
	defer resp.Body.Close()

	if strings.Contains(rawURL, "generate_204") {
		return resp.StatusCode == http.StatusNoContent
	}
	return resp.StatusCode >= 200 && resp.StatusCode < 300
}
