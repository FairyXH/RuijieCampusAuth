package protocol

import (
	"bytes"
	"crypto/sha256"
	"fmt"
	"testing"
)

func TestTailMatchesPythonReference(t *testing.T) {
	serviceName, err := GB18030Encode("电信宽带服务")
	if err != nil {
		t.Fatal(err)
	}
	network := NetworkInfo{
		IP:       "172.18.28.6",
		Netmask:  "255.255.128.0",
		Gateway:  "172.18.0.1",
		DHCP:     "172.18.0.1",
		IPv6:     make([]byte, 16),
		IPString: "172.18.28.6",
		StaticID: []byte("Static:AB45A862"),
	}
	options := &TailOptions{
		HeaderFlag:  1,
		Nonce:       bytes.Repeat([]byte("A"), 32),
		Fingerprint: bytes.Repeat([]byte("B"), 128),
		ServiceName: serviceName,
	}
	tail, err := BuildTail(
		[]byte("user"),
		[]byte("pass"),
		"aa:bb:cc:dd:ee:ff",
		network,
		make([]byte, 16),
		options,
	)
	if err != nil {
		t.Fatal(err)
	}
	if len(tail) != 569 {
		t.Fatalf("tail length = %d, want 569", len(tail))
	}
	got := fmt.Sprintf("%x", sha256.Sum256(tail))
	const want = "779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5"
	if got != want {
		t.Fatalf("tail sha256 = %s, want %s", got, want)
	}
}

func TestNormalizeMAC(t *testing.T) {
	cases := map[string]string{
		"AA-BB-CC-DD-EE-FF": "aa:bb:cc:dd:ee:ff",
		"aabbccddeeff":      "aa:bb:cc:dd:ee:ff",
		"aa:bb:cc:dd:ee:ff": "aa:bb:cc:dd:ee:ff",
		"not-a-mac":         "",
	}
	for input, want := range cases {
		if got := NormalizeMAC(input); got != want {
			t.Fatalf("NormalizeMAC(%q) = %q, want %q", input, got, want)
		}
	}
}

func TestCRC16XMODEM(t *testing.T) {
	// Standard CRC-16/XMODEM check value for "123456789".
	if got := CRC16XMODEM([]byte("123456789")); got != 0x31C3 {
		t.Fatalf("CRC16XMODEM = 0x%04X, want 0x31C3", got)
	}
}
