package status

import (
	"encoding/json"
	"os"
	"time"
)

// DefaultPath is written by the daemon and read by the CLI/LuCI.
const DefaultPath = "/var/run/hxxy-auth/status.json"

// Snapshot is the daemon status document.
type Snapshot struct {
	Time      string `json:"time"`
	State     string `json:"state"`
	Message   string `json:"message"`
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
