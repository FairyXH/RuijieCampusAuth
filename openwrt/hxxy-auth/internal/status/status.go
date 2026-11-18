package status

import (
	"encoding/json"
	"os"
	"path/filepath"
	"time"
)

// DefaultPath is written by the daemon and read by the CLI/LuCI.
const DefaultPath = "/var/run/hxxy-auth/status.json"

// Snapshot is the daemon status document.
type Snapshot struct {
	Time      string `json:"time"`
	State     string `json:"state"`
	Message   string `json:"message"`
	Account   string `json:"account"`
	Interface string `json:"interface"`
	Server    string `json:"server"`
}

// Read loads a status document.
func Read(path string) (*Snapshot, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var snapshot Snapshot
	if err := json.Unmarshal(data, &snapshot); err != nil {
		return nil, err
	}
	return &snapshot, nil
}

// Write atomically updates the status document.
func Write(path string, snapshot Snapshot) error {
	if snapshot.Time == "" {
		snapshot.Time = time.Now().Format(time.RFC3339)
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(snapshot, "", "  ")
	if err != nil {
		return err
	}
	temp := path + ".tmp"
	if err := os.WriteFile(temp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(temp, path)
}

// ReadOrDefault loads a status document, or returns a stopped snapshot.
func ReadOrDefault(path string) Snapshot {
	snapshot, err := Read(path)
	if err == nil && snapshot != nil {
		return *snapshot
	}
	return Snapshot{
		Time:    time.Now().Format(time.RFC3339),
		State:   "stopped",
		Message: "服务未运行",
	}
}
