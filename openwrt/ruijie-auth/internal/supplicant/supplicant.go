package supplicant

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"

	"ruijie-auth/internal/protocol"
	"ruijie-auth/internal/transport"
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
	Kind           Kind
	ServerMAC      string
	SourceMAC      string
	Connected      bool
	Authenticated  bool
	Messages       []string
	Properties     *protocol.PrivatePropertiesResult
	ReauthInterval time.Duration
	Err            error
}

// StatusFunc receives state transitions.
type StatusFunc func(state, message, server string)

// Options controls one resident supplicant session.
type Options struct {
	Device         string
	Username       string
	Password       string
	AuthMode       string
	ServiceName    string
	StaticID       string
	Timeout        time.Duration
	Retries        int
	Repeats        int
	StartTimeout   time.Duration
	ResponseDelay  time.Duration
	ReauthInterval time.Duration
	CheckInterval  time.Duration
	DisableTail    bool
	NoLogoffFirst  bool
	Preempt        bool
	Autoguard      bool
	LogoffOnStop   bool
	LockedServer   string
	Network        func() protocol.NetworkInfo
	Probe          func(context.Context) bool
	Status         StatusFunc
	Log            func(string)
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

func (s *Session) logf(format string, args ...any) {
	if s.opts.Log != nil {
		s.opts.Log(fmt.Sprintf(format, args...))
	}
}

// Run opens the raw transport and dispatches on auth_mode: EAP-MD5 keeps the
// frame-level state machine, PEAP/EAP-TLS run the TLS tunnel path.
func (s *Session) Run(ctx context.Context, commands <-chan string) Result {
	if protocol.ParseAuthMethod(s.opts.AuthMode) != protocol.AuthEAPMD5 {
		return s.runTunnel(ctx, commands)
	}
	return s.runEAPMD5(ctx, commands)
}

func (o Options) normalized() Options {
	if o.Timeout <= 0 {
		o.Timeout = 20 * time.Second
	}
	if o.StartTimeout <= 0 {
		o.StartTimeout = 3 * time.Second
	}
	if o.Retries <= 0 {
		o.Retries = 4
	}
	if o.Repeats <= 0 {
		o.Repeats = 3
	}
	if o.ResponseDelay <= 0 {
		o.ResponseDelay = 40 * time.Millisecond
	}
	return o
}

// setup holds every byte-level input the C# Supplicant constructor derives.
type setup struct {
	raw          transport.Transport
	source       string
	identity     []byte
	password     []byte
	serviceName  []byte
	network      protocol.NetworkInfo
	locked       string
	sessionNonce []byte
}

func (s *Session) open() (*setup, error) {
	opts := s.opts.normalized()
	raw, err := s.opener(opts.Device)
	if err != nil {
		return nil, err
	}
	time.Sleep(300 * time.Millisecond)
	source := protocol.NormalizeMAC(raw.HardwareAddr())
	if source == "" {
		raw.Close()
		return nil, fmt.Errorf("接口没有有效 MAC: %s", opts.Device)
	}
	identity, err := protocol.GB18030Encode(opts.Username)
	if err != nil {
		raw.Close()
		return nil, err
	}
	password, err := protocol.GB18030Encode(opts.Password)
	if err != nil {
		raw.Close()
		return nil, err
	}
	serviceName, err := protocol.GB18030Encode(strings.TrimSpace(opts.ServiceName))
	if err != nil {
		raw.Close()
		return nil, err
	}
	network := protocol.NetworkInfo{}
	if opts.Network != nil {
		network = opts.Network()
	}
	if opts.StaticID != "" {
		network.StaticID = []byte(strings.TrimSpace(opts.StaticID))
	}
	return &setup{
		raw:          raw,
		source:       source,
		identity:     identity,
		password:     password,
		serviceName:  serviceName,
		network:      network,
		locked:       protocol.NormalizeMAC(opts.LockedServer),
		sessionNonce: protocol.RandomHex(16),
	}, nil
}

// tail builds one private tail; a nil result means "private tail off".
func (s *Session) tail(
	st *setup,
	iv []byte,
	nonce []byte,
	fingerprint []byte,
) ([]byte, error) {
	if s.opts.DisableTail {
		return nil, nil
	}
	options := &protocol.TailOptions{ServiceName: st.serviceName}
	if nonce != nil {
		options.Nonce = nonce
	}
	if fingerprint != nil {
		options.Fingerprint = fingerprint
	}
	return protocol.BuildTail(
		st.identity, st.password, st.source, st.network, iv, options)
}

// controlTail is the tail attached to Start/Logoff: session nonce with a
// zeroed 128-byte fingerprint, exactly like C# Supplicant.ControlTail.
func (s *Session) controlTail(st *setup) ([]byte, error) {
	return s.tail(st, make([]byte, 16), st.sessionNonce, make([]byte, 128))
}

func (s *Session) sendBurst(st *setup, frame []byte, repeats int) error {
	for i := 0; i < repeats; i++ {
		if err := st.raw.Send(frame); err != nil {
			return err
		}
	}
	return nil
}

func (s *Session) sendControl(st *setup, typ byte, destination string) error {
	tail, err := s.controlTail(st)
	if err != nil {
		return err
	}
	frame, err := protocol.BuildControl(st.source, destination, typ, tail)
	if err != nil {
		return err
	}
	return s.sendBurst(st, frame, s.opts.normalized().Repeats)
}

func (s *Session) startDestination(st *setup) string {
	if st.locked != "" {
		return st.locked
	}
	return protocol.RuijieGroup
}

// captureServerProperties parses the private block and logs the same subset
// C# Supplicant.CaptureServerProperties reports.
func (s *Session) captureServerProperties(eap []byte) *protocol.PrivatePropertiesResult {
	if len(eap) <= 4 {
		return nil
	}
	parsed := protocol.ParsePrivateProperties(eap[4:])
	result := &parsed
	for _, property := range result.Properties {
		switch property.Type {
		case byte(protocol.SuReauthInterval),
			byte(protocol.SuUpgradeURL),
			byte(protocol.AccountInfo),
			byte(protocol.PersonalInfo),
			byte(protocol.BroadcastInfo),
			byte(protocol.RejectReason),
			byte(protocol.HelloInterval),
			byte(protocol.IndicatePort),
			byte(protocol.IndicateServerIP),
			byte(protocol.MessageClientPort),
			byte(protocol.ClientMessagePeriod):
			s.logf("服务器属性: %s=%s", property.Name, property.DisplayValue())
		}
	}
	if len(result.Errors) > 0 {
		s.logf("服务器私有属性解析告警: %s", strings.Join(result.Errors, "; "))
	}
	return result
}

func messagesFrom(eap []byte) ([]string, *protocol.PrivatePropertiesResult) {
	if len(eap) <= 4 {
		return nil, nil
	}
	parsed := protocol.ParsePrivateProperties(eap[4:])
	messages := parsed.Messages
	if len(messages) == 0 {
		messages = protocol.ExtractServerMessages(eap)
	}
	return messages, &parsed
}

func propertyNumber(result *protocol.PrivatePropertiesResult, typ byte) (int64, bool) {
	if result == nil {
		return 0, false
	}
	for _, property := range result.Properties {
		if property.Type == typ && property.Number != nil {
			return *property.Number, true
		}
	}
	return 0, false
}

// reauthFromServer returns the server-driven interval when SU_REAUTH_INTERV
// is present, otherwise the configured interval.
func reauthFromServer(
	result *protocol.PrivatePropertiesResult,
	configured time.Duration,
) time.Duration {
	if seconds, ok := propertyNumber(result, byte(protocol.SuReauthInterval)); ok &&
		seconds > 0 {
		return time.Duration(seconds) * time.Second
	}
	return configured
}

// ---------------------------------------------------------------------------
// EAP-MD5
// ---------------------------------------------------------------------------

// runEAPMD5 is the original frame-level state machine.
func (s *Session) runEAPMD5(ctx context.Context, commands <-chan string) Result {
	opts := s.opts.normalized()
	st, err := s.open()
	if err != nil {
		return Result{Kind: KindTransport, Err: err}
	}
	defer st.raw.Close()

	result := func(kind Kind, server string, connected, authenticated bool,
		messages []string, properties *protocol.PrivatePropertiesResult,
		err error) Result {
		return Result{
			Kind:           kind,
			ServerMAC:      server,
			SourceMAC:      st.source,
			Connected:      connected,
			Authenticated:  authenticated,
			Messages:       messages,
			Properties:     properties,
			ReauthInterval: reauthFromServer(properties, opts.ReauthInterval),
			Err:            err,
		}
	}

	sendLogoff := func() error {
		return s.sendControl(st, 2, protocol.StandardGroup)
	}
	sendStart := func() error {
		if err := s.sendControl(st, 1, s.startDestination(st)); err != nil {
			return err
		}
		if st.locked == "" {
			s.status("authenticating", "正在寻找认证服务器...", "")
		} else {
			s.status("authenticating", "正在请求锁定的认证服务器 "+st.locked+" ...", "")
		}
		return nil
	}
	sendFrame := func(frame []byte) error {
		return s.sendBurst(st, frame, opts.Repeats)
	}

	if !opts.NoLogoffFirst {
		s.status("authenticating", "正在清理可能存在的旧会话...", "")
		if err := sendLogoff(); err != nil {
			return result(KindTransport, "", false, false, nil, nil, err)
		}
		time.Sleep(100 * time.Millisecond)
	}
	if err := sendStart(); err != nil {
		return result(KindTransport, "", false, false, nil, nil, err)
	}

	authTimer := time.NewTimer(opts.Timeout)
	startTimer := time.NewTimer(opts.StartTimeout)
	defer authTimer.Stop()
	defer startTimer.Stop()

	authenticated := false
	connected := false
	serverMAC := ""
	var lastProperties *protocol.PrivatePropertiesResult
	startSent := 1
	handled := make(map[string]bool)
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
	startReauthTimer := func() {
		interval := reauthFromServer(lastProperties, opts.ReauthInterval)
		if opts.Preempt && interval > 0 {
			reauthC = time.After(interval)
		} else {
			reauthC = nil
		}
	}
	startCheckTimer := func() {
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
			return result(KindCanceled, serverMAC, connected, authenticated, nil,
				lastProperties, nil)

		case command := <-commands:
			switch command {
			case "auth", "resume":
				if err := restartAuth("正在重新认证..."); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, lastProperties, err)
				}
			case "logoff":
				_ = sendLogoff()
				s.status("idle", "已发送 EAPOL-Logoff", serverMAC)
				return result(KindLogoff, serverMAC, connected, authenticated, nil, lastProperties, nil)
			case "pause":
				_ = sendLogoff()
				s.status("paused", "认证已暂停", serverMAC)
				return result(KindPaused, serverMAC, connected, authenticated, nil, lastProperties, nil)
			case "reload":
				return result(KindReload, serverMAC, connected, authenticated, nil, lastProperties, nil)
			}

		case <-authTimer.C:
			if !authenticated {
				if serverMAC == "" {
					s.status("retrying", "未找到认证服务器", "")
					return result(KindNoServer, "", false, false, nil, lastProperties, nil)
				}
				s.status("retrying", "认证超时，服务器未返回最终结果", serverMAC)
				return result(KindTimeout, serverMAC, true, false, nil, lastProperties, nil)
			}

		case <-startTimer.C:
			if !authenticated {
				if startSent >= opts.Retries {
					if serverMAC == "" {
						s.status("retrying", "未找到认证服务器", "")
						return result(KindNoServer, "", false, false, nil, lastProperties, nil)
					}
					s.status("retrying", "认证超时，服务器未返回最终结果", serverMAC)
					return result(KindTimeout, serverMAC, true, false, nil, lastProperties, nil)
				}
				if err := sendStart(); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, lastProperties, err)
				}
				startSent++
				startTimer.Reset(opts.StartTimeout)
			}

		case <-reauthC:
			if authenticated && opts.Preempt {
				if err := restartAuth("定时重认证..."); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, lastProperties, err)
				}
			}
			reauthC = nil

		case <-checkC:
			if opts.Autoguard && opts.Probe != nil && !opts.Probe(ctx) {
				if err := restartAuth("检测到掉线，重新认证..."); err != nil {
					return result(KindTransport, serverMAC, connected, authenticated, nil, lastProperties, err)
				}
			}
			startCheckTimer()

		case frame, ok := <-st.raw.Packets():
			if !ok {
				return result(KindTransport, serverMAC, connected, authenticated, nil,
					lastProperties, fmt.Errorf("网卡读取结束"))
			}
			if len(frame) < 18 {
				continue
			}
			from := formatMAC(frame[6:12])
			if from == st.source {
				continue
			}
			if st.locked != "" && from != st.locked {
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
				properties := s.captureServerProperties(eap)
				lastProperties = properties
				messages, _ := messagesFrom(eap)
				for _, message := range messages {
					s.logf("服务器消息: %s", message)
				}
				authenticated = true
				stopTimer(authTimer)
				stopTimer(startTimer)
				startReauthTimer()
				startCheckTimer()
				message := "认证成功"
				if len(messages) > 0 {
					message += ": " + strings.Join(messages, " | ")
				}
				s.status("authenticated", message, from)
			case protocol.EapFailure:
				properties := s.captureServerProperties(eap)
				lastProperties = properties
				messages, _ := messagesFrom(eap)
				message := "认证失败（服务器未返回可读原因）"
				if len(messages) > 0 {
					message = strings.Join(messages, " | ")
				}
				s.status("error", message, from)
				return result(KindFailure, from, true, authenticated, messages, properties, nil)
			case protocol.EapRequest:
				if len(eap) < 5 {
					continue
				}
				eapType := eap[4]
				key := fmt.Sprintf("%d:%d:%x", identifier, eapType, eap[5:])
				if handled[key] {
					continue
				}
				var response []byte
				switch eapType {
				case protocol.EapIdentity:
					tail, err := s.tail(st, make([]byte, 16), st.sessionNonce, nil)
					if err != nil {
						return result(KindFailure, from, true, authenticated, nil, lastProperties, err)
					}
					s.status("authenticating", "已连接认证服务器，提交用户身份...", from)
					response = append(protocol.IdentityResponse(identifier, st.identity), tail...)
				case protocol.EapMD5:
					if len(eap) < 6 || eap[5] != 16 || len(eap) < 6+16 {
						s.status("error", "服务器 MD5 Challenge 格式不完整", from)
						return result(KindFailure, from, true, authenticated, nil, lastProperties,
							fmt.Errorf("invalid MD5 challenge"))
					}
					challenge := eap[6 : 6+16]
					tail, err := s.tail(st, challenge, nil, nil)
					if err != nil {
						return result(KindFailure, from, true, authenticated, nil, lastProperties, err)
					}
					s.status("authenticating", "正在进行 EAP-MD5 认证...", from)
					response = append(protocol.MD5Response(identifier, st.password, challenge, st.identity), tail...)
				default:
					s.status("error", fmt.Sprintf("服务器请求了未实现的 EAP 类型: %d", eapType), from)
					return result(KindFailure, from, true, authenticated, nil, lastProperties,
						fmt.Errorf("unsupported EAP type %d", eapType))
				}
				handled[key] = true
				time.Sleep(opts.ResponseDelay)
				frame, err := protocol.BuildFrame(st.source, from, response, nil)
				if err != nil {
					return result(KindFailure, from, true, authenticated, nil, lastProperties, err)
				}
				if err := sendFrame(frame); err != nil {
					return result(KindTransport, from, true, authenticated, nil, lastProperties, err)
				}
			}
		}
	}
}

