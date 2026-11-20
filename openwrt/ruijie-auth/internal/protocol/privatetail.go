package protocol

import (
	"crypto/md5"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"net"
	"regexp"
	"strings"
)

var (
	marker               = []byte{0x00, 0x00, 0x13, 0x11}
	defaultClientName    = []byte("8021x.exe")
	defaultClientVersion = []byte{0x06, 0x56, 0x00, 0x00}
	defaultClientFlag    = []byte{0x01}
	macPattern           = regexp.MustCompile(`^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$`)
)

const (
	// AttrTag is the Ruijie vendor attribute tag.
	AttrTag byte = 0x1A
	// ControlFlag is the default private header flag.
	ControlFlag = 1
)

// NetworkInfo is the IPv4/DHCP information carried in the private header.
type NetworkInfo struct {
	IP       string
	Netmask  string
	Gateway  string
	DHCP     string
	IPv6     []byte
	IPString string
	StaticID []byte
}

// TailOptions controls the non-credential bytes of the private tail.
type TailOptions struct {
	ClientName    []byte
	ClientVersion []byte
	ClientFlag    []byte
	HeaderFlag    int
	Nonce         []byte
	Fingerprint   []byte
	ServiceName   []byte
}

// DefaultTailOptions returns a tail configuration with fresh random fields.
// Service name and static ID stay empty here: both come from the school
// profile, exactly like the C# PrivateTail.TailOptions defaults.
func DefaultTailOptions() *TailOptions {
	return (&TailOptions{}).withDefaults()
}

func (o *TailOptions) withDefaults() *TailOptions {
	out := *o
	if len(out.ClientName) == 0 {
		out.ClientName = append([]byte(nil), defaultClientName...)
	}
	if len(out.ClientVersion) == 0 {
		out.ClientVersion = append([]byte(nil), defaultClientVersion...)
	}
	if len(out.ClientFlag) == 0 {
		out.ClientFlag = append([]byte(nil), defaultClientFlag...)
	}
	if out.HeaderFlag == 0 {
		out.HeaderFlag = ControlFlag
	}
	if len(out.Nonce) == 0 {
		out.Nonce = RandomHex(16)
	}
	if len(out.Fingerprint) == 0 {
		out.Fingerprint = RandomHex(64)
	}
	// ServiceName stays as given: an empty value is zero-padded to 32 bytes
	// in BuildTail, matching C# TailOptions with no profile service name.
	return &out
}

// NormalizeMAC returns a lowercase colon-separated MAC, or "" when invalid.
func NormalizeMAC(value string) string {
	text := strings.ToLower(strings.TrimSpace(value))
	text = strings.ReplaceAll(text, "-", ":")
	if len(text) == 12 {
		if _, err := hex.DecodeString(text); err == nil {
			parts := make([]string, 6)
			for i := 0; i < 6; i++ {
				parts[i] = text[i*2 : i*2+2]
			}
			text = strings.Join(parts, ":")
		}
	}
	if !macPattern.MatchString(text) {
		return ""
	}
	return text
}

// RandomHex returns uppercase hexadecimal ASCII bytes from crypto/rand.
func RandomHex(byteCount int) []byte {
	buffer := make([]byte, byteCount)
	if _, err := rand.Read(buffer); err != nil {
		panic(fmt.Sprintf("crypto/rand: %v", err))
	}
	return []byte(strings.ToUpper(hex.EncodeToString(buffer)))
}

// CRC16XMODEM matches the private header checksum.
func CRC16XMODEM(data []byte) uint16 {
	var crc uint16
	for _, value := range data {
		crc ^= uint16(value) << 8
		for i := 0; i < 8; i++ {
			if crc&0x8000 != 0 {
				crc = (crc << 1) ^ 0x1021
			} else {
				crc <<= 1
			}
		}
	}
	return crc
}

// Obfuscate inverts every byte and reverses its bits, as the client does.
func Obfuscate(data []byte) []byte {
	out := make([]byte, len(data))
	for i, value := range data {
		v := ^value
		out[i] = byte(
			((v & 0x10) >> 1) |
				((v & 0x20) >> 3) |
				((v & 0x40) >> 5) |
				((v & 0x80) >> 7) |
				(2 * (v & 0x08)) |
				(8 * (v & 0x04)) |
				(32 * (v & 0x02)) |
				((v & 0x01) << 7))
	}
	return out
}

// Pad16 zero-pads to a 16-byte boundary; empty input becomes one block.
func Pad16(data []byte) []byte {
	if len(data) == 0 {
		return make([]byte, 16)
	}
	remainder := len(data) % 16
	if remainder == 0 {
		return append([]byte(nil), data...)
	}
	out := make([]byte, len(data)+16-remainder)
	copy(out, data)
	return out
}

// EncryptCredentials implements Ruijie's MD5-chained XOR cipher.
//
// MD5 is mandated by the Ruijie private tail (EAP-MD5 and the 0x2F attribute).
// It is wire compatibility, not a confidentiality primitive.
func EncryptCredentials(username, password, iv []byte) ([]byte, error) {
	if len(iv) != 16 {
		return nil, fmt.Errorf("IV 必须为 16 字节")
	}
	plain := Pad16(password)
	previous := iv
	out := make([]byte, 0, len(plain))
	for offset := 0; offset < len(plain); offset += 16 {
		seed := make([]byte, 0, len(username)+16)
		seed = append(seed, username...)
		seed = append(seed, previous...)
		sum := md5.Sum(seed)
		block := make([]byte, 16)
		for i := 0; i < 16; i++ {
			block[i] = plain[offset+i] ^ sum[i]
		}
		out = append(out, block...)
		previous = block
	}
	return out, nil
}

