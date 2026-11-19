package control

import (
	"context"
	"path/filepath"
	"testing"
	"time"
)

func TestControlRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "control.sock")
	commands := make(chan string, 1)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = Serve(ctx, path, commands) }()

	deadline := time.Now().Add(2 * time.Second)
	var err error
	for time.Now().Before(deadline) {
		if err = Send(path, "auth"); err == nil {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if err != nil {
		t.Fatal(err)
	}
	select {
	case command := <-commands:
		if command != "auth" {
			t.Fatalf("command = %q, want auth", command)
		}
	case <-time.After(time.Second):
		t.Fatal("command was not delivered")
	}
	if err := Send(path, "nope"); err == nil {
		t.Fatal("unknown command was accepted")
	}
}
