package protocol

import (
	"encoding/hex"
	"fmt"
	"strings"
)

// PrivatePropertyType enumerates the server private-property types the
// official 8021x engine understands (SuConfigType in 8021x.exe).
type PrivatePropertyType byte

const (
	SuNewestVersion                  PrivatePropertyType = 5
	ProxyAvoid                       PrivatePropertyType = 20
	DialupAvoid                      PrivatePropertyType = 21
	SuUpgradeURL                     PrivatePropertyType = 55
	AccountInfo                      PrivatePropertyType = 60
	PersonalInfo                     PrivatePropertyType = 61
	ServerListUpdate                 PrivatePropertyType = 66
	BroadcastInfo                    PrivatePropertyType = 82
	RejectReason                     PrivatePropertyType = 83
	SuReauthInterval                 PrivatePropertyType = 86
	HelloInterval                    PrivatePropertyType = 89
	IndicatePort                     PrivatePropertyType = 90
	IndicateServerIP                 PrivatePropertyType = 91
	EncryptKey                       PrivatePropertyType = 92
	EncryptIV                        PrivatePropertyType = 93
	ServerUTCTime                    PrivatePropertyType = 94
	RadiusServerType                 PrivatePropertyType = 97
	ServerListUpdateLegacy           PrivatePropertyType = 102
	UserLoginURL                     PrivatePropertyType = 104
	MessageClientPort                PrivatePropertyType = 110
	UtrustURL                        PrivatePropertyType = 114
	SMPIPAddress                     PrivatePropertyType = 115
	ProxyDetectKinds                 PrivatePropertyType = 116
	ClientMessagePeriod              PrivatePropertyType = 117
	AutoOpenSSOURL                   PrivatePropertyType = 119
	DirectCommunicationHighestVer    PrivatePropertyType = 121
	DirectCommunicationHeartbeatFlag PrivatePropertyType = 128
	WifiConnectionCountFlags         PrivatePropertyType = 152
)

const (
	// MaximumPrivatePropertyPacket is the payload cap enforced by the
	// original parser.
	MaximumPrivatePropertyPacket = 0x578
	// MaximumPrivatePropertyValue is the largest declared value length.
	MaximumPrivatePropertyValue = 0xfe
)

// Rc4Key is the fixed RC4 key for ENCRYPT_KEY / ENCRYPT_IV.
var Rc4Key = []byte("com.ruijie.www")

// PrivateProperty is one decoded TLV, mirroring the C# record.
type PrivateProperty struct {
	Type   byte    `json:"type"`
	Name   string  `json:"name"`
	Raw    []byte  `json:"-"`
	Text   *string `json:"text"`
	Number *int64  `json:"number"`
	Hex    string  `json:"hex"`
}

// DisplayValue returns Text, then Number, then Hex — the C# precedence.
func (p PrivateProperty) DisplayValue() string {
	if p.Text != nil {
		return *p.Text
	}
	if p.Number != nil {
		return fmt.Sprintf("%d", *p.Number)
	}
	return p.Hex
}

// PrivatePropertiesResult is the full parse outcome.
type PrivatePropertiesResult struct {
	Properties         []PrivateProperty `json:"properties"`
	Messages           []string          `json:"messages"`
	Section2Value      *int64            `json:"section2_value"`
	Section5Hash       *int64            `json:"section5_hash"`
	Section5IntervalMs *int64            `json:"section5_interval_ms"`
	ConsumedBytes      int               `json:"consumed_bytes"`
	Errors             []string          `json:"errors"`
}

var privatePropertyMarker = []byte{0x00, 0x00, 0x13, 0x11}

var stringPropertyTypes = map[byte]bool{
	byte(SuUpgradeURL):           true,
	byte(AccountInfo):            true,
	byte(PersonalInfo):           true,
	byte(ServerListUpdate):       true,
	byte(BroadcastInfo):          true,
	byte(RejectReason):           true,
	byte(ServerListUpdateLegacy): true,
	byte(UserLoginURL):           true,
	byte(UtrustURL):              true,
}

