package protocol

import (
	"bytes"
	"testing"
)

func TestControlFrameHasPrivateTailOutsideEAPOLLength(t *testing.T) {
	tail := bytes.Repeat([]byte{0x5A}, 569)
	frame, err := BuildControl("aa:bb:cc:dd:ee:ff", RuijieGroup, 1, tail)
	if err != nil {
		t.Fatal(err)
	}
	if len(frame) != 587 {
		t.Fatalf("control frame length = %d, want 587", len(frame))
	}
	if frame[12] != 0x88 || frame[13] != 0x8E {
		t.Fatalf("ethertype = %02x%02x, want 888e", frame[12], frame[13])
	}
	if frame[14] != 1 || frame[15] != 1 {
		t.Fatalf("EAPOL version/type = %d/%d, want 1/1", frame[14], frame[15])
	}
	if frame[16] != 0 || frame[17] != 0 {
		t.Fatalf("control EAPOL length = %d, want 0", int(frame[16])<<8|int(frame[17]))
	}
	if !bytes.Equal(frame[18:], tail) {
		t.Fatal("private tail was not appended at offset 18")
	}
}

func TestIdentityFrameKeepsTailOutsideEAPOLLength(t *testing.T) {
	eap := IdentityResponse(3, []byte("user"))
	tail := bytes.Repeat([]byte{0x33}, 569)
	frame, err := BuildFrame("aa:bb:cc:dd:ee:ff", RuijieGroup, eap, tail)
	if err != nil {
		t.Fatal(err)
	}
	if len(frame) != 14+4+len(eap)+len(tail) {
		t.Fatalf("frame length = %d", len(frame))
	}
	if got := int(frame[16])<<8 | int(frame[17]); got != len(eap) {
		t.Fatalf("EAPOL length = %d, want %d", got, len(eap))
	}
	code, identifier, parsed, ok := TryParseEAP(frame)
	if !ok {
		t.Fatal("TryParseEAP rejected a valid frame")
	}
	if code != EapResponse || identifier != 3 {
		t.Fatalf("parsed code/id = %d/%d, want 2/3", code, identifier)
	}
	if !bytes.Equal(parsed, eap) {
		t.Fatal("parsed EAP payload differs from the input")
	}
}

func TestMD5ResponseLayout(t *testing.T) {
	response := MD5Response(
		7,
		[]byte("pass"),
		bytes.Repeat([]byte{0x11}, 16),
		[]byte("user"),
	)
	if len(response) != 26 {
		t.Fatalf("MD5 response length = %d, want 26", len(response))
	}
	if response[0] != EapResponse || response[1] != 7 {
		t.Fatalf("code/id = %d/%d, want 2/7", response[0], response[1])
	}
	if response[4] != EapMD5 || response[5] != 16 {
		t.Fatalf("type/size = %d/%d, want 4/16", response[4], response[5])
	}
	if string(response[22:]) != "user" {
		t.Fatalf("identity suffix = %q, want user", response[22:])
	}
}

func TestExtractServerMessages(t *testing.T) {
	message, err := GB18030Encode("欢迎使用厦门华厦学院网络")
	if err != nil {
		t.Fatal(err)
	}
	data := append([]byte{}, vendorMarker...)
	data = append(data, 0x00, byte(len(message)))
	data = append(data, message...)

	eap := []byte{EapSuccess, 1, 0, 0}
	eap = append(eap, data...)
	eap[2] = byte(len(eap) >> 8)
	eap[3] = byte(len(eap))

	messages := ExtractServerMessages(eap)
	if len(messages) != 1 {
		t.Fatalf("messages = %#v, want one message", messages)
	}
	if messages[0] != "欢迎使用厦门华厦学院网络" {
		t.Fatalf("message = %q", messages[0])
	}
}
