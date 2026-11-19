package transport

import (
	"sync"
)

// Fake is an in-memory transport used by tests and simulation.
type Fake struct {
	mac      string
	mu       sync.Mutex
	sent     [][]byte
	incoming chan []byte
	closed   bool
}

// NewFake creates a fake transport with the given MAC.
func NewFake(mac string) *Fake {
	return &Fake{
		mac:      mac,
		incoming: make(chan []byte, 64),
	}
}

// Send records an outgoing frame.
func (f *Fake) Send(frame []byte) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.closed {
		return errClosed
	}
	copied := append([]byte(nil), frame...)
	f.sent = append(f.sent, copied)
	return nil
}

// Packets returns the receive channel.
func (f *Fake) Packets() <-chan []byte {
	return f.incoming
}

// HardwareAddr returns the configured MAC.
func (f *Fake) HardwareAddr() string {
	return f.mac
}

// Inject delivers a frame to the session.
func (f *Fake) Inject(frame []byte) {
	f.mu.Lock()
	closed := f.closed
	f.mu.Unlock()
	if closed {
		return
	}
	select {
	case f.incoming <- append([]byte(nil), frame...):
	default:
	}
}

// Sent returns a copy of sent frames.
func (f *Fake) Sent() [][]byte {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([][]byte, len(f.sent))
	for i, frame := range f.sent {
		out[i] = append([]byte(nil), frame...)
	}
	return out
}

// Close closes the receive channel.
func (f *Fake) Close() error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.closed {
		return nil
	}
	f.closed = true
	close(f.incoming)
	return nil
}

type closedError struct{}

func (closedError) Error() string { return "transport closed" }

var errClosed error = closedError{}
