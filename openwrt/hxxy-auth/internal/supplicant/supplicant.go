package supplicant

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/protocol"
	"github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth/internal/transport"
)

// Kind classifies the end of a session.
type Kind int

const (
	KindFailure Kind = iota
	KindTransport
	KindNoServer
	KindTimeout
	KindPaused
	KindLogoff
	KindReload
	KindCanceled
)

// Result is returned when a session stops holding.
type Result struct {
	Kind          Kind
	ServerMAC     string
	SourceMAC     string
	Connected     bool
	Authenticated bool
	Messages      []string
	Err           error
}

// StatusFunc receives state transitions.
type StatusFunc func(state, message, server string)

// Options controls one resident supplicant session.
type Options struct {
	Device         string
	Username       string
	Password       string
	ServiceName    string
	StaticID       string
	Timeout        time.Duration
	Retries        int
	Repeats        int
	StartTimeout   time.Duration
	ResponseDelay  time.Duration
	ReauthInterval time.Duration
	CheckInterval  time.Duration
	Preempt        bool
	Autoguard      bool
	LogoffOnStop   bool
	LockedServer   string
	Network        func() protocol.NetworkInfo
	Probe          func(context.Context) bool
	Status         StatusFunc
}

// Session runs one account until it fails, pauses, is reloaded, or is
// cancelled.
type Session struct {
	opts   Options
	opener transport.Opener
}

// New creates a session.
func New(opts Options, opener transport.Opener) *Session {
	if opener == nil {
		opener = transport.Open
	}
	return &Session{opts: opts, opener: opener}
}

func (s *Session) status(state, message, server string) {
	if s.opts.Status != nil {
		s.opts.Status(state, message, server)
	}
}

