// Package dircomm implements the SAM direct-comm heartbeat recovered from
// the official 8021x.exe engine (see docs/protocols/dircomm-heartbeat.md).
//
// After 802.1X succeeds the official client keeps a UDP channel to the SAM
// server: it binds MSG_CLIENT_PORT (private property 0x6E), sends an
// encrypted heartbeat every HELLO_INTERV (0x59) to
// INDICATE_SERV_IP:INDICATE_PORT (0x5B/0x5A), and re-authenticates once the
// heartbeat stops being answered. The key/IV are the ENCRYPT_KEY/ENCRYPT_IV
// properties (0x5C/0x5D) delivered by the same authentication.
//
// Wire format, all recovered statically from sub_4a2a30/sub_4a6e30:
//
//	plaintext = TLV(01, [02])              protocol version 2
//	          + TLV(03, service-name GBK)   adapter service name
//	          + TLV(04, IPv4)               client address
//	          + TLV(05, MAC)                client hardware address
//	          + TLV(14, table entry)        128-byte anti-replay blob
//	TLV = [type:1][len:1][value:len]
//
// The blob is selected by hashing htonl(seq) plus a 16-byte slot that the
// official binary leaves zero by default (0x755818): index =
// (sum(header)*0x2d7) mod 53 into the 53x128 table at 0x7343c0. The
// plaintext is zero-padded to a multiple of 8 and DES-CBC encrypted with
// the delivered key/IV; only the ciphertext goes on the wire.
package dircomm

import (
	"crypto/cipher"
	"crypto/des"
	_ "embed"
	"encoding/binary"
	"errors"
	"fmt"
	"net"
	"strconv"
	"sync"
	"time"
)

//go:embed table.bin
var obfuscationTable []byte

const (
	// tableEntries x tableEntrySize must match the embedded table.
	tableEntries  = 53
	tableEntrySize = 128

	// selectorHashMultiplier and selectorModulus mirror the constants in
	// sub_4a6e30 (imul 0x2d7 / div 0x35).
	selectorHashMultiplier = 0x2d7
	selectorModulus        = 0x35

	// selectorMagic is the 16-byte .data slot at 0x755818. The official
	// binary ships it zeroed; a runtime writer was not identified.
	selectorMagicSize = 16

	// protocolVersion is TLV 0x01's value (dircomm.log: 直通协议版本 2).
	protocolVersion = 2

	// DefaultRetry / DefaultTimeout mirror dircomm.log (重传 2 次,
	// 超时 2000 ms). ReauthThreshold mirrors the global counter checked
	// before the "直通心跳超时重认证" path.
	DefaultInterval      = 30 * time.Second
	DefaultRetry         = 2
	DefaultTimeout       = 2 * time.Second
	ReauthThreshold      = 3
)

// Config carries the per-session heartbeat inputs extracted from the
// authentication properties plus the adapter identity.
type Config struct {
	ServiceName []byte // GBK-encoded service name (TLV 0x03)
	IP          []byte // 4-byte IPv4 (TLV 0x04)
	MAC         []byte // 6-byte MAC (TLV 0x05)
	Key         []byte // 8-byte DES key (ENCRYPT_KEY)
	IV          []byte // 8-byte DES IV (ENCRYPT_IV)
}

// Validate reports whether every heartbeat input is present and sized.
func (c Config) Validate() error {
	if len(c.ServiceName) == 0 {
		return errors.New("dircomm: service name is empty")
	}
	if len(c.ServiceName) > 255 {
		return errors.New("dircomm: service name exceeds one length byte")
	}
	if len(c.IP) != 4 {
		return fmt.Errorf("dircomm: IPv4 must be 4 bytes, got %d", len(c.IP))
	}
	if len(c.MAC) != 6 {
		return fmt.Errorf("dircomm: MAC must be 6 bytes, got %d", len(c.MAC))
	}
	if len(c.Key) != des.BlockSize {
		return fmt.Errorf("dircomm: DES key must be %d bytes, got %d", des.BlockSize, len(c.Key))
	}
	if len(c.IV) != des.BlockSize {
		return fmt.Errorf("dircomm: DES IV must be %d bytes, got %d", des.BlockSize, len(c.IV))
	}
	return nil
}