// ---------------------------------------------------------------------------
// PEAP / EAP-TLS
// ---------------------------------------------------------------------------

type tunnelAttempt struct {
	session    protocol.PeapResult
	command    string
	connected  bool
	server     string
	messages   []string
	properties *protocol.PrivatePropertiesResult
}

// runTunnel authenticates once, then stays resident like the EAP-MD5 path:
// periodic re-auth, connectivity guard, and control commands keep working.
func (s *Session) runTunnel(ctx context.Context, commands <-chan string) Result {
	opts := s.opts.normalized()
	kind := protocol.ParseAuthMethod(opts.AuthMode)
	st, err := s.open()
	if err != nil {
		return Result{Kind: KindTransport, Err: err}
	}
	defer st.raw.Close()

	result := func(kind Kind, server string, connected, authenticated bool,
		messages []string, properties *protocol.PrivatePropertiesResult,
		err error) Result {
		return Result{
			Kind:           kind,
			ServerMAC:      server,
			SourceMAC:      st.source,
			Connected:      connected,
			Authenticated:  authenticated,
			Messages:       messages,
			Properties:     properties,
			ReauthInterval: reauthFromServer(properties, opts.ReauthInterval),
			Err:            err,
		}
	}
	failure := func(attempt tunnelAttempt) Result {
		message := attempt.session.Message
		if attempt.session.OuterFailure != "" {
			message += "（" + attempt.session.OuterFailure + "）"
		}
		s.status("error", message, attempt.server)
		return result(KindFailure, attempt.server, attempt.connected, false,
			attempt.messages, attempt.properties, fmt.Errorf("%s", attempt.session.Message))
	}

	sendLogoff := func() error { return s.sendControl(st, 2, protocol.StandardGroup) }

	for {
		identityTail, err := s.tail(st, make([]byte, 16), nil, nil)
		if err != nil {
			return result(KindFailure, "", false, false, nil, nil, err)
		}
		s.status("authenticating",
			fmt.Sprintf("认证方式: %s（auth_mode 配置驱动）", kind.Display()), "")

		attempt := s.attemptTunnel(ctx, st, commands, kind, identityTail)
		switch attempt.command {
		case "pause":
			_ = sendLogoff()
			s.status("paused", "认证已暂停", attempt.server)
			return result(KindPaused, attempt.server, attempt.connected, false, nil, nil, nil)
		case "logoff":
			_ = sendLogoff()
			s.status("idle", "已发送 EAPOL-Logoff", attempt.server)
			return result(KindLogoff, attempt.server, attempt.connected, false, nil, nil, nil)
		case "reload":
			return result(KindReload, attempt.server, attempt.connected, false, nil, nil, nil)
		}
		if !attempt.session.OK {
			if ctx.Err() != nil {
				if opts.LogoffOnStop {
					_ = sendLogoff()
				}
				return result(KindCanceled, attempt.server, attempt.connected, false, nil, nil, nil)
			}
			return failure(attempt)
		}

		interval := reauthFromServer(attempt.properties, opts.ReauthInterval)
		s.status("authenticated", attempt.session.Message, attempt.server)

		var reauthC <-chan time.Time
		if opts.Preempt && interval > 0 {
			reauthC = time.After(interval)
		}
		var checkC <-chan time.Time
		if opts.Autoguard && opts.Probe != nil && opts.CheckInterval > 0 {
			checkC = time.After(opts.CheckInterval)
		}

	hold:
		for {
			select {
			case <-ctx.Done():
				if opts.LogoffOnStop {
					_ = sendLogoff()
				}
				return result(KindCanceled, attempt.server, true, true, nil,
					attempt.properties, nil)
			case command := <-commands:
				switch command {
				case "auth", "resume":
					break hold
				case "logoff":
					_ = sendLogoff()
					s.status("idle", "已发送 EAPOL-Logoff", attempt.server)
					return result(KindLogoff, attempt.server, true, false, nil, nil, nil)
				case "pause":
					_ = sendLogoff()
					s.status("paused", "认证已暂停", attempt.server)
					return result(KindPaused, attempt.server, true, false, nil, nil, nil)
				case "reload":
					return result(KindReload, attempt.server, true, false, nil, nil, nil)
				}
			case <-reauthC:
				s.logf("定时重认证...")
				break hold
			case <-checkC:
				if opts.Autoguard && opts.Probe != nil && !opts.Probe(ctx) {
					s.logf("检测到掉线，重新认证...")
					break hold
				}
				if opts.Autoguard && opts.Probe != nil && opts.CheckInterval > 0 {
					checkC = time.After(opts.CheckInterval)
				} else {
					checkC = nil
				}
			}
		}
	}
}

