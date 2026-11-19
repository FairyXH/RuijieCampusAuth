package protocol

import (
	"errors"
	"io"
	"net"
	"sync"
	"time"
)

// EapTLSConn bridges crypto/tls to EAP/PEAP payloads, the Go counterpart of
// EapTlsStream: Read blocks until the transport feeds a server fragment,
// Write hands TLS records to the PEAP pump.
type EapTLSConn struct {
	mu       sync.Mutex
	incoming chan []byte
	current  []byte
	offset   int
	outgoing chan []byte
	closed   chan struct{}
	once     sync.Once

	deadlineMu   sync.Mutex
	readDeadline time.Time
}

// NewEapTLSConn creates an empty bridge.
func NewEapTLSConn() *EapTLSConn {
	return &EapTLSConn{
		incoming: make(chan []byte, 16),
		outgoing: make(chan []byte, 16),
		closed:   make(chan struct{}),
	}
}

// Feed queues a server TLS fragment.
func (c *EapTLSConn) Feed(data []byte) {
	if len(data) == 0 {
		return
	}
	c.mu.Lock()
	select {
	case <-c.closed:
	default:
		select {
		case c.incoming <- append([]byte(nil), data...):
		default:
			// Keep the pump from blocking forever on a slow TLS reader.
			go func() { c.incoming <- append([]byte(nil), data...) }()
		}
	}
	c.mu.Unlock()
}

// CompleteInput closes the inbound stream; pending and future reads see EOF.
func (c *EapTLSConn) CompleteInput() {
	c.once.Do(func() {
		close(c.closed)
		close(c.incoming)
		close(c.outgoing)
	})
}

// Read implements net.Conn.
func (c *EapTLSConn) Read(buffer []byte) (int, error) {
	for {
		c.mu.Lock()
		if c.current != nil && c.offset < len(c.current) {
			length := len(buffer)
			if remaining := len(c.current) - c.offset; length > remaining {
				length = remaining
			}
			copy(buffer, c.current[c.offset:c.offset+length])
			c.offset += length
			c.mu.Unlock()
			return length, nil
		}
		c.mu.Unlock()

		chunk, err := c.next()
		if err != nil {
			return 0, err
		}
		c.mu.Lock()
		c.current = chunk
		c.offset = 0
		c.mu.Unlock()
	}
}

// next waits for the next inbound fragment, honouring the read deadline.
func (c *EapTLSConn) next() ([]byte, error) {
	timeout, hasTimeout := c.readTimeout()
	if !hasTimeout {
		chunk, ok := <-c.incoming
		if !ok {
			return nil, io.EOF
		}
		return chunk, nil
	}
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case chunk, ok := <-c.incoming:
		if !ok {
			return nil, io.EOF
		}
		return chunk, nil
	case <-timer.C:
		return nil, errDeadline
	}
}

// Write implements net.Conn.
func (c *EapTLSConn) Write(buffer []byte) (int, error) {
	if len(buffer) == 0 {
		return 0, nil
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	select {
	case <-c.closed:
		return 0, errors.New("TLS bridge closed")
	default:
	}
	c.outgoing <- append([]byte(nil), buffer...)
	return len(buffer), nil
}

// TakeOutgoing returns the next TLS record, or false when the bridge closed.
func (c *EapTLSConn) TakeOutgoing() ([]byte, bool) {
	chunk, ok := <-c.outgoing
	return chunk, ok
}

// Close implements net.Conn.
func (c *EapTLSConn) Close() error {
	c.CompleteInput()
	return nil
}

// LocalAddr implements net.Conn.
func (c *EapTLSConn) LocalAddr() net.Addr { return netAddr("eap-local") }

// RemoteAddr implements net.Conn.
func (c *EapTLSConn) RemoteAddr() net.Addr { return netAddr("eap-remote") }

// SetDeadline implements net.Conn.
func (c *EapTLSConn) SetDeadline(deadline time.Time) error {
	c.SetReadDeadline(deadline)
	return nil
}

// SetReadDeadline implements net.Conn.
func (c *EapTLSConn) SetReadDeadline(deadline time.Time) error {
	c.deadlineMu.Lock()
	c.readDeadline = deadline
	c.deadlineMu.Unlock()
	return nil
}

// SetWriteDeadline implements net.Conn.
func (c *EapTLSConn) SetWriteDeadline(time.Time) error { return nil }

func (c *EapTLSConn) readTimeout() (time.Duration, bool) {
	c.deadlineMu.Lock()
	deadline := c.readDeadline
	c.deadlineMu.Unlock()
	if deadline.IsZero() {
		return 0, false
	}
	remaining := time.Until(deadline)
	if remaining <= 0 {
		return time.Millisecond, true
	}
	return remaining, true
}

type netAddr string

func (a netAddr) Network() string { return "eap" }
func (a netAddr) String() string  { return string(a) }

var errDeadline = errors.New("EAP TLS read deadline exceeded")
