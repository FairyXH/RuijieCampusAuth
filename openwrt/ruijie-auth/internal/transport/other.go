//go:build !linux

package transport

import "errors"

// Open is only implemented on Linux.
func Open(device string) (Transport, error) {
	return nil, errors.New("raw socket transport is only implemented on Linux")
}

// ResolveDevice passes through on non-Linux builds.
func ResolveDevice(device string) (string, error) {
	return device, nil
}
