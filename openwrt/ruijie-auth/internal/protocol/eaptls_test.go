package protocol

import (
	"errors"
	"io"
	"sync"
	"testing"
	"time"
)

// TestEapTLSConnCloseDoesNotPanic sweeps the shutdown race that a plain
// channel close would introduce: Feed and Write may still be called while the
// PEAP pump completes the input side.
func TestEapTLSConnCloseDoesNotPanic(t *testing.T) {
	conn := NewEapTLSConn()
	conn.Feed([]byte{0x01, 0x02, 0x03})
	conn.CompleteInput()

	if _, err := conn.Write([]byte{0xAA}); err == nil {
		t.Fatal("Write after CompleteInput must fail")
	}
	conn.Feed([]byte{0x04})
	conn.CompleteInput()

	buffer := make([]byte, 8)
	n, err := conn.Read(buffer)
	if err != nil {
		t.Fatalf("pending read: %v", err)
	}
	if n != 3 || buffer[0] != 0x01 {
		t.Fatalf("pending data = % x", buffer[:n])
	}
	if _, err := conn.Read(buffer); !errors.Is(err, io.EOF) {
		t.Fatalf("read after drain = %v, want EOF", err)
	}
}

// TestEapTLSConnConcurrentClose races Feed/Write against CompleteInput. Run
// with -race to prove the shutdown path does not touch a closed channel.
func TestEapTLSConnConcurrentClose(t *testing.T) {
	for i := 0; i < 32; i++ {
		conn := NewEapTLSConn()
		var wait sync.WaitGroup
		wait.Add(2)
		go func() {
			defer wait.Done()
			for j := 0; j < 64; j++ {
				_, _ = conn.Write([]byte{0xAA, 0xBB})
			}
		}()
		go func() {
			defer wait.Done()
			for j := 0; j < 64; j++ {
				conn.Feed([]byte{0xCC, 0xDD})
			}
		}()
		conn.CompleteInput()
		wait.Wait()
	}
}

// TestEapTLSConnReadDeadline verifies that a stalled handshake unblocks with
// a timeout instead of hanging RunPeap.
func TestEapTLSConnReadDeadline(t *testing.T) {
	conn := NewEapTLSConn()
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Millisecond))
	start := time.Now()
	buffer := make([]byte, 4)
	if _, err := conn.Read(buffer); !errors.Is(err, errDeadline) {
		t.Fatalf("read = %v, want deadline", err)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Fatalf("deadline took %v", elapsed)
	}
}