var privatePropertyNames = map[byte]string{
	byte(SuNewestVersion):                  "SU_NEWEST_VER",
	byte(ProxyAvoid):                       "PROXY_AVOID",
	byte(DialupAvoid):                      "DIALUP_AVOID",
	byte(SuUpgradeURL):                     "SU_UPGRADE_URL",
	byte(AccountInfo):                      "ACCOUNT_INFO",
	byte(PersonalInfo):                     "PERSIONAL_INFO",
	byte(ServerListUpdate):                 "SERVER_LIST_UPDATE",
	byte(BroadcastInfo):                    "BROADCASE_INFO",
	byte(RejectReason):                     "REJECT_REASON",
	byte(SuReauthInterval):                 "SU_REAUTH_INTERV",
	byte(HelloInterval):                    "HELLO_INTERV",
	byte(IndicatePort):                     "INDICATE_PORT",
	byte(IndicateServerIP):                 "INDICATE_SERV_IP",
	byte(EncryptKey):                       "ENCRYPT_KEY",
	byte(EncryptIV):                        "ENCRYPT_IV",
	byte(ServerUTCTime):                    "SERVER_UTC_TIME",
	byte(RadiusServerType):                 "RADIUS_SERV_TYPE",
	byte(ServerListUpdateLegacy):           "SERVER_LIST_UPDATE_LEGACY",
	byte(UserLoginURL):                     "USER_LOGIN_URL",
	byte(MessageClientPort):                "MSG_CLIENT_PORT",
	byte(UtrustURL):                        "UTRUST_URL",
	byte(SMPIPAddress):                     "SMP_IPADDRESS",
	byte(ProxyDetectKinds):                 "PROXY_DECTECT_KINDS",
	byte(ClientMessagePeriod):              "CLIENT_MSG_PERIOD",
	byte(AutoOpenSSOURL):                   "AUTO_OPEN_SSO_URL",
	byte(DirectCommunicationHighestVer):    "DIRECT_COMM_HIGHEST_VERSION",
	byte(DirectCommunicationHeartbeatFlag): "DIRECT_COMM_HEARTBEAT_FLAGS",
	byte(WifiConnectionCountFlags):         "WIFI_CNNT_NUM_FLAGS",
}

// PrivatePropertyName returns the vendor name for a property type.
func PrivatePropertyName(typ byte) string {
	if name, ok := privatePropertyNames[typ]; ok {
		return name
	}
	return fmt.Sprintf("UNKNOWN_0x%02X", typ)
}

// ParsePrivateProperties decodes the block carried by EAP-Success/EAP-Failure.
//
// It is a byte-for-byte port of the C# parser, which in turn matches the
// original 8021x.exe ParsePrivateProperty (0x537ee0):
//
//	[00 00 13 11][2B len][GBK message]                    section 1
//	[00 00 13 11][2B][4B value][...][1B count][count*32]  section 2
//	[00 00 13 11][ver][ver][4B]                           section 3
//	[00 00 13 11][ver][ver][4B]                           section 4
//	[15 obfuscated bytes]                                 section 5
//	[00 00 13 11][type][len=value+2][value] ...           TLV loop
func ParsePrivateProperties(payload []byte) PrivatePropertiesResult {
	result := PrivatePropertiesResult{
		Properties: []PrivateProperty{},
		Messages:   []string{},
		Errors:     []string{},
	}
	data := payload
	total := len(data)
	if total > MaximumPrivatePropertyPacket {
		result.Errors = append(result.Errors,
			fmt.Sprintf("私有属性长度 %d 超过上限 %d", total, MaximumPrivatePropertyPacket))
		return result
	}
	pos := 0

	// Section 1: marker + 2B length + GBK message.
	if total < 6 || !privateMarkerAt(data, 0) {
		result.Errors = append(result.Errors, "缺少段 1 标记")
		return result
	}
	messageLength := int(readUint16BE(data, 4))
	if messageLength+6 > total {
		result.Errors = append(result.Errors,
			fmt.Sprintf("段 1 长度 %d 超出数据范围", messageLength))
		return result
	}
	message := decodePrivateText(data[6 : 6+messageLength])
	if strings.TrimSpace(message) != "" {
		result.Messages = append(result.Messages, message)
	}
	pos = 6 + messageLength

	// Section 2: fixed 0x6F layout with a count of 32-byte entries.
	if pos+0x6F <= total {
		if !privateMarkerAt(data, pos) {
			result.Errors = append(result.Errors,
				fmt.Sprintf("偏移 %d 处缺少段 2 标记", pos))
			result.ConsumedBytes = pos
			return result
		}
		pos += 6
		value := int64(readUint32BE(data, pos))
		result.Section2Value = &value
		count := int(data[pos+0x68])
		pos += 0x69
		if count*0x20+pos <= total {
			pos += count * 0x20
		} else {
			result.Errors = append(result.Errors,
				fmt.Sprintf("段 2 条目数 %d 超出数据范围", count))
			result.ConsumedBytes = pos
			return result
		}
	} else {
		result.ConsumedBytes = pos
		return result
	}

	// Sections 3/4: marker + version + sub, version must be 1 and sub < 2.
	for section := 3; section <= 4; section++ {
		if pos+6 > total {
			result.ConsumedBytes = pos
			return result
		}
		if !privateMarkerAt(data, pos) {
			result.Errors = append(result.Errors,
				fmt.Sprintf("偏移 %d 处缺少段 %d 标记", pos, section))
			result.ConsumedBytes = pos
			return result
		}
		version := data[pos+4]
		sub := data[pos+5]
		if version == 1 && sub < 2 {
			pos += 6
		} else {
			result.Errors = append(result.Errors,
				fmt.Sprintf("段 %d 版本 %d/%d 被拒绝", section, version, sub))
			result.ConsumedBytes = pos
			return result
		}
	}

	// Section 5: 15 obfuscated bytes, de-obfuscated in place.
	if pos+15 > total {
		result.ConsumedBytes = pos
		return result
	}
	plain := Obfuscate(data[pos : pos+15])
	if !privateMarkerAt(plain, 0) {
		result.Errors = append(result.Errors,
			fmt.Sprintf("段 5 解混淆后标记不匹配: %s",
				strings.ToUpper(hex.EncodeToString(plain))))
		result.ConsumedBytes = pos
		return result
	}
	if plain[4] != 0x0A {
		result.Errors = append(result.Errors,
			fmt.Sprintf("段 5 门控字节 %02X != 0x0A", plain[4]))
		result.ConsumedBytes = pos
		return result
	}
	hash := int64(readUint32BE(plain, 6))
	// The C# parser multiplies in 32-bit and then widens, so keep the same
	// wrap behaviour for adversarial interval values.
	interval := int64(readUint32BE(plain, 10) * 1000)
	result.Section5Hash = &hash
	result.Section5IntervalMs = &interval
	pos += 15

	// Property TLV loop: [1311][type][len=value+2][value].
	for pos+6 <= total {
		if !privateMarkerAt(data, pos) {
			break
		}
		typ := data[pos+4]
		declared := int(data[pos+5])
		if declared < 2 {
			result.Errors = append(result.Errors,
				fmt.Sprintf("偏移 %d 处类型 %d 长度 %d 过小", pos, typ, declared))
			break
		}
		valueLength := declared - 2
		if pos+6+valueLength > total {
			result.Errors = append(result.Errors,
				fmt.Sprintf("偏移 %d 处类型 %d 超出数据范围", pos, typ))
			break
		}
		value := append([]byte(nil), data[pos+6:pos+6+valueLength]...)
		pos += 6 + valueLength
		result.Properties = append(result.Properties, decodePrivateProperty(typ, value))
	}

	if pos != total {
		result.Errors = append(result.Errors,
			fmt.Sprintf("尾部残留 %d 字节", total-pos))
	}
	result.ConsumedBytes = pos
	return result
}

