package protocol

import (
	"bytes"
	"encoding/hex"
	"testing"
)

// fakeTransport records the EAP packets the PEAP logic emits.
type fakeTransport struct {
	sent      [][]byte
	responses [][]byte
	source    string
	server    string
	success   bool
	failure   string
}

func (f *fakeTransport) SourceMAC() string { return f.source }
func (f *fakeTransport) ServerMAC() string { return f.server }
func (f *fakeTransport) OuterSuccess() bool { return f.success }
func (f *fakeTransport) OuterFailure() string { return f.failure }

func (f *fakeTransport) Start(identity string, tail []byte) ([]byte, error) {
	return []byte{EapRequest, 1, 0, 5, EapPEAP}, nil
}

func (f *fakeTransport) Exchange(eapPacket, tail []byte) ([]byte, error) {
	f.sent = append(f.sent, append([]byte(nil), eapPacket...))
	if len(f.responses) == 0 {
		return []byte{EapRequest, byte(len(f.sent)), 0, 5, EapPEAP}, nil
	}
	next := f.responses[0]
	f.responses = f.responses[1:]
	return next, nil
}

func TestBuildTunnelResponseLayout(t *testing.T) {
	payload := []byte{0x80, 0x00, 0x00, 0x00, 0x01, 0xAA}
	peap := buildTunnelResponse(TunnelPEAP, payload)
	if peap[0] != EapResponse || peap[4] != EapPEAP {
		t.Fatalf("header = % x", peap[:5])
	}
	if int(peap[2])<<8|int(peap[3]) != len(peap) {
		t.Fatalf("length mismatch: % x", peap)
	}
	if !bytes.Equal(peap[5:], payload) {
		t.Fatalf("payload = % x", peap[5:])
	}
	tls := buildTunnelResponse(TunnelEAPTLS, payload)
	if tls[4] != EapTLS {
		t.Fatalf("eap-tls type = %d", tls[4])
	}
}

func TestTryParseTunnel(t *testing.T) {
	flags, data, ok := tryParseTunnel([]byte{EapRequest, 2, 0, 10, EapPEAP, 0x80, 0, 0, 0, 3, 1, 2, 3}, TunnelPEAP)
	if !ok || flags != 0x80 || !bytes.Equal(data, []byte{1, 2, 3}) {
		t.Fatalf("parse = %x %x %v", flags, data, ok)
	}
	if _, _, ok := tryParseTunnel([]byte{EapRequest, 2, 0, 5, EapTLS}, TunnelPEAP); ok {
		t.Fatal("wrong EAP type must not parse")
	}
	if _, _, ok := tryParseTunnel([]byte{EapRequest, 2, 0, 10, EapPEAP, 0x80, 0, 0, 0}, TunnelPEAP); ok {
		t.Fatal("truncated length header must not parse")
	}
}

// TestSendClientPayloadFragments verifies the flag and length layout of a
// multi-fragment TLS record, which is what a real RADIUS server parses.
func TestSendClientPayloadFragments(t *testing.T) {
	transport := &fakeTransport{source: "aa:bb:cc:dd:ee:ff", server: "00:11:22:33:44:55"}
	data := bytes.Repeat([]byte{0x5A}, 500)
	if _, err := sendClientPayload(transport, data, PeapOptions{Mode: TunnelPEAP, MaxFragmentBytes: 200}); err != nil {
		t.Fatal(err)
	}
	if len(transport.sent) != 3 {
		t.Fatalf("fragments = %d", len(transport.sent))
	}
	first := transport.sent[0]
	if first[4] != EapPEAP ||
		first[5] != tunnelFlagLength|tunnelFlagMore ||
		first[6] != 0 || first[7] != 0 || first[8] != 0x01 || first[9] != 0xF4 {
		t.Fatalf("first fragment header = % x", first[:10])
	}
	if transport.sent[1][5] != tunnelFlagMore {
		t.Fatalf("second flags = %02x", transport.sent[1][5])
	}
	if transport.sent[2][5] != 0x00 {
		t.Fatalf("last flags = %02x", transport.sent[2][5])
	}
	total := 0
	for i, packet := range transport.sent {
		offset := 6
		if i == 0 {
			offset = 10
		}
		total += len(packet) - offset
	}
	if total != len(data) {
		t.Fatalf("reassembled %d bytes, want %d", total, len(data))
	}
}

func TestReceiveServerPayloadAcksFragments(t *testing.T) {
	transport := &fakeTransport{
		source: "aa:bb:cc:dd:ee:ff",
		server: "00:11:22:33:44:55",
		responses: [][]byte{
			{EapRequest, 3, 0, 9, EapPEAP, tunnelFlagMore, 0xAA},
			{EapRequest, 4, 0, 9, EapPEAP, 0x00, 0xBB},
		},
	}
	payload, ok := receiveServerPayload(transport, []byte{EapRequest, 2, 0, 9, EapPEAP, tunnelFlagMore, 0x90}, PeapOptions{Mode: TunnelPEAP})
	if !ok {
		t.Fatal("receive failed")
	}
	if !bytes.Equal(payload, []byte{0x90, 0xAA, 0xBB}) {
		t.Fatalf("payload = % x", payload)
	}
	if len(transport.sent) != 2 {
		t.Fatalf("acks = %d", len(transport.sent))
	}
	for _, ack := range transport.sent {
		if ack[4] != EapPEAP || !bytes.Equal(ack[5:], []byte{0x00}) {
			t.Fatalf("ack layout = % x", ack)
		}
	}
}

func TestTunnelModeConstants(t *testing.T) {
	if EapTLS != 13 || EapPEAP != 25 || EapMSCHAPv2 != 26 {
		t.Fatal("EAP type constants drifted")
	}
	if hex.EncodeToString(obfuscateMarker()) != "00001311" {
		t.Fatal("vendor marker drifted")
	}
}

func obfuscateMarker() []byte { return privatePropertyMarker }
