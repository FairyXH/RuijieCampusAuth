package servers

import (
	"encoding/json"
	"os"
	"path/filepath"
	"time"

	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/protocol"
)

const (
	// DiscoveryFile is the cached auth channel.
	DiscoveryFile = "discovery.json"
	// ServersFile is the learned authenticator list.
	ServersFile = "servers.json"
)

// Discovery is the last working interface/server pair.
type Discovery struct {
	Version     int    `json:"version"`
	Interface   string `json:"interface"`
	Description string `json:"description"`
	MAC         string `json:"mac"`
	ServerMAC   string `json:"server_mac"`
	UpdatedAt   string `json:"updated_at"`
}

// Server is one learned authenticator.
type Server struct {
	MAC          string `json:"mac"`
	Name         string `json:"name"`
	Note         string `json:"note"`
	Interface    string `json:"interface"`
	Description  string `json:"description"`
	FirstSeen    string `json:"first_seen"`
	LastSeen     string `json:"last_seen"`
	LastSuccess  string `json:"last_success"`
	SuccessCount int    `json:"success_count"`
	Locked       bool   `json:"locked"`
}

// Store is the learned-server list.
type Store struct {
	Version int      `json:"version"`
	Servers []Server `json:"servers"`
}

// LoadDiscovery reads discovery.json; a missing file returns nil.
func LoadDiscovery(path string) (*Discovery, error) {
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var discovery Discovery
	if err := json.Unmarshal(data, &discovery); err != nil {
		return nil, err
	}
	return &discovery, nil
}

// SaveDiscovery writes discovery.json with 0600 permissions.
func SaveDiscovery(path string, discovery Discovery) error {
	discovery.Version = 1
	return writeJSON(path, discovery)
}

// ClearDiscovery removes the cache.
func ClearDiscovery(path string) error {
	err := os.Remove(path)
	if os.IsNotExist(err) {
		return nil
	}
	return err
}

// Load reads servers.json; a missing file returns an empty store.
func Load(path string) (*Store, error) {
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return &Store{Version: 1}, nil
	}
	if err != nil {
		return &Store{Version: 1}, err
	}
	var store Store
	if err := json.Unmarshal(data, &store); err != nil {
		return &Store{Version: 1}, err
	}
	store.Version = 1
	return &store, nil
}

// Save writes servers.json with 0600 permissions.
func (s *Store) Save(path string) error {
	s.Version = 1
	return writeJSON(path, s)
}

// Upsert records an authenticator and optionally a successful exchange.
func (s *Store) Upsert(mac, iface, description string, success bool) *Server {
	normalized := protocol.NormalizeMAC(mac)
	if normalized == "" {
		return nil
	}
	now := time.Now().Format(time.RFC3339)
	var record *Server
	for i := range s.Servers {
		if s.Servers[i].MAC == normalized {
			record = &s.Servers[i]
			break
		}
	}
	if record == nil {
		s.Servers = append(s.Servers, Server{
			MAC:       normalized,
			FirstSeen: now,
		})
		record = &s.Servers[len(s.Servers)-1]
	}
	record.LastSeen = now
	if iface != "" {
		record.Interface = iface
	}
	if description != "" {
		record.Description = description
	}
	if success {
		record.SuccessCount++
		record.LastSuccess = now
	}
	return record
}

// Lock marks one server as the unicast target and clears other locks.
func (s *Store) Lock(mac string) bool {
	normalized := protocol.NormalizeMAC(mac)
	if normalized == "" {
		return false
	}
	found := false
	for i := range s.Servers {
		s.Servers[i].Locked = s.Servers[i].MAC == normalized
		if s.Servers[i].Locked {
			found = true
		}
	}
	return found
}

// Unlock clears all locks.
func (s *Store) Unlock() {
	for i := range s.Servers {
		s.Servers[i].Locked = false
	}
}

// LockedMAC returns the locked authenticator, or "".
func (s *Store) LockedMAC() string {
	for _, record := range s.Servers {
		if record.Locked {
			return record.MAC
		}
	}
	return ""
}

// Remove deletes a server entry.
func (s *Store) Remove(mac string) bool {
	normalized := protocol.NormalizeMAC(mac)
	if normalized == "" {
		return false
	}
	kept := s.Servers[:0]
	removed := false
	for _, record := range s.Servers {
		if record.MAC == normalized {
			removed = true
			continue
		}
		kept = append(kept, record)
	}
	s.Servers = kept
	return removed
}

// Rename updates the display name.
func (s *Store) Rename(mac, name string) bool {
	normalized := protocol.NormalizeMAC(mac)
	if normalized == "" {
		return false
	}
	for i := range s.Servers {
		if s.Servers[i].MAC == normalized {
			s.Servers[i].Name = name
			return true
		}
	}
	return false
}

func writeJSON(path string, value any) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	data, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	temp := path + ".tmp"
	if err := os.WriteFile(temp, data, 0o600); err != nil {
		return err
	}
	if err := os.Chmod(temp, 0o600); err != nil {
		_ = os.Remove(temp)
		return err
	}
	return os.Rename(temp, path)
}
