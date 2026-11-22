package dircomm

import (
	"bytes"
	"encoding/hex"
	"net"
	"strings"
	"testing"
	"time"
)

var liveKey, _ = hex.DecodeString("7C73533126403871") // dircomm.log DES key
var liveIV, _ = hex.DecodeString("4163332331215164")  // dircomm.log IV

func testConfig() Config {
	return Config{
		ServiceName: []byte("svct"),
		IP:          net.ParseIP("127.0.0.1").To4(),
		MAC:         []byte{0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF},
		Key:         liveKey,
		IV:          liveIV,
	}
}

func TestSelectorIndex(t *testing.T) {
	// (sum(htonl(seq) || 16 zero bytes) * 0x2d7) mod 53, mirrored from
	// sub_4a6e30.
	cases := []struct {
		seq  uint32
		want int
	}{
		{1, 38},
		{2, 23},
		{0x11223344, 47},
	}
	for _, c := range cases {
		if got := SelectorIndex(c.seq); got != c.want {
			t.Errorf("SelectorIndex(%d) = %d, want %d", c.seq, got, c.want)
		}
	}
}

func TestTableEntry(t *testing.T) {
	if len(obfuscationTable) != tableEntries*tableEntrySize {
		t.Fatalf("embedded table is %d bytes, want %d",
			len(obfuscationTable), tableEntries*tableEntrySize)
	}
	// First bytes of entry 0, extracted from 0x7343c0 in 8021x.exe.
	first := obfuscationTable[:8]
	want, _ := hex.DecodeString("a4996b0f47e71c6c")
	if !bytes.Equal(first, want) {
		t.Errorf("table entry 0 starts %x, want %x", first, want)
	}
	entry, err := TableEntry(2)
	if err != nil {
		t.Fatalf("TableEntry(2): %v", err)
	}
	if !bytes.Equal(entry, obfuscationTable[2*tableEntrySize:3*tableEntrySize]) {
		t.Error("TableEntry(2) does not slice entry 2")
	}
}

func TestPlaintextLayout(t *testing.T) {
	body, err := Plaintext(testConfig(), 1)
	if err != nil {
		t.Fatalf("Plaintext: %v", err)
	}
	// 01 01 02 | 03 04 svct | 04 04 7f000001 | 05 06 aabbccddeeff
	// | 14 80 <entry 38> | zero padding to 160 bytes.
	want := []byte{
		0x01, 0x01, 0x02,
		0x03, 0x04, 's', 'v', 'c', 't',
		0x04, 0x04, 127, 0, 0, 1,
		0x05, 0x06, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
		0x14, 0x80,
	}
	entry, _ := TableEntry(1)
	want = append(want, entry...)
	if raw := len(body) - len(want); raw != 7 {
		t.Fatalf("padding = %d bytes, want 7 (total %d)", raw, len(body))
	}
	if !bytes.Equal(body[:len(want)], want) {
		t.Error("plaintext TLV prefix mismatch")
	}
	if !bytes.Equal(body[len(want):], make([]byte, 7)) {
		t.Error("padding is not zero")
	}
}

func TestEncryptCBC(t *testing.T) {
	// Vector computed with pycryptodome for the key/IV observed in
	// dircomm.log: DES-CBC(one block 00..07) = 96dd177a6ec9671a.
	block := []byte{0, 1, 2, 3, 4, 5, 6, 7}
	got, err := Encrypt(liveKey, liveIV, block)
	if err != nil {
		t.Fatalf("Encrypt: %v", err)
	}
	if hex.EncodeToString(got) != "96dd177a6ec9671a" {
		t.Errorf("Encrypt = %x, want 96dd177a6ec9671a", got)
	}

	// Multi-block chain: changing the first plaintext block must alter
	// every following block (CBC diffusion).
	plaintext := make([]byte, 32)
	for i := range plaintext {
		plaintext[i] = byte(i)
	}
	first, _ := Encrypt(liveKey, liveIV, plaintext)
	plaintext[0] ^= 0xFF
	second, _ := Encrypt(liveKey, liveIV, plaintext)
	if bytes.Equal(first[16:], second[16:]) {
		t.Error("CBC diffusion failure: later blocks unchanged")
	}
}