func ipBytes(value string) []byte {
	ip := net.ParseIP(strings.TrimSpace(value))
	if ip == nil {
		return []byte{0, 0, 0, 0}
	}
	v4 := ip.To4()
	if v4 == nil {
		return []byte{0, 0, 0, 0}
	}
	return append([]byte(nil), v4...)
}

func padded(value []byte, length int) []byte {
	out := make([]byte, length)
	copy(out, value)
	return out
}

func attribute(typ byte, payload []byte) ([]byte, error) {
	total := 8 + len(payload)
	if total > 0xFF {
		return nil, fmt.Errorf("私有属性 0x%02X 过长: %d", typ, total)
	}
	out := make([]byte, 2+4+2+len(payload))
	out[0] = AttrTag
	out[1] = byte(total)
	copy(out[2:6], marker)
	out[6] = typ
	out[7] = byte(2 + len(payload))
	copy(out[8:], payload)
	return out, nil
}

func header(network NetworkInfo, flag int) []byte {
	raw := make([]byte, 0, 21)
	raw = append(raw, marker...)
	raw = append(raw, byte(flag&0xFF))
	raw = append(raw, ipBytes(network.IP)...)
	raw = append(raw, ipBytes(network.Netmask)...)
	raw = append(raw, ipBytes(network.Gateway)...)
	raw = append(raw, ipBytes(network.DHCP)...)
	crc := CRC16XMODEM(raw)
	raw = append(raw, byte(crc>>8), byte(crc))
	return Obfuscate(raw)
}

// BuildTail builds the 569-byte Ruijie private tail for one EAP response.
func BuildTail(
	username, password []byte,
	mac string,
	network NetworkInfo,
	iv []byte,
	options *TailOptions,
) ([]byte, error) {
	macNormalized := NormalizeMAC(mac)
	if macNormalized == "" {
		return nil, fmt.Errorf("MAC 地址无效: %s", mac)
	}
	if len(iv) != 16 {
		return nil, fmt.Errorf("IV 必须为 16 字节")
	}
	if options == nil {
		options = DefaultTailOptions()
	} else {
		options = options.withDefaults()
	}

	macBytes, err := hex.DecodeString(strings.ReplaceAll(macNormalized, ":", ""))
	if err != nil {
		return nil, fmt.Errorf("解析 MAC 失败: %w", err)
	}
	encrypted, err := EncryptCredentials(username, password, iv)
	if err != nil {
		return nil, err
	}

	staticID := network.StaticID
	ipString := strings.TrimSpace(network.IPString)
	if ipString == "" {
		ipString = strings.TrimSpace(network.IP)
	}
	if ipString == "" {
		ipString = "0.0.0.0"
	}

	attrs := make([]byte, 0, 512)
	add := func(typ byte, payload []byte) error {
		encoded, err := attribute(typ, payload)
		if err != nil {
			return err
		}
		attrs = append(attrs, encoded...)
		return nil
	}
	if err := add(0x17, options.Nonce); err != nil {
		return nil, err
	}
	if err := add(0x18, make([]byte, 4)); err != nil {
		return nil, err
	}
	if err := add(0x2D, macBytes); err != nil {
		return nil, err
	}
	if err := add(0x2F, encrypted); err != nil {
		return nil, err
	}
	if err := add(0x35, []byte{0x01}); err != nil {
		return nil, err
	}
	if err := add(0x36, make([]byte, 16)); err != nil {
		return nil, err
	}
	if err := add(0x38, padded(network.IPv6, 16)); err != nil {
		return nil, err
	}
	if err := add(0x4E, make([]byte, 16)); err != nil {
		return nil, err
	}
	if err := add(0x4D, options.Fingerprint); err != nil {
		return nil, err
	}
	if err := add(0x39, padded(options.ServiceName, 32)); err != nil {
		return nil, err
	}
	if err := add(0x54, padded(staticID, 64)); err != nil {
		return nil, err
	}
	if err := add(0x62, []byte{0x00}); err != nil {
		return nil, err
	}
	if err := add(0x6B, []byte{0x00}); err != nil {
		return nil, err
	}
	if err := add(0x7E, []byte{0x00, 0x00, 0x01, 0x00, 0x00, 0x0C, 0x00, 0x00}); err != nil {
		return nil, err
	}
	if err := add(0x70, []byte{0x40}); err != nil {
		return nil, err
	}
	if err := add(0x6F, []byte{0x00}); err != nil {
		return nil, err
	}
	if err := add(0x79, []byte{0x02}); err != nil {
		return nil, err
	}
	if err := add(0x76, []byte(ipString)); err != nil {
		return nil, err
	}

	preamble := make([]byte, 0, 47)
	preamble = append(preamble, marker...)
	preamble = append(preamble, padded(options.ClientName, 32)...)
	preamble = append(preamble, options.ClientVersion...)
	preamble = append(preamble, options.ClientFlag...)
	preamble = append(preamble, marker...)
	preamble = append(preamble, byte(len(attrs)>>8), byte(len(attrs)))

	out := make([]byte, 0, 23+len(preamble)+len(attrs))
	out = append(out, header(network, options.HeaderFlag)...)
	out = append(out, preamble...)
	out = append(out, attrs...)
	return out, nil
}