// SelectorIndex mirrors the blob selection in sub_4a6e30: a 20-byte header
// of htonl(seq) followed by the (zero) magic slot is summed, multiplied by
// 0x2d7 and reduced mod 53.
func SelectorIndex(seq uint32) int {
	header := make([]byte, 4+selectorMagicSize)
	binary.BigEndian.PutUint32(header, seq)
	sum := 0
	for _, b := range header {
		sum += int(b)
	}
	return (sum * selectorHashMultiplier) % selectorModulus
}

// TableEntry returns the 128-byte anti-replay blob for seq.
func TableEntry(seq uint32) ([]byte, error) {
	index := SelectorIndex(seq)
	if index < 0 || index >= tableEntries {
		return nil, fmt.Errorf("dircomm: selector index %d out of range", index)
	}
	start := index * tableEntrySize
	entry := make([]byte, tableEntrySize)
	copy(entry, obfuscationTable[start:start+tableEntrySize])
	return entry, nil
}

func tlv(typ byte, value []byte) ([]byte, error) {
	if len(value) > 255 {
		return nil, fmt.Errorf("dircomm: TLV 0x%02x value too long: %d", typ, len(value))
	}
	out := make([]byte, 0, 2+len(value))
	out = append(out, typ, byte(len(value)))
	return append(out, value...), nil
}

// Plaintext builds the unencrypted heartbeat body for seq: the four fixed
// TLVs plus the selected 128-byte table entry.
func Plaintext(cfg Config, seq uint32) ([]byte, error) {
	entry, err := TableEntry(seq)
	if err != nil {
		return nil, err
	}
	var body []byte
	for _, field := range []struct {
		typ   byte
		value []byte
	}{
		{0x01, []byte{protocolVersion}},
		{0x03, cfg.ServiceName},
		{0x04, cfg.IP},
		{0x05, cfg.MAC},
		{0x14, entry},
	} {
		segment, err := tlv(field.typ, field.value)
		if err != nil {
			return nil, err
		}
		body = append(body, segment...)
	}
	// DES operates on whole blocks; the official sender zero-pads.
	if rest := len(body) % des.BlockSize; rest != 0 {
		body = append(body, make([]byte, des.BlockSize-rest)...)
	}
	return body, nil
}

// Encrypt DES-CBC encrypts the padded plaintext, matching sub_4a9380 →
// sub_55b0d0 (block[i] = DES(block[i-1] ^ block[i]), chain starts at IV).
func Encrypt(key, iv, plaintext []byte) ([]byte, error) {
	if len(key) != des.BlockSize || len(iv) != des.BlockSize {
		return nil, errors.New("dircomm: DES key/IV must be 8 bytes")
	}
	if len(plaintext)%des.BlockSize != 0 {
		return nil, errors.New("dircomm: plaintext must be 8-byte aligned")
	}
	block, err := des.NewCipher(key)
	if err != nil {
		return nil, err
	}
	ciphertext := make([]byte, len(plaintext))
	cipher.NewCBCEncrypter(block, iv).CryptBlocks(ciphertext, plaintext)
	return ciphertext, nil
}

// BuildHeartbeat assembles the full datagram for seq.
func BuildHeartbeat(cfg Config, seq uint32) ([]byte, error) {
	if err := cfg.Validate(); err != nil {
		return nil, err
	}
	plaintext, err := Plaintext(cfg, seq)
	if err != nil {
		return nil, err
	}
	return Encrypt(cfg.Key, cfg.IV, plaintext)
}

// Event is what the monitor reports back to the session loop.
type Event struct {
	// Reauth asks the caller to restart authentication: the heartbeat has
	// been unanswered past ReauthThreshold rounds.
	Reauth bool
	// Message is a human-readable log line (heartbeat result, SAM push).
	Message string
}

// MonitorOptions configures the runtime heartbeat loop.
type MonitorOptions struct {
	Config   Config
	Server   string // "ip:port" — INDICATE_SERV_IP:INDICATE_PORT
	LocalPort int   // MSG_CLIENT_PORT, the UDP source port to bind
	Interval time.Duration
	Retry    int
	Timeout  time.Duration
	// Log receives human-readable status lines (optional).
	Log func(string)
}

// Monitor runs the heartbeat loop on its own goroutine until Stop.
type Monitor struct {
	opts    MonitorOptions
	conn    *net.UDPConn
	events  chan Event
	stopped chan struct{}
	once    sync.Once
	wg      sync.WaitGroup
}