func TestBuildHeartbeatEndToEnd(t *testing.T) {
	packet, err := BuildHeartbeat(testConfig(), 7)
	if err != nil {
		t.Fatalf("BuildHeartbeat: %v", err)
	}
	if len(packet)%8 != 0 {
		t.Errorf("packet length %d is not 8-byte aligned", len(packet))
	}
	if bytes.Contains(packet, []byte("svct")) {
		t.Error("service name leaked into ciphertext")
	}
}

func TestValidate(t *testing.T) {
	cfg := testConfig()
	cfg.MAC = cfg.MAC[:5]
	if err := cfg.Validate(); err == nil {
		t.Error("Validate accepted a 5-byte MAC")
	}
	cfg = testConfig()
	cfg.Key = nil
	if err := cfg.Validate(); err == nil {
		t.Error("Validate accepted a missing key")
	}
}

func TestParseServerAddress(t *testing.T) {
	// The INDICATE_SERV_IP observed on this campus (2887125761) is
	// 172.22.11.1 big-endian.
	addr, err := ParseServerAddress(2887125761, 53)
	if err != nil {
		t.Fatalf("ParseServerAddress: %v", err)
	}
	if addr != "172.22.11.1:53" {
		t.Errorf("ParseServerAddress = %q, want 172.22.11.1:53", addr)
	}
	if _, err := ParseServerAddress(1, 0); err == nil {
		t.Error("ParseServerAddress accepted port 0")
	}
}

func TestMonitorAgainstEcho(t *testing.T) {
	// A fake SAM that answers every heartbeat keeps the monitor healthy.
	sam, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Skipf("udp unavailable: %v", err)
	}
	defer sam.Close()
	go func() {
		buf := make([]byte, 1500)
		for {
			n, addr, err := sam.ReadFromUDP(buf)
			if err != nil {
				return
			}
			_, _ = sam.WriteToUDP(buf[:n], addr)
		}
	}()

	var logs []string
	monitor, err := NewMonitor(MonitorOptions{
		Config:   testConfig(),
		Server:   sam.LocalAddr().String(),
		Interval: 40 * time.Millisecond,
		Timeout:  300 * time.Millisecond,
		Log:      func(m string) { logs = append(logs, m) },
	})
	if err != nil {
		t.Fatalf("NewMonitor: %v", err)
	}
	monitor.Start()
	defer monitor.Stop()

	deadline := time.After(2 * time.Second)
	for {
		select {
		case ev := <-monitor.Events():
			if ev.Reauth {
				t.Fatalf("unexpected reauth against echoing SAM: %s", ev.Message)
			}
		case <-time.After(400 * time.Millisecond):
			return // survived several beats without failure
		case <-deadline:
			t.Fatal("monitor did not stay healthy against echo server")
		}
	}
}

func TestMonitorReauthAfterSilence(t *testing.T) {
	// A silent SAM: after ReauthThreshold unanswered beats the monitor
	// must emit the reauth event and stop.
	sam, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Skipf("udp unavailable: %v", err)
	}
	defer sam.Close() // bound but never answers

	monitor, err := NewMonitor(MonitorOptions{
		Config:   testConfig(),
		Server:   sam.LocalAddr().String(),
		Interval: 30 * time.Millisecond,
		Timeout:  60 * time.Millisecond,
		Retry:    0,
	})
	if err != nil {
		t.Fatalf("NewMonitor: %v", err)
	}
	monitor.Start()
	defer monitor.Stop()

	select {
	case ev := <-monitor.Events():
		if !ev.Reauth {
			t.Fatalf("expected reauth event, got %+v", ev)
		}
		if !strings.Contains(ev.Message, "心跳") {
			t.Errorf("message %q does not mention the heartbeat", ev.Message)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("no reauth event after silent SAM")
	}
}
