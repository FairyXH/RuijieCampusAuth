package control

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"time"
)

// DefaultSocket is the daemon control socket.
const DefaultSocket = "/var/run/hxxy-auth.sock"

// Request is one control command.
type Request struct {
	Command string `json:"command"`
}

// Response is the daemon reply.
type Response struct {
	OK    bool   `json:"ok"`
	Error string `json:"error,omitempty"`
}

var validCommands = map[string]bool{
	"auth":   true,
	"logoff": true,
	"pause":  true,
	"resume": true,
	"reload": true,
}

// Serve accepts control commands until ctx is cancelled.
func Serve(ctx context.Context, path string, commands chan<- string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	_ = os.Remove(path)
	listener, err := net.Listen("unix", path)
	if err != nil {
		return err
	}
	if err := os.Chmod(path, 0o600); err != nil {
		_ = listener.Close()
		return err
	}
	go func() {
		<-ctx.Done()
		_ = listener.Close()
	}()
	for {
		conn, err := listener.Accept()
		if err != nil {
			select {
			case <-ctx.Done():
				return nil
			default:
				return err
			}
		}
		go handle(conn, commands)
	}
}

func handle(conn net.Conn, commands chan<- string) {
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(5 * time.Second))
	var request Request
	response := Response{}
	if err := json.NewDecoder(conn).Decode(&request); err != nil {
		response.Error = "invalid request"
	} else if !validCommands[request.Command] {
		response.Error = "unknown command"
	} else {
		select {
		case commands <- request.Command:
			response.OK = true
		default:
			response.Error = "daemon busy"
		}
	}
	_ = json.NewEncoder(conn).Encode(response)
}

// Send asks the daemon to run one command.
func Send(path, command string) error {
	if !validCommands[command] {
		return fmt.Errorf("unknown command: %s", command)
	}
	conn, err := net.DialTimeout("unix", path, 3*time.Second)
	if err != nil {
		return fmt.Errorf("daemon not running: %w", err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(5 * time.Second))
	if err := json.NewEncoder(conn).Encode(Request{Command: command}); err != nil {
		return err
	}
	var response Response
	if err := json.NewDecoder(conn).Decode(&response); err != nil {
		return err
	}
	if !response.OK {
		if response.Error == "" {
			response.Error = "command rejected"
		}
		return fmt.Errorf("%s", response.Error)
	}
	return nil
}
