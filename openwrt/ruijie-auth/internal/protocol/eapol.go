package protocol

import (
	"bytes"
	"crypto/md5"
	"encoding/hex"
	"strings"
	"unicode/utf8"
)

const (
	// RuijieGroup is the Ruijie discovery group.
	RuijieGroup = "01:d0:f8:00:00:03"
	// StandardGroup is the standard PAE group used for Logoff.
	StandardGroup = "01:80:c2:00:00:03"
	// EtherType is the EAPOL EtherType.
	EtherType = 0x888E

	EapRequest  = 1
	EapResponse = 2
	EapSuccess  = 3
	EapFailure  = 4
	EapIdentity = 1
	EapMD5      = 4
)

var vendorMarker = []byte{0x00, 0x00, 0x13, 0x11}

// MACBytes parses a MAC address into six bytes.
func MACBytes(mac string) ([]byte, error) {
	normalized := NormalizeMAC(mac)
	if normalized == "" {
		return nil, errInvalidMAC(mac)
	}
	return hex.DecodeString(strings.ReplaceAll(normalized, ":", ""))
}

type invalidMAC string

func (e invalidMAC) Error() string { return "MAC 地址无效: " + string(e) }

func errInvalidMAC(mac string) error { return invalidMAC(mac) }

// BuildFrame assembles an 802.1X data frame. The private tail stays outside
// the EAPOL length field, exactly like the official client.
func BuildFrame(source, destination string, eap, tail []byte) ([]byte, error) {
	eapLength := len(eap)
	if len(eap) >= 4 {
		declared := int(eap[2])<<8 | int(eap[3])
		if declared > 0 && declared <= len(eap) {
			eapLength = declared
		}
	}
	src, err := MACBytes(source)
	if err != nil {
		return nil, err
	}
	dst, err := MACBytes(destination)
	if err != nil {
		return nil, err
	}
	frame := make([]byte, 14+4+len(eap)+len(tail))
	copy(frame[0:6], dst)
	copy(frame[6:12], src)
	frame[12] = byte(EtherType >> 8)
	frame[13] = byte(EtherType & 0xFF)
	frame[14] = 1
	frame[15] = 0
	frame[16] = byte(eapLength >> 8)
	frame[17] = byte(eapLength)
	copy(frame[18:], eap)
	copy(frame[18+len(eap):], tail)
	return frame, nil
}

// BuildControl assembles EAPOL-Start (type 1) or EAPOL-Logoff (type 2).
func BuildControl(source, destination string, typ byte, tail []byte) ([]byte, error) {
	src, err := MACBytes(source)
	if err != nil {
		return nil, err
	}
	dst, err := MACBytes(destination)
	if err != nil {
		return nil, err
	}
	frame := make([]byte, 18+len(tail))
	copy(frame[0:6], dst)
	copy(frame[6:12], src)
	frame[12] = byte(EtherType >> 8)
	frame[13] = byte(EtherType & 0xFF)
	frame[14] = 1
	frame[15] = typ
	frame[16] = 0
	frame[17] = 0
	copy(frame[18:], tail)
	return frame, nil
}

// IdentityResponse builds an EAP-Response/Identity.
func IdentityResponse(identifier byte, identity []byte) []byte {
	length := 5 + len(identity)
	eap := make([]byte, length)
	eap[0] = EapResponse
	eap[1] = identifier
	eap[2] = byte(length >> 8)
	eap[3] = byte(length)
	eap[4] = EapIdentity
	copy(eap[5:], identity)
	return eap
}

// MD5Response builds an EAP-Response/MD5.
//
// MD5 is required by EAP-MD5; it is protocol compatibility, not password
// storage or a confidentiality primitive.
func MD5Response(identifier byte, password, challenge, identity []byte) []byte {
	seed := make([]byte, 0, 1+len(password)+len(challenge))
	seed = append(seed, identifier)
	seed = append(seed, password...)
	seed = append(seed, challenge...)
	digest := md5.Sum(seed)

	length := 6 + len(digest) + len(identity)
	eap := make([]byte, length)
	eap[0] = EapResponse
	eap[1] = identifier
	eap[2] = byte(length >> 8)
	eap[3] = byte(length)
	eap[4] = EapMD5
	eap[5] = 16
	copy(eap[6:], digest[:])
	copy(eap[6+len(digest):], identity)
	return eap
}

// TryParseEAP parses an EAPOL data frame, leaving the private tail out.
func TryParseEAP(frame []byte) (code, identifier byte, eap []byte, ok bool) {
	if len(frame) < 18 {
		return 0, 0, nil, false
	}
	if frame[12] != byte(EtherType>>8) || frame[13] != byte(EtherType&0xFF) {
		return 0, 0, nil, false
	}
	if frame[15] != 0 {
		return 0, 0, nil, false
	}
	payload := frame[18:]
	if len(payload) < 4 {
		return 0, 0, nil, false
	}
	code = payload[0]
	identifier = payload[1]
	length := int(payload[2])<<8 | int(payload[3])
	if length < 4 || length > len(payload) {
		return 0, 0, nil, false
	}
	return code, identifier, payload[:length], true
}

// ExtractServerMessages pulls GB18030 text out of Ruijie result frames.
func ExtractServerMessages(eap []byte) []string {
	if len(eap) <= 4 {
		return nil
	}
	data := eap[4:]
	positions := make([]int, 0, 2)
	start := 0
	for {
		index := bytes.Index(data[start:], vendorMarker)
		if index < 0 {
			break
		}
		position := start + index
		positions = append(positions, position)
		start = position + len(vendorMarker)
	}

	messages := make([]string, 0, len(positions))
	seen := make(map[string]bool)
	for i, position := range positions {
		end := len(data)
		if i+1 < len(positions) {
			end = positions[i+1]
		}
		if position+6 > len(data) {
			continue
		}
		if data[position+4] != 0 {
			continue
		}
		chunk := data[position+6 : end]
		chunk = bytes.TrimRight(chunk, "\x00")
		if len(chunk) == 0 {
			continue
		}
		candidates := [][]byte{chunk}
		declared := int(data[position+5])
		if declared > 0 && len(chunk) >= declared {
			candidates = append([][]byte{chunk[:declared]}, candidates...)
		}
		for _, candidate := range candidates {
			text, err := GB18030Decode(candidate)
			if err != nil {
				continue
			}
			text = strings.Trim(text, "\x00 \r\n\t")
			if !containsCJK(text) || printableRatio(text) < 0.8 {
				continue
			}
			if !seen[text] {
				seen[text] = true
				messages = append(messages, text)
			}
			break
		}
	}
	return messages
}

func containsCJK(text string) bool {
	for _, r := range text {
		if r >= '\u4e00' && r <= '\u9fff' {
			return true
		}
	}
	return false
}

func printableRatio(text string) float64 {
	if text == "" {
		return 0
	}
	total := utf8.RuneCountInString(text)
	if total == 0 {
		return 0
	}
	printable := 0
	for _, r := range text {
		if r == utf8.RuneError {
			continue
		}
		if r >= 0x20 && r != 0x7f {
			printable++
		}
	}
	return float64(printable) / float64(total)
}