// attemptTunnel runs one full PEAP/EAP-TLS exchange. A control command that
// arrives during the exchange cancels it and is reported back to the caller.
func (s *Session) attemptTunnel(
	ctx context.Context,
	st *setup,
	commands <-chan string,
	kind protocol.AuthMethodKind,
	identityTail []byte,
) tunnelAttempt {
	opts := s.opts.normalized()
	runCtx, cancel := context.WithCancel(ctx)
	defer cancel()

	command := make(chan string, 1)
	go func() {
		select {
		case <-runCtx.Done():
		case cmd, ok := <-commands:
			if ok {
				command <- cmd
				cancel()
			}
		}
	}()

	tt := &tunnelTransport{
		raw:         st.raw,
		source:      st.source,
		locked:      st.locked,
		repeats:     opts.Repeats,
		timeout:     opts.Timeout,
		logoffFirst: !s.opts.NoLogoffFirst,
		controlTail: func() ([]byte, error) { return s.controlTail(st) },
		ctx:         runCtx,
		status:      s.status,
	}
	session := protocol.RunPeap(tt, protocol.PeapOptions{
		Mode:                    kind.TunnelMode(),
		UserName:                opts.Username,
		Password:                opts.Password,
		VerifyServerCertificate: false,
		MaxFragmentBytes:        1200,
		Timeout:                 2 * opts.Timeout,
		IdentityTail:            identityTail,
	})
	cancel()
	for _, step := range session.Steps {
		s.logf("[PEAP] %s", step)
	}

	attempt := tunnelAttempt{
		session:    session,
		connected:  tt.Connected(),
		server:     tt.ServerMAC(),
		messages:   tt.Messages(),
		properties: tt.Properties(),
	}
	select {
	case cmd := <-command:
		attempt.command = cmd
	default:
	}
	return attempt
}

