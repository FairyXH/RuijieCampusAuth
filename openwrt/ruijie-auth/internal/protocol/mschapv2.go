package protocol

import (
	"crypto/des"
	"crypto/sha1"
	"encoding/binary"
	"errors"
	"strings"
)

// MSCHAPv2 opcodes.
const (
	MschapChallengeOpcode = 1
	MschapResponseOpcode  = 2
	MschapSuccessOpcode   = 3
	MschapFailureOpcode   = 4
	// EapMSCHAPv2 is the EAP type used inside the PEAP tunnel.
	EapMSCHAPv2 byte = 26
)

// MschapChallenge is the server challenge inside EAP-Request/MSCHAPv2,
// identical to MschapV2Challenge in the C# core.
type MschapChallenge struct {
	Identifier       byte
	MschapIdentifier byte
	Challenge        []byte
	Name             string
}

// MschapResponse mirrors MschapV2Response.
type MschapResponse struct {
	PeerChallenge []byte
	NTResponse    []byte
	UserName      string
}

// TryParseChallenge parses an EAP-Request/MSCHAPv2 packet. A non-nil error
// carries the same reason string the C# implementation returns.
func TryParseChallenge(eapPacket []byte) (*MschapChallenge, error) {
	if len(eapPacket) < 5+21 || eapPacket[0] != 1 || eapPacket[4] != EapMSCHAPv2 {
		return nil, errors.New("not an EAP-MSCHAPv2 packet")
	}
	data := eapPacket[5:]
	if data[0] != MschapChallengeOpcode || data[4] != 16 {
		return nil, errors.New("not an MSCHAPv2 challenge")
	}
	nameLength := len(data) - 21
	if nameLength < 0 {
		return nil, errors.New("challenge too short")
	}
	return &MschapChallenge{
		Identifier:       eapPacket[1],
		MschapIdentifier: data[1],
		Challenge:        append([]byte(nil), data[5:21]...),
		Name:             string(data[21:]),
	}, nil
}

// BuildResponse builds EAP-Response/MSCHAPv2 with the RFC 2759 value layout.
func BuildResponse(
	challenge *MschapChallenge,
	userName, password string,
	peerChallenge []byte,
) ([]byte, error) {
	if len(peerChallenge) != 16 {
		return nil, errors.New("peer challenge must be 16 bytes")
	}
	ntResponse := GenerateNTResponse(
		challenge.Challenge, peerChallenge, userName, password)
	name := []byte(userName)
	const valueSize = 49
	msLength := 5 + valueSize + len(name)
	packet := make([]byte, 5+msLength)
	packet[0] = EapResponse
	packet[1] = challenge.Identifier
	packet[2] = byte((5 + msLength) >> 8)
	packet[3] = byte(5 + msLength)
	packet[4] = EapMSCHAPv2
	packet[5] = MschapResponseOpcode
	packet[6] = challenge.MschapIdentifier
	packet[7] = byte(msLength >> 8)
	packet[8] = byte(msLength)
	packet[9] = valueSize
	copy(packet[10:26], peerChallenge)
	// packet[26:34] is the reserved area and stays zero.
	copy(packet[34:58], ntResponse)
	packet[58] = 0 // Flags
	copy(packet[59:], name)
	return packet, nil
}

// GenerateNTResponse implements the RFC 2759 NT-Response derivation.
func GenerateNTResponse(
	authenticatorChallenge, peerChallenge []byte,
	userName, password string,
) []byte {
	challenge := ChallengeHash(peerChallenge, authenticatorChallenge, userName)
	passwordHash := MD4Hash(utf16le(password))
	zPasswordHash := make([]byte, 24)
	copy(zPasswordHash, passwordHash)
	response := make([]byte, 24)
	for i := 0; i < 3; i++ {
		key := make([]byte, 8)
		expandDesKey(zPasswordHash[i*7:i*7+7], key)
		setOddParity(key)
		block, err := des.NewCipher(key)
		if err != nil {
			return nil
		}
		block.Encrypt(response[i*8:], challenge[:8])
	}
	return response
}

// ChallengeHash is SHA1(peerChallenge | authenticatorChallenge | user)[:8].
func ChallengeHash(
	peerChallenge, authenticatorChallenge []byte,
	userName string,
) []byte {
	parts := strings.Split(userName, `\`)
	name := []byte(parts[len(parts)-1])
	seed := make([]byte, 0, len(peerChallenge)+len(authenticatorChallenge)+len(name))
	seed = append(seed, peerChallenge...)
	seed = append(seed, authenticatorChallenge...)
	seed = append(seed, name...)
	sum := sha1.Sum(seed)
	return append([]byte(nil), sum[:8]...)
}

// IsSuccess reports an MSCHAPv2 success opcode.
func IsSuccess(eapPacket []byte) bool {
	return len(eapPacket) >= 6 && eapPacket[4] == EapMSCHAPv2 &&
		eapPacket[5] == MschapSuccessOpcode
}

// IsFailure reports an MSCHAPv2 failure opcode.
func IsFailure(eapPacket []byte) bool {
	return len(eapPacket) >= 6 && eapPacket[4] == EapMSCHAPv2 &&
		eapPacket[5] == MschapFailureOpcode
}

// utf16le encodes a string the way Encoding.Unicode does on the C# side.
func utf16le(text string) []byte {
	out := make([]byte, 0, len(text)*2)
	for _, r := range text {
		if r > 0xFFFF {
			// Encode as a surrogate pair.
			r -= 0x10000
			high := 0xD800 + (r >> 10)
			low := 0xDC00 + (r & 0x3FF)
			out = binary.LittleEndian.AppendUint16(out, uint16(high))
			out = binary.LittleEndian.AppendUint16(out, uint16(low))
			continue
		}
		out = binary.LittleEndian.AppendUint16(out, uint16(r))
	}
	return out
}

func expandDesKey(key7 []byte, key8 []byte) {
	key8[0] = key7[0]
	key8[1] = (key7[0] << 7) | (key7[1] >> 1)
	key8[2] = (key7[1] << 6) | (key7[2] >> 2)
	key8[3] = (key7[2] << 5) | (key7[3] >> 3)
	key8[4] = (key7[3] << 4) | (key7[4] >> 4)
	key8[5] = (key7[4] << 3) | (key7[5] >> 5)
	key8[6] = (key7[5] << 2) | (key7[6] >> 6)
	key8[7] = key7[6] << 1
}

func setOddParity(key []byte) {
	for i := range key {
		value := key[i] & 0xFE
		parity := 0
		for bit := 0; bit < 8; bit++ {
			parity += int(value>>bit) & 1
		}
		if parity%2 == 0 {
			value |= 1
		}
		key[i] = value
	}
}