func decodePrivateProperty(typ byte, value []byte) PrivateProperty {
	property := PrivateProperty{
		Type: typ,
		Name: PrivatePropertyName(typ),
		Raw:  value,
		Hex:  hex.EncodeToString(value),
	}
	if typ == byte(EncryptKey) || typ == byte(EncryptIV) {
		decoded := RC4Transform(value, Rc4Key)
		property.Raw = decoded
		property.Hex = hex.EncodeToString(decoded)
		return property
	}
	if stringPropertyTypes[typ] {
		text := decodePrivateText(value)
		property.Text = &text
		return property
	}
	property.Number = decodePrivateNumber(value)
	return property
}

func decodePrivateText(value []byte) string {
	if len(value) == 0 {
		return ""
	}
	text, err := GB18030Decode(value)
	if err != nil {
		return latin1String(value)
	}
	return strings.Trim(text, "\x00")
}

func decodePrivateNumber(value []byte) *int64 {
	var number int64
	switch len(value) {
	case 1:
		number = int64(value[0])
	case 2:
		// C# promotes both bytes to int before ORing, so 2-byte values stay
		// unsigned (0..65535).
		number = int64(uint16(value[0])<<8 | uint16(value[1]))
	case 4:
		// C# widens each byte to long before shifting, so 4-byte values are
		// always non-negative here (unlike readUint32BE).
		number = int64(uint32(value[0])<<24 | uint32(value[1])<<16 |
			uint32(value[2])<<8 | uint32(value[3]))
	case 8:
		number = int64(uint64(value[0])<<56 | uint64(value[1])<<48 |
			uint64(value[2])<<40 | uint64(value[3])<<32 |
			uint64(value[4])<<24 | uint64(value[5])<<16 |
			uint64(value[6])<<8 | uint64(value[7]))
	default:
		return nil
	}
	return &number
}

func latin1String(value []byte) string {
	runes := make([]rune, len(value))
	for i, b := range value {
		runes[i] = rune(b)
	}
	return string(runes)
}

func privateMarkerAt(data []byte, offset int) bool {
	return offset+4 <= len(data) &&
		data[offset] == privatePropertyMarker[0] &&
		data[offset+1] == privatePropertyMarker[1] &&
		data[offset+2] == privatePropertyMarker[2] &&
		data[offset+3] == privatePropertyMarker[3]
}

func readUint16BE(data []byte, offset int) uint16 {
	return uint16(data[offset])<<8 | uint16(data[offset+1])
}

func readUint32BE(data []byte, offset int) int32 {
	return int32(uint32(data[offset])<<24 | uint32(data[offset+1])<<16 |
		uint32(data[offset+2])<<8 | uint32(data[offset+3]))
}