// tunnelTransport implements protocol.PeapTransport over the raw EAPOL
// socket: Start performs EAPOL-Start plus the Identity round trip, Exchange
// sends one tunnel response and returns the next server EAP packet.
type tunnelTransport struct {
	raw         transport.Transport
	source      string
	locked      string
	repeats     int
	timeout     time.Duration
	logoffFirst bool
	controlTail func() ([]byte, error)
	ctx         context.Context
	status      StatusFunc

	mu             sync.Mutex
	server         string
	outerSuccess   bool
	outerFailure   string
	lastIdentifier byte
	connected      bool
	messages       []string
	properties     *protocol.PrivatePropertiesResult
}

func (t *tunnelTransport) SourceMAC() string { return t.source }

func (t *tunnelTransport) ServerMAC() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.server
}

func (t *tunnelTransport) Connected() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.connected
}

func (t *tunnelTransport) OuterSuccess() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.outerSuccess
}

func (t *tunnelTransport) OuterFailure() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.outerFailure
}

func (t *tunnelTransport) Messages() []string {
	t.mu.Lock()
	defer t.mu.Unlock()
	return append([]string(nil), t.messages...)
}

func (t *tunnelTransport) Properties() *protocol.PrivatePropertiesResult {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.properties
}

func (t *tunnelTransport) sendControl(typ byte) error {
	tail, err := t.controlTail()
	if err != nil {
		return err
	}
	destination := protocol.RuijieGroup
	if typ == 2 {
		destination = protocol.StandardGroup
	}
	if typ == 1 && t.locked != "" {
		destination = t.locked
	}
	frame, err := protocol.BuildControl(t.source, destination, typ, tail)
	if err != nil {
		return err
	}
	for i := 0; i < t.repeats; i++ {
		if err := t.raw.Send(frame); err != nil {
			return err
		}
	}
	return nil
}

