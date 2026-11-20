package protocol

import (
	"encoding/hex"
	"fmt"
	"strings"
	"testing"
)

func TestRC4MatchesStandardVector(t *testing.T) {
	output := RC4Transform([]byte("Plaintext"), []byte("Key"))
	if got := hex.EncodeToString(output); got != "bbf316e8d940af0ad3" {
		t.Fatalf("rc4 = %s", got)
	}
}

// TestParseRealCapturedServerBlock uses the live capture vector locked by
// RuijieCampusAuth.Core.Tests.PrivatePropertiesTests and cross-checked
// against original 8021x.exe ParsePrivateProperty (0x537ee0).
func TestParseRealCapturedServerBlock(t *testing.T) {
	const payloadHex = "000013110018bbb6d3adcab9d3c3cfc3c3c5bbaacfc3d1a7d4bacdf8c2e7000013116900000000000000" +
		"000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
		"000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
		"000000000000000000000000000000000013110100000013110101ffff3777afff6765cda1ffffffd7ff" +
		"00001311560600000000000013115a0600000035000013115b06ac160b01000013115c0a6e9bafe984d4" +
		"1102000013115d0a538bcffb93b57817000013115e0a000000006afef017000013116e060000008a0000" +
		"13117903020000131180060000000000001311980601000100"
	payload, err := hex.DecodeString(payloadHex)
	if err != nil {
		t.Fatalf("decode vector: %v", err)
	}

	result := ParsePrivateProperties(payload)
	if len(result.Errors) != 0 {
		t.Fatalf("errors: %v", result.Errors)
	}
	if len(result.Messages) != 1 || result.Messages[0] != "欢迎使用厦门华厦学院网络" {
		t.Fatalf("messages = %#v", result.Messages)
	}
	if result.Section2Value == nil || *result.Section2Value != 0 {
		t.Fatalf("section2 = %v", result.Section2Value)
	}
	if result.Section5Hash == nil || *result.Section5Hash != 0x19594C7A {
		t.Fatalf("hash = %v", result.Section5Hash)
	}
	if result.Section5IntervalMs == nil || *result.Section5IntervalMs != 20000 {
		t.Fatalf("interval = %v", result.Section5IntervalMs)
	}
	if len(result.Properties) != 10 {
		t.Fatalf("properties = %d", len(result.Properties))
	}

	expect := []struct {
		name   string
		number *int64
		hex    string
	}{
		{"SU_REAUTH_INTERV", int64Ptr(0), "00000000"},
		{"INDICATE_PORT", int64Ptr(53), "00000035"},
		{"INDICATE_SERV_IP", int64Ptr(0xac160b01), "ac160b01"},
		{"ENCRYPT_KEY", nil, "7c73533126403871"},
		{"ENCRYPT_IV", nil, "4163332331215164"},
		{"SERVER_UTC_TIME", int64Ptr(0x6afef017), "000000006afef017"},
		{"MSG_CLIENT_PORT", int64Ptr(138), "0000008a"},
		{"DIRECT_COMM_HIGHEST_VERSION", int64Ptr(2), "02"},
		{"DIRECT_COMM_HEARTBEAT_FLAGS", int64Ptr(0), "00000000"},
		{"WIFI_CNNT_NUM_FLAGS", int64Ptr(0x01000100), "01000100"},
	}
	for i, want := range expect {
		got := result.Properties[i]
		if got.Name != want.name {
			t.Fatalf("property %d name = %s, want %s", i, got.Name, want.name)
		}
		if got.Hex != want.hex {
			t.Fatalf("property %d hex = %s, want %s", i, got.Hex, want.hex)
		}
		if want.number == nil {
			if got.Number != nil {
				t.Fatalf("property %d number = %d, want nil", i, *got.Number)
			}
			continue
		}
		if got.Number == nil || *got.Number != *want.number {
			gotValue := "<nil>"
			if got.Number != nil {
				gotValue = fmt.Sprintf("%d", *got.Number)
			}
			t.Fatalf("property %d number = %s, want %d", i, gotValue, *want.number)
		}
	}
}