// NewMonitor binds the local UDP port and prepares the loop.
func NewMonitor(opts MonitorOptions) (*Monitor, error) {
	if err := opts.Config.Validate(); err != nil {
		return nil, err
	}
	if _, _, err := net.SplitHostPort(opts.Server); err != nil {
		return nil, fmt.Errorf("dircomm: invalid server address %q: %w", opts.Server, err)
	}
	if opts.Interval <= 0 {
		opts.Interval = DefaultInterval
	}
	if opts.Retry <= 0 {
		opts.Retry = DefaultRetry
	}
	if opts.Timeout <= 0 {
		opts.Timeout = DefaultTimeout
	}
	local := &net.UDPAddr{Port: opts.LocalPort}
	conn, err := net.ListenUDP("udp4", local)
	if err != nil {
		return nil, fmt.Errorf("dircomm: bind UDP %d: %w", opts.LocalPort, err)
	}
	return &Monitor{
		opts:    opts,
		conn:    conn,
		events:  make(chan Event, 8),
		stopped: make(chan struct{}),
	}, nil
}

// Events exposes the heartbeat outcome channel. The session loop selects on
// it alongside its own channels.
func (m *Monitor) Events() <-chan Event {
	return m.events
}

func (m *Monitor) log(message string) {
	if m.opts.Log != nil {
		m.opts.Log(message)
	}
}

// Start launches the heartbeat goroutine.
func (m *Monitor) Start() {
	m.wg.Add(1)
	go m.run()
}

// Stop terminates the loop and releases the socket. Safe to call twice.
func (m *Monitor) Stop() {
	m.once.Do(func() {
		close(m.stopped)
		_ = m.conn.Close()
	})
	m.wg.Wait()
}

func (m *Monitor) run() {
	defer m.wg.Done()
	var seq uint32
	failures := 0
	deadline := time.NewTimer(m.opts.Interval)
	defer deadline.Stop()
	for {
		select {
		case <-m.stopped:
			return
		case <-deadline.C:
		}
		seq++
		ok := m.beat(seq)
		if ok {
			if failures > 0 {
				m.log(fmt.Sprintf("SAM 心跳恢复（此前连续失败 %d 次）", failures))
			}
			failures = 0
		} else {
			failures++
			m.log(fmt.Sprintf("SAM 心跳无响应（连续 %d 次）", failures))
			if failures >= ReauthThreshold {
				m.emit(Event{
					Reauth:  true,
					Message: "SAM 直通心跳超时，触发重新认证",
				})
				return
			}
		}
		deadline.Reset(m.opts.Interval)
	}
}

func (m *Monitor) emit(event Event) {
	select {
	case m.events <- event:
	default:
	}
}

// beat sends one heartbeat and waits for any reply within the timeout,
// retrying per the official retry count. Any datagram back counts as a
// response (SAM push payloads are logged but not decoded yet).
func (m *Monitor) beat(seq uint32) bool {
	packet, err := BuildHeartbeat(m.opts.Config, seq)
	if err != nil {
		m.log("SAM 心跳构建失败: " + err.Error())
		return false
	}
	for attempt := 0; attempt <= m.opts.Retry; attempt++ {
		if _, err := m.conn.Write(packet); err != nil {
			m.log("SAM 心跳发送失败: " + err.Error())
			return false
		}
		if reply, err := m.readWithTimeout(); err == nil {
			if len(reply) > 0 {
				m.log(fmt.Sprintf("SAM 推送 %d 字节（未解析）", len(reply)))
			}
			return true
		}
		select {
		case <-m.stopped:
			return false
		case <-time.After(m.opts.Retry * 0):
		}
	}
	return false
}

// readWithTimeout waits for one datagram until the timeout elapses.
func (m *Monitor) readWithTimeout() ([]byte, error) {
	buf := make([]byte, 1500)
	_ = m.conn.SetReadDeadline(time.Now().Add(m.opts.Timeout))
	for {
		n, _, err := m.conn.ReadFromUDP(buf)
		if err != nil {
			return nil, err
		}
		if n > 0 {
			return buf[:n], nil
		}
	}
}

// ParseServerAddress converts the INDICATE_SERV_IP private property (a
// big-endian uint32 such as 2887125761) plus the INDICATE_PORT number into
// a dialable "ip:port".
func ParseServerAddress(serverIP uint32, port int64) (string, error) {
	if port <= 0 || port > 65535 {
		return "", fmt.Errorf("dircomm: invalid port %d", port)
	}
	ip := make(net.IP, 4)
	binary.BigEndian.PutUint32(ip, serverIP)
	return net.JoinHostPort(ip.String(), strconv.FormatInt(port, 10)), nil
}
