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
//
// The queues are never closed. Shutdown is signalled through `closed`, so a
// concurrent Write or a deferred Feed can never panic with "send on closed
// channel"; the same ordering as the C# BlockingCollection (CompleteAdding on
// the inbound side only) is preserved.
type EapTLSConn struct {
	incoming  chan []byte
	outgoing  chan []byte
	closed    chan struct{}
	closeOnce sync.Once

	mu      sync.Mutex
	current []byte
	offset  int

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

// Feed queues a server TLS fragment. Fragments are queued in order; a full
// queue never blocks the PEAP pump.
func (c *EapTLSConn) Feed(data []byte) {
	if len(data) == 0 {
		return
	}
	copied := append([]byte(nil), data...)
	select {
	case <-c.closed:
		return
	default:
	}
	select {
	case c.incoming <- copied:
	default:
		go func() {
			select {
			case c.incoming <- copied:
			case <-c.closed:
			}
		}()
	}
}

// CompleteInput closes the inbound stream; pending data is still readable,
// then reads see EOF. Pending TLS output can still be drained by the pump.
func (c *EapTLSConn) CompleteInput() {
	c.closeOnce.Do(func() { close(c.closed) })
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
	select {
	case chunk := <-c.incoming:
		return chunk, nil
	default:
	}

	timeout, hasTimeout := c.readTimeout()
	if !hasTimeout {
		select {
		case chunk := <-c.incoming:
			return chunk, nil
		case <-c.closed:
			return nil, io.EOF
		}
	}

	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case chunk := <-c.incoming:
		return chunk, nil
	case <-c.closed:
		return nil, io.EOF
	case <-timer.C:
		return nil, errDeadline
	}
}

// Write implements net.Conn.
func (c *EapTLSConn) Write(buffer []byte) (int, error) {
	if len(buffer) == 0 {
		return 0, nil
	}
	copied := append([]byte(nil), buffer...)
	select {
	case <-c.closed:
		return 0, errors.New("EAP TLS 通道已关闭")
	default:
	}
	select {
	case c.outgoing <- copied:
		return len(buffer), nil
	case <-c.closed:
		return 0, errors.New("EAP TLS 通道已关闭")
	}
}

// TakeOutgoing returns the next TLS record, or false when the bridge closed
// and no record is pending.
func (c *EapTLSConn) TakeOutgoing() ([]byte, bool) {
	select {
	case chunk := <-c.outgoing:
		return chunk, true
	default:
	}
	select {
	case chunk := <-c.outgoing:
		return chunk, true
	case <-c.closed:
		select {
		case chunk := <-c.outgoing:
			return chunk, true
		default:
			return nil, false
		}
	}
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

var errDeadline = errors.New("EAP TLS 读取超时")
