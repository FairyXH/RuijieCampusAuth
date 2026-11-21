//go:build !linux

package connectivity

import "syscall"

// deviceControl is a no-op outside Linux. The OpenWrt daemon is Linux-only;
// this stub keeps the package buildable for host-side tests elsewhere.
func deviceControl(string) func(string, string, syscall.RawConn) error {
	return nil
}
