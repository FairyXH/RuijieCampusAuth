//go:build linux

package connectivity

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestTCPOnLoopback(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	go func() {
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			_ = conn.Close()
		}
	}()

	if !TCPOn(context.Background(), "lo", []string{listener.Addr().String()}, time.Second) {
		t.Fatal("probe bound to lo did not reach a loopback listener")
	}
}

func TestTCPOnMissingDevice(t *testing.T) {
	if TCPOn(context.Background(), "ruijie-no-such-device", []string{"127.0.0.1:1"}, 300*time.Millisecond) {
		t.Fatal("probe with a missing device should fail")
	}
}

func TestHTTPOnLoopback(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNoContent)
	}))
	defer server.Close()

	if !HTTPOn(context.Background(), "lo", server.URL+"/generate_204", time.Second) {
		t.Fatal("HTTP probe bound to lo did not reach the test server")
	}
}

func TestHTTPAnyOnLoopback(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	defer server.Close()

	urls := []string{"http://127.0.0.1:1/", server.URL + "/"}
	if !HTTPAnyOn(context.Background(), "lo", urls, time.Second) {
		t.Fatal("HTTPAnyOn did not accept a working URL")
	}
}