// Run opens the raw transport and holds the session.
func (s *Session) Run(ctx context.Context, commands <-chan string) Result {
	opts := s.opts
	if opts.Timeout <= 0 {
		opts.Timeout = 20 * time.Second
	}
	if opts.StartTimeout <= 0 {
		opts.StartTimeout = 3 * time.Second
	}
	if opts.Retries <= 0 {
		opts.Retries = 4
	}
	if opts.Repeats <= 0 {
		opts.Repeats = 3
	}
	if opts.ResponseDelay <= 0 {
		opts.ResponseDelay = 40 * time.Millisecond
	}

	raw, err := s.opener(opts.Device)
	if err != nil {
		return Result{Kind: KindTransport, Err: err}
	}
	defer raw.Close()
	time.Sleep(300 * time.Millisecond)

	source := protocol.NormalizeMAC(raw.HardwareAddr())
	if source == "" {
		return Result{Kind: KindTransport, Err: fmt.Errorf("接口没有有效 MAC: %s", opts.Device)}
	}
	identity, err := protocol.GB18030Encode(opts.Username)
	if err != nil {
		return Result{Kind: KindFailure, SourceMAC: source, Err: err}
	}
	password, err := protocol.GB18030Encode(opts.Password)
	if err != nil {
		return Result{Kind: KindFailure, SourceMAC: source, Err: err}
	}
	serviceName, err := protocol.GB18030Encode(opts.ServiceName)
	if err != nil {
		serviceName = protocol.DefaultServiceName()
	}
	network := protocol.NetworkInfo{}
	if opts.Network != nil {
		network = opts.Network()
	}
	if opts.StaticID != "" {
		network.StaticID = []byte(opts.StaticID)
	}
	locked := protocol.NormalizeMAC(opts.LockedServer)
	sessionNonce := protocol.RandomHex(16)

	send := func(frame []byte) error {
		for i := 0; i < opts.Repeats; i++ {
			if err := raw.Send(frame); err != nil {
				return err
			}
		}
		return nil
	}

	controlTail := func() ([]byte, error) {
		return protocol.BuildTail(identity, password, source, network, make([]byte, 16),
			&protocol.TailOptions{
				HeaderFlag:  1,
				Nonce:       sessionNonce,
				Fingerprint: make([]byte, 128),
				ClientFlag:  []byte{0x01},
				ServiceName: serviceName,
			})
	}
	identityTail := func() ([]byte, error) {
		return protocol.BuildTail(identity, password, source, network, make([]byte, 16),
			&protocol.TailOptions{
				HeaderFlag:  1,
				Nonce:       sessionNonce,
				ServiceName: serviceName,
			})
	}
	md5Tail := func(challenge []byte) ([]byte, error) {
		return protocol.BuildTail(identity, password, source, network, challenge,
			&protocol.TailOptions{
				HeaderFlag:  1,
				ServiceName: serviceName,
			})
	}

	sendControl := func(typ byte, destination string) error {
		tail, err := controlTail()
		if err != nil {
			return err
		}
		frame, err := protocol.BuildControl(source, destination, typ, tail)
		if err != nil {
			return err
		}
		return send(frame)
	}
	sendStart := func() error {
		destination := protocol.RuijieGroup
		if locked != "" {
			destination = locked
		}
		return sendControl(1, destination)
	}
	sendLogoff := func() error {
		return sendControl(2, protocol.StandardGroup)
	}

	result := func(kind Kind, server string, connected, authenticated bool, messages []string, err error) Result {
		return Result{
			Kind:          kind,
			ServerMAC:     server,
			SourceMAC:     source,
			Connected:     connected,
			Authenticated: authenticated,
			Messages:      messages,
			Err:           err,
		}
	}

	if err := sendLogoff(); err != nil {
		return result(KindTransport, "", false, false, nil, err)
	}
	time.Sleep(100 * time.Millisecond)
	if err := sendStart(); err != nil {
		return result(KindTransport, "", false, false, nil, err)
	}
	s.status("authenticating", "正在寻找认证服务器...", "")

	authTimer := time.NewTimer(opts.Timeout)
	startTimer := time.NewTimer(opts.StartTimeout)
	defer authTimer.Stop()
	defer startTimer.Stop()

	authenticated := false
	connected := false
	serverMAC := ""
	startSent := 1
	handled := make(map[string]time.Time)
	var reauthC <-chan time.Time
	var checkC <-chan time.Time

	stopTimer := func(timer *time.Timer) {
		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}
	}
	resetTimer := func(timer *time.Timer, duration time.Duration) {
		stopTimer(timer)
		timer.Reset(duration)
	}
	onAuthenticated := func(server string) {
		stopTimer(authTimer)
		stopTimer(startTimer)
		if opts.Preempt && opts.ReauthInterval > 0 {
			reauthC = time.After(opts.ReauthInterval)
		} else {
			reauthC = nil
		}
		if opts.Autoguard && opts.Probe != nil && opts.CheckInterval > 0 {
			checkC = time.After(opts.CheckInterval)
		} else {
			checkC = nil
		}
	}
	restartAuth := func(message string) error {
		authenticated = false
		connected = false
		serverMAC = ""
		startSent = 1
		reauthC = nil
		if err := sendLogoff(); err != nil {
			return err
		}
		time.Sleep(100 * time.Millisecond)
		if err := sendStart(); err != nil {
			return err
		}
		resetTimer(authTimer, opts.Timeout)
		resetTimer(startTimer, opts.StartTimeout)
		s.status("authenticating", message, "")
		return nil
	}

	for {
		select {
		case <-ctx.Done():
			if opts.LogoffOnStop {
				_ = sendLogoff()
			}
			return result(KindCanceled, serverMAC, connected, authenticated, nil, nil)

		case command := <-commands:
			switch command {
			case "auth", "resume":
				if err := restartAuth("正在重新认证..."); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, err)
				}
			case "logoff":
				_ = sendLogoff()
				s.status("idle", "已发送 EAPOL-Logoff", serverMAC)
				return result(KindLogoff, serverMAC, connected, authenticated, nil, nil)
			case "pause":
				_ = sendLogoff()
				s.status("paused", "认证已暂停", serverMAC)
				return result(KindPaused, serverMAC, connected, authenticated, nil, nil)
			case "reload":
				return result(KindReload, serverMAC, connected, authenticated, nil, nil)
			}

		case <-authTimer.C:
			if !authenticated {
				if serverMAC == "" {
					s.status("retrying", "未找到认证服务器", "")
					return result(KindNoServer, "", false, false, nil, nil)
				}
				s.status("retrying", "认证超时，服务器未返回最终结果", serverMAC)
				return result(KindTimeout, serverMAC, true, false, nil, nil)
			}

		case <-startTimer.C:
			if !authenticated {
				if startSent >= opts.Retries {
					if serverMAC == "" {
						s.status("retrying", "未找到认证服务器", "")
						return result(KindNoServer, "", false, false, nil, nil)
					}
					s.status("retrying", "认证超时，服务器未返回最终结果", serverMAC)
					return result(KindTimeout, serverMAC, true, false, nil, nil)
				}
				if err := sendStart(); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, err)
				}
				startSent++
				startTimer.Reset(opts.StartTimeout)
			}

		case <-reauthC:
			if authenticated && opts.Preempt {
				if err := restartAuth("定时重认证..."); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, err)
				}
			}
			reauthC = nil

		case <-checkC:
			if opts.Autoguard && opts.Probe != nil {
				if !opts.Probe(ctx) {
					if err := restartAuth("检测到掉线，重新认证..."); err != nil {
						return result(KindTransport, serverMAC, connected, authenticated, nil, err)
					}
				}
			}
			if opts.Autoguard && opts.Probe != nil && opts.CheckInterval > 0 {
				checkC = time.After(opts.CheckInterval)
			} else {
				checkC = nil
			}

		case frame, ok := <-raw.Packets():
			if !ok {
				return result(KindTransport, serverMAC, connected, authenticated, nil, fmt.Errorf("网卡读取结束"))
			}
			if len(frame) < 18 {
				continue
			}
			from := formatMAC(frame[6:12])
			if from == source {
				continue
			}
			if locked != "" && from != locked {
				continue
			}
			code, identifier, eap, parsed := protocol.TryParseEAP(frame)
			if !parsed {
				continue
			}
			connected = true
			serverMAC = from
			switch code {
			case protocol.EapSuccess:
				messages := protocol.ExtractServerMessages(eap)
				message := "认证成功"
				if len(messages) > 0 {
					message += ": " + strings.Join(messages, " | ")
				}
				authenticated = true
				onAuthenticated(from)
				s.status("authenticated", message, from)
			case protocol.EapFailure:
				messages := protocol.ExtractServerMessages(eap)
				message := "认证失败（服务器未返回可读原因）"
				if len(messages) > 0 {
					message = strings.Join(messages, " | ")
				}
				s.status("error", message, from)
				return result(KindFailure, from, true, authenticated, messages, nil)
			case protocol.EapRequest:
				if len(eap) < 5 {
					continue
				}
				eapType := eap[4]
				key := fmt.Sprintf("%d:%d:%x", identifier, eapType, eap[5:])
				if seen, exists := handled[key]; exists && time.Since(seen) < 5*time.Second {
					continue
				}
				handled[key] = time.Now()
				var response []byte
				switch eapType {
				case protocol.EapIdentity:
					tail, err := identityTail()
					if err != nil {
						return result(KindFailure, from, true, authenticated, nil, err)
					}
					response = append(protocol.IdentityResponse(identifier, identity), tail...)
					s.status("authenticating", "已连接认证服务器，提交用户身份...", from)
				case protocol.EapMD5:
					if len(eap) < 6 || eap[5] != 16 || len(eap) < 6+16 {
						s.status("error", "服务器 MD5 Challenge 格式不完整", from)
						return result(KindFailure, from, true, authenticated, nil, fmt.Errorf("invalid MD5 challenge"))
					}
					challenge := eap[6 : 6+16]
					tail, err := md5Tail(challenge)
					if err != nil {
						return result(KindFailure, from, true, authenticated, nil, err)
					}
					response = append(protocol.MD5Response(identifier, password, challenge, identity), tail...)
					s.status("authenticating", "正在进行 EAP-MD5 认证...", from)
				default:
					s.status("error", fmt.Sprintf("服务器请求了未实现的 EAP 类型: %d", eapType), from)
					return result(KindFailure, from, true, authenticated, nil, fmt.Errorf("unsupported EAP type %d", eapType))
				}
				time.Sleep(opts.ResponseDelay)
				frame, err := protocol.BuildFrame(source, from, response, nil)
				if err != nil {
					return result(KindFailure, from, true, authenticated, nil, err)
				}
				if err := send(frame); err != nil {
					return result(KindTransport, from, true, authenticated, nil, err)
				}
			}
		}
	}
}

func formatMAC(bytes []byte) string {
	if len(bytes) < 6 {
		return ""
	}
	parts := make([]string, 6)
	for i := 0; i < 6; i++ {
		parts[i] = fmt.Sprintf("%02x", bytes[i])
	}
	return strings.Join(parts, ":")
}
