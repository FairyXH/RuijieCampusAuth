//go:build linux

package connectivity

import "syscall"

// deviceControl pins a socket to one network device (SO_BINDTODEVICE).
// Binding affects the route lookup as well as the egress interface, so a
// probe for the campus WAN continues to test the campus path even when a
// lower-metric USB or Wi-Fi default route is active.
func deviceControl(device string) func(string, string, syscall.RawConn) error {
	if device == "" {
		return nil
	}
	return func(_, _ string, conn syscall.RawConn) error {
		var sockErr error
		if err := conn.Control(func(fd uintptr) {
			sockErr = syscall.SetsockoptString(
				int(fd), syscall.SOL_SOCKET, syscall.SO_BINDTODEVICE, device)
		}); err != nil {
			return err
		}
		return sockErr
	}
}
