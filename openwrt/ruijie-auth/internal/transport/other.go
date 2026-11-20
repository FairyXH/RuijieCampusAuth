//go:build !linux

package transport

import "errors"

// Open is only implemented on Linux.
func Open(device string) (Transport, error) {
	return nil, errors.New("原始套接字传输仅支持 Linux")
}

// ResolveDevice passes through on non-Linux builds.
func ResolveDevice(device string) (string, error) {
	return device, nil
}