func (t *tunnelTransport) sendEap(eap, tail []byte) error {
	server := t.ServerMAC()
	if server == "" {
		return fmt.Errorf("transport is not started")
	}
	frame, err := protocol.BuildFrame(t.source, server, eap, tail)
	if err != nil {
		return err
	}
	for i := 0; i < t.repeats; i++ {
		if err := t.raw.Send(frame); err != nil {
			return err
		}
	}
	return nil
}

// Start mirrors PcapEapTransport.Start: Logoff, Start, Identity, then wait
// for the first PEAP/EAP-TLS request.
func (t *tunnelTransport) Start(identity string, identityTail []byte) ([]byte, error) {
	deadline := time.Now().Add(t.timeout)
	if t.logoffFirst {
		if err := t.sendControl(2); err != nil {
			return nil, err
		}
		time.Sleep(100 * time.Millisecond)
	}
	if err := t.sendControl(1); err != nil {
		return nil, err
	}
	if t.locked == "" {
		t.status("authenticating", "正在寻找认证服务器...", "")
	} else {
		t.status("authenticating", "正在请求锁定的认证服务器 "+t.locked+" ...", "")
	}

	for {
		packet, err := t.nextEap(deadline)
		if err != nil {
			return nil, err
		}
		if packet == nil {
			return nil, fmt.Errorf("no EAP identity request")
		}
		switch packet[0] {
		case protocol.EapSuccess:
			t.recordOuter(packet)
			return nil, fmt.Errorf("server sent EAP-Success before PEAP start")
		case protocol.EapFailure:
			t.recordOuter(packet)
			return nil, fmt.Errorf("EAP-Failure before PEAP start")
		case protocol.EapRequest:
			if len(packet) >= 5 && packet[4] == protocol.EapIdentity {
				t.status("authenticating", "收到 Identity 请求，提交用户身份", t.ServerMAC())
				response := protocol.IdentityResponse(packet[1], []byte(identity))
				if err := t.sendEap(response, identityTail); err != nil {
					return nil, err
				}
				goto tunnel
			}
		}
	}

tunnel:
	for {
		packet, err := t.nextEap(deadline)
		if err != nil {
			return nil, err
		}
		if packet == nil {
			return nil, fmt.Errorf("no PEAP/EAP-TLS request")
		}
		switch packet[0] {
		case protocol.EapSuccess, protocol.EapFailure:
			t.recordOuter(packet)
			return packet, nil
		case protocol.EapRequest:
			if len(packet) >= 5 {
				return packet, nil
			}
		}
	}
}

