package servers

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestServerUpsertLockAndRemove(t *testing.T) {
	path := filepath.Join(t.TempDir(), ServersFile)
	store, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	record := store.Upsert("AA-BB-CC-DD-EE-FF", "wan", "wan", true)
	if record == nil || record.MAC != "aa:bb:cc:dd:ee:ff" {
		t.Fatalf("record = %+v", record)
	}
	if record.SuccessCount != 1 || record.FirstSeen == "" || record.LastSuccess == "" {
		t.Fatalf("record fields = %+v", record)
	}
	record = store.Upsert("aa:bb:cc:dd:ee:ff", "wan", "wan", true)
	if record.SuccessCount != 2 {
		t.Fatalf("success count = %d, want 2", record.SuccessCount)
	}
	store.Upsert("11:22:33:44:55:66", "wan", "wan", false)
	if !store.Lock("aa-bb-cc-dd-ee-ff") {
		t.Fatal("lock failed")
	}
	if got := store.LockedMAC(); got != "aa:bb:cc:dd:ee:ff" {
		t.Fatalf("locked = %q", got)
	}
	if !store.Lock("11:22:33:44:55:66") {
		t.Fatal("second lock failed")
	}
	if got := store.LockedMAC(); got != "11:22:33:44:55:66" {
		t.Fatalf("locked = %q, want 11:22:33:44:55:66", got)
	}
	store.Unlock()
	if got := store.LockedMAC(); got != "" {
		t.Fatalf("locked = %q, want empty", got)
	}
	if !store.Rename("11:22:33:44:55:66", "备用") {
		t.Fatal("rename failed")
	}
	if !store.Remove("11:22:33:44:55:66") {
		t.Fatal("remove failed")
	}
	if err := store.Save(path); err != nil {
		t.Fatal(err)
	}
	loaded, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if len(loaded.Servers) != 1 || loaded.Servers[0].SuccessCount != 2 {
		t.Fatalf("loaded = %+v", loaded)
	}
	if runtime.GOOS != "windows" {
		info, err := os.Stat(path)
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode().Perm() != 0o600 {
			t.Fatalf("mode = %o, want 600", info.Mode().Perm())
		}
	}
}

func TestDiscoveryRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), DiscoveryFile)
	if discovery, err := LoadDiscovery(path); err != nil || discovery != nil {
		t.Fatalf("missing discovery = %+v, err=%v", discovery, err)
	}
	if err := SaveDiscovery(path, Discovery{
		Interface:   "wan",
		Description: "wan",
		MAC:         "aa:bb:cc:dd:ee:ff",
		ServerMAC:   "00:1a:a9:17:ff:ff",
		UpdatedAt:   "2026-11-18T23:00:00+08:00",
	}); err != nil {
		t.Fatal(err)
	}
	discovery, err := LoadDiscovery(path)
	if err != nil {
		t.Fatal(err)
	}
	if discovery == nil || discovery.ServerMAC != "00:1a:a9:17:ff:ff" {
		t.Fatalf("discovery = %+v", discovery)
	}
	if err := ClearDiscovery(path); err != nil {
		t.Fatal(err)
	}
	if discovery, err := LoadDiscovery(path); err != nil || discovery != nil {
		t.Fatalf("cleared discovery = %+v, err=%v", discovery, err)
	}
}