func TestParseRejectsMissingMarker(t *testing.T) {
	result := ParsePrivateProperties([]byte("garbage"))
	if len(result.Errors) == 0 || result.Errors[0] != "缺少段 1 标记" {
		t.Fatalf("errors = %#v", result.Errors)
	}
}

func TestMD4Vectors(t *testing.T) {
	cases := map[string]string{
		"":    "31d6cfe0d16ae931b73c59d7e0c089c0",
		"abc": "a448017aaf21d8525fc10ae87aa6729d",
	}
	for input, want := range cases {
		if got := hex.EncodeToString(MD4Hash([]byte(input))); got != want {
			t.Fatalf("md4(%q) = %s", input, got)
		}
	}
}

func TestMschapV2RFCVector(t *testing.T) {
	authenticator := mustHex(t, "5B5D7C7D7B3F2F3E3C2C602132262628")
	peer := mustHex(t, "21402324255E262A28295F2B3A337C7E")
	if got := hex.EncodeToString(ChallengeHash(peer, authenticator, "User")); got != "d02e4386bce91226" {
		t.Fatalf("challenge hash = %s", got)
	}
	nt := GenerateNTResponse(authenticator, peer, "User", "clientPass")
	if got := hex.EncodeToString(nt); got != "82309ecd8d708b5ea08faa3981cd83544233114a3d85d6df" {
		t.Fatalf("nt response = %s", got)
	}
}

func TestMschapV2ChallengeAndResponseLayout(t *testing.T) {
	name := []byte("server")
	data := make([]byte, 5+16+len(name))
	data[0] = 1
	data[1] = 9
	data[2] = 0
	data[3] = byte(5 + 16 + len(name))
	data[4] = 16
	for i := 0; i < 16; i++ {
		data[5+i] = byte(i)
	}
	copy(data[21:], name)
	eap := make([]byte, 5+len(data))
	eap[0] = 1
	eap[1] = 7
	eap[2] = byte((5 + len(data)) >> 8)
	eap[3] = byte(5 + len(data))
	eap[4] = 26
	copy(eap[5:], data)

	challenge, err := TryParseChallenge(eap)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if challenge.Identifier != 7 || challenge.MschapIdentifier != 9 || challenge.Name != "server" {
		t.Fatalf("challenge = %#v", challenge)
	}
	peer := make([]byte, 16)
	for i := range peer {
		peer[i] = 0x11
	}
	response, err := BuildResponse(challenge, "user", "pass", peer)
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if response[0] != 2 || response[1] != 7 || response[4] != 26 ||
		response[5] != 2 || response[6] != 9 || response[9] != 49 {
		t.Fatalf("header = % x", response[:10])
	}
	if string(response[10:26]) != string(peer) {
		t.Fatalf("peer challenge = % x", response[10:26])
	}
	if got := string(response[59:]); got != "user" {
		t.Fatalf("name = %q", got)
	}
}

func TestAuthMethodParsing(t *testing.T) {
	cases := map[string]AuthMethodKind{
		"":              AuthEAPMD5,
		"EAPMD5":        AuthEAPMD5,
		"eap-md5":       AuthEAPMD5,
		"PEAP":          AuthPEAPv0,
		"peapv0":        AuthPEAPv0,
		"PEAP-MSCHAPV2": AuthPEAPv0,
		"PEAPV1":        AuthPEAPv1,
		"EAP-TLS":       AuthEAPTLS,
		"unknown":       AuthEAPMD5,
	}
	for input, want := range cases {
		if got := ParseAuthMethod(input); got != want {
			t.Fatalf("ParseAuthMethod(%q) = %v, want %v", input, got, want)
		}
	}
}

func mustHex(t *testing.T, value string) []byte {
	t.Helper()
	data, err := hex.DecodeString(strings.TrimSpace(value))
	if err != nil {
		t.Fatalf("hex %q: %v", value, err)
	}
	return data
}

func int64Ptr(value int64) *int64 { return &value }