// Exchange sends one EAP response with the server's last identifier and
// returns the next server packet.
func (t *tunnelTransport) Exchange(eapPacket, tail []byte) ([]byte, error) {
	t.mu.Lock()
	eapPacket[1] = t.lastIdentifier
	t.mu.Unlock()
	if err := t.sendEap(eapPacket, tail); err != nil {
		return nil, err
	}
	deadline := time.Now().Add(t.timeout)
	for {
		packet, err := t.nextEap(deadline)
		if err != nil {
			return nil, err
		}
		if packet == nil {
			return nil, fmt.Errorf("no server EAP response")
		}
		switch packet[0] {
		case protocol.EapSuccess, protocol.EapFailure:
			t.recordOuter(packet)
		}
		return packet, nil
	}
}

// recordOuter stores the terminal EAP result plus any private properties the
// server attached to it.
func (t *tunnelTransport) recordOuter(packet []byte) {
	messages, properties := messagesFrom(packet)
	t.mu.Lock()
	if packet[0] == protocol.EapSuccess {
		t.outerSuccess = true
	} else {
		t.outerFailure = "EAP-Failure"
	}
	if len(messages) > 0 {
		t.messages = messages
	}
	if properties != nil {
		t.properties = properties
	}
	t.mu.Unlock()
	if properties == nil {
		return
	}
	for _, property := range properties.Properties {
		switch property.Type {
		case byte(protocol.SuReauthInterval),
			byte(protocol.RejectReason),
			byte(protocol.BroadcastInfo):
			t.status("authenticating",
				fmt.Sprintf("服务器属性: %s=%s", property.Name, property.DisplayValue()), "")
		}
	}
	if len(properties.Errors) > 0 {
		t.status("authenticating",
			"服务器私有属性解析告警: "+strings.Join(properties.Errors, "; "), "")
	}
}

// nextEap returns the next EAP packet from the filter, or nil on timeout.
func (t *tunnelTransport) nextEap(deadline time.Time) ([]byte, error) {
	for {
		if t.ctx.Err() != nil {
			return nil, t.ctx.Err()
		}
		remaining := time.Until(deadline)
		if remaining <= 0 {
			return nil, nil
		}
		timer := time.NewTimer(remaining)
		var frame []byte
		var ok bool
		select {
		case <-t.ctx.Done():
			timer.Stop()
			return nil, t.ctx.Err()
		case frame, ok = <-t.raw.Packets():
			timer.Stop()
			if !ok {
				return nil, fmt.Errorf("网卡读取结束")
			}
		case <-timer.C:
			return nil, nil
		}
		if len(frame) < 18 {
			continue
		}
		from := formatMAC(frame[6:12])
		if from == t.source {
			continue
		}
		if t.locked != "" && from != t.locked {
			continue
		}
		_, identifier, eap, parsed := protocol.TryParseEAP(frame)
		if !parsed {
			continue
		}
		t.mu.Lock()
		if t.server == "" {
			t.server = from
		}
		t.connected = true
		t.lastIdentifier = identifier
		t.mu.Unlock()
		return eap, nil
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
