package protocol

import (
	"context"
	crand "crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"
)

// TunnelMode selects PEAP (EAP type 25) or EAP-TLS (EAP type 13).
type TunnelMode int

const (
	// TunnelPEAP carries MSCHAPv2 inside a TLS tunnel.
	TunnelPEAP TunnelMode = iota
	// TunnelEAPTLS performs mutual TLS without an inner method.
	TunnelEAPTLS
)

// EAP method types used by the tunnel.
const (
	EapTLS  byte = 13
	EapPEAP byte = 25
)

const (
	tunnelFlagLength byte = 0x80
	tunnelFlagMore   byte = 0x40
	tunnelFlagStart  byte = 0x20
)

// PeapOptions mirrors PeapOptions in the C# core.
type PeapOptions struct {
	Mode                    TunnelMode
	UserName                string
	Password                string
	TargetHost              string
	VerifyServerCertificate bool
	ClientCertificate       *tls.Certificate
	MaxFragmentBytes        int
	Timeout                 time.Duration
	IdentityTail            []byte
}

// PeapResult mirrors PeapResult in the C# core.
type PeapResult struct {
	OK            bool
	Message       string
	TLSProtocol   string
	ServerSubject string
	Steps         []string
	OuterFailure  string
}

// PeapTransport is the frame-level contract used by RunPeap, matching
// IPeapTransport: Start performs Start/Identity, Exchange sends one tunnel
// response and returns the next server EAP packet.
type PeapTransport interface {
	SourceMAC() string
	ServerMAC() string
	OuterSuccess() bool
	OuterFailure() string
	Start(identity string, identityTail []byte) ([]byte, error)
	Exchange(eapPacket, tail []byte) ([]byte, error)
}

// RunPeap drives one PEAP or EAP-TLS exchange over the supplied transport.
//
// The structure follows PeapSession.Run: Start -> feed the first server
// fragment -> TLS handshake (pump goroutine relays fragments) -> phase 2 ->
// wait for the outer EAP-Success.
func RunPeap(transport PeapTransport, options PeapOptions) PeapResult {
	steps := make([]string, 0, 16)
	deadline := time.Now().Add(options.Timeout)
	conn := NewEapTLSConn()
	var ctxCancel context.CancelFunc
	var pumpDone chan struct{}

	result := func(ok bool, message string, tlsProtocol, subject string, outerFailure string) PeapResult {
		return PeapResult{
			OK:            ok,
			Message:       message,
			TLSProtocol:   tlsProtocol,
			ServerSubject: subject,
			Steps:         steps,
			OuterFailure:  outerFailure,
		}
	}
	failure := func(message string) PeapResult {
		return result(false, message, "", "", transport.OuterFailure())
	}
	defer func() {
		if ctxCancel != nil {
			ctxCancel()
		}
		conn.CompleteInput()
		if pumpDone != nil {
			select {
			case <-pumpDone:
			case <-time.After(time.Second):
			}
		}
	}()

	first, err := transport.Start(options.UserName, options.IdentityTail)
	steps = append(steps, fmt.Sprintf("server=%s", dashIfEmpty(transport.ServerMAC())))
	if err != nil {
		return failure(err.Error())
	}
	if !isTunnelRequest(first, options.Mode) {
		return failure("first server packet is not a PEAP/EAP-TLS request")
	}

	pumpCtx, cancel := context.WithCancel(context.Background())
	ctxCancel = cancel
	pumpDone = make(chan struct{})
	go func() {
		defer close(pumpDone)
		pump(pumpCtx, transport, conn, options)
	}()

	feedFirstPayload(transport, conn, first, &steps, options)

	verify := func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
		if !options.VerifyServerCertificate {
			return nil
		}
		if len(rawCerts) == 0 {
			return errors.New("server presented no certificate")
		}
		certificate, err := x509.ParseCertificate(rawCerts[0])
		if err != nil {
			return err
		}
		if options.TargetHost != "" &&
			!strings.Contains(strings.ToLower(certificate.Subject.String()),
				strings.ToLower(options.TargetHost)) {
			return fmt.Errorf("certificate subject %q does not match %q",
				certificate.Subject.String(), options.TargetHost)
		}
		return nil
	}
	targetHost := options.TargetHost
	if targetHost == "" {
		targetHost = "localhost"
	}
	tlsConfig := &tls.Config{
		InsecureSkipVerify:    true,
		ServerName:            targetHost,
		VerifyPeerCertificate: verify,
		MinVersion:            tls.VersionTLS10,
	}
	if options.ClientCertificate != nil {
		tlsConfig.Certificates = []tls.Certificate{*options.ClientCertificate}
	}

	tlsConn := tls.Client(conn, tlsConfig)
	handshakeErr := handshakeWithTimeout(tlsConn, conn, deadline)
	if handshakeErr != nil {
		return failure(handshakeErr.Error())
	}
	state := tlsConn.ConnectionState()
	steps = append(steps, fmt.Sprintf("tls=%s cipher=%s",
		tlsVersionName(state.Version), tls.CipherSuiteName(state.CipherSuite)))
	subject := "-"
	if len(state.PeerCertificates) > 0 {
		subject = state.PeerCertificates[0].Subject.String()
	}
	steps = append(steps, "server-cert="+subject)

	if options.Mode == TunnelEAPTLS {
		waitOuterSuccess(transport, deadline)
		message := "EAP-TLS 认证成功"
		if !transport.OuterSuccess() {
			message = "EAP-TLS 未收到外层成功"
		}
		return result(transport.OuterSuccess(), message,
			tlsVersionName(state.Version), subject, transport.OuterFailure())
	}

	if !runPhase2(tlsConn, options, &steps, deadline) {
		return result(false, "PEAP phase2 失败",
			tlsVersionName(state.Version), subject, transport.OuterFailure())
	}
	waitOuterSuccess(transport, deadline)
	message := "PEAP/MSCHAPv2 认证成功"
	if !transport.OuterSuccess() {
		message = "PEAP 未收到外层成功"
	}
	return result(transport.OuterSuccess(), message,
		tlsVersionName(state.Version), subject, transport.OuterFailure())
}

func handshakeWithTimeout(tlsConn *tls.Conn, conn *EapTLSConn, deadline time.Time) error {
	ctx, cancel := context.WithDeadline(context.Background(), deadline)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- tlsConn.HandshakeContext(ctx) }()
	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		_ = conn.Close()
		return errors.New("TLS handshake timeout")
	}
}

// pump relays TLS records to the transport exactly like PeapSession.PumpAsync.
func pump(ctx context.Context, transport PeapTransport, conn *EapTLSConn, options PeapOptions) {
	defer conn.CompleteInput()
	for {
		select {
		case <-ctx.Done():
			return
		case outgoing, ok := <-conn.outgoing:
			if !ok {
				return
			}
			next, err := sendClientPayload(transport, outgoing, options)
			if err != nil || next == nil {
				return
			}
			if transport.OuterSuccess() || transport.OuterFailure() != "" {
				return
			}
			payload, ok := receiveServerPayload(transport, next, options)
			if !ok || payload == nil {
				return
			}
			conn.Feed(payload)
		case <-time.After(50 * time.Millisecond):
			if transport.OuterSuccess() || transport.OuterFailure() != "" {
				return
			}
		}
	}
}

func sendClientPayload(
	transport PeapTransport,
	tlsData []byte,
	options PeapOptions,
) ([]byte, error) {
	offset := 0
	first := true
	max := options.MaxFragmentBytes
	if max < 200 {
		max = 200
	}
	var lastServer []byte
	for offset < len(tlsData) {
		length := len(tlsData) - offset
		if length > max {
			length = max
		}
		last := offset+length >= len(tlsData)
		var flags byte
		if first {
			flags |= tunnelFlagLength
		}
		if !last {
			flags |= tunnelFlagMore
		}
		header := 0
		if first {
			header = 4
		}
		payload := make([]byte, 1+header+length)
		payload[0] = flags
		if first {
			payload[1] = byte(len(tlsData) >> 24)
			payload[2] = byte(len(tlsData) >> 16)
			payload[3] = byte(len(tlsData) >> 8)
			payload[4] = byte(len(tlsData))
		}
		copy(payload[1+header:], tlsData[offset:offset+length])
		eap := buildTunnelResponse(options.Mode, payload)
		next, err := transport.Exchange(eap, options.IdentityTail)
		if err != nil {
			return nil, err
		}
		lastServer = next
		offset += length
		first = false
		if transport.OuterSuccess() || transport.OuterFailure() != "" {
			return lastServer, nil
		}
	}
	return lastServer, nil
}

func receiveServerPayload(
	transport PeapTransport,
	firstPacket []byte,
	options PeapOptions,
) ([]byte, bool) {
	assembled := make([]byte, 0, 4096)
	packet := firstPacket
	for {
		flags, data, ok := tryParseTunnel(packet, options.Mode)
		if !ok {
			return nil, false
		}
		assembled = append(assembled, data...)
		if flags&tunnelFlagMore == 0 {
			return assembled, true
		}
		ack := buildTunnelResponse(options.Mode, []byte{0x00})
		next, err := transport.Exchange(ack, options.IdentityTail)
		if err != nil {
			return nil, false
		}
		if transport.OuterSuccess() || transport.OuterFailure() != "" {
			return nil, false
		}
		packet = next
	}
}

func feedFirstPayload(
	transport PeapTransport,
	conn *EapTLSConn,
	packet []byte,
	steps *[]string,
	options PeapOptions,
) {
	payload, ok := receiveServerPayload(transport, packet, options)
	if ok && len(payload) > 0 {
		*steps = append(*steps, fmt.Sprintf("server-tls-fragment=%d", len(payload)))
		conn.Feed(payload)
		return
	}
	*steps = append(*steps, "server-start")
}

func runPhase2(
	tlsConn *tls.Conn,
	options PeapOptions,
	steps *[]string,
	deadline time.Time,
) bool {
	for time.Now().Before(deadline) {
		packet, err := readEapPacket(tlsConn, deadline)
		if err != nil || len(packet) < 4 {
			return false
		}
		if packet[0] == EapSuccess {
			*steps = append(*steps, "phase2-eap-success")
			return true
		}
		if packet[0] == EapFailure {
			*steps = append(*steps, "phase2-eap-failure")
			return false
		}
		if packet[0] != EapRequest || len(packet) < 5 {
			*steps = append(*steps, fmt.Sprintf("phase2-unexpected-code=%d", packet[0]))
			return false
		}
		switch packet[4] {
		case EapIdentity:
			response := IdentityResponse(packet[1], []byte(options.UserName))
			if _, err := tlsConn.Write(response); err != nil {
				return false
			}
			*steps = append(*steps, "phase2-identity")
		case EapMSCHAPv2:
			challenge, err := TryParseChallenge(packet)
			if err != nil {
				if IsSuccess(packet) {
					*steps = append(*steps, "phase2-mschap-success")
					continue
				}
				*steps = append(*steps, "phase2-mschap-error="+err.Error())
				return false
			}
			peerChallenge := RandomBytes(16)
			response, err := BuildResponse(challenge, options.UserName, options.Password, peerChallenge)
			if err != nil {
				*steps = append(*steps, "phase2-mschap-error="+err.Error())
				return false
			}
			if _, err := tlsConn.Write(response); err != nil {
				return false
			}
			*steps = append(*steps, "phase2-mschap-response")
		default:
			*steps = append(*steps,
				fmt.Sprintf("phase2-unsupported-eap-type=%d", packet[4]))
			return false
		}
	}
	return false
}

func readEapPacket(tlsConn *tls.Conn, deadline time.Time) ([]byte, error) {
	if err := tlsConn.SetReadDeadline(deadline); err != nil {
		return nil, err
	}
	header := make([]byte, 4)
	if _, err := io.ReadFull(tlsConn, header); err != nil {
		return nil, err
	}
	length := int(header[2])<<8 | int(header[3])
	if length < 4 {
		return header, nil
	}
	packet := make([]byte, length)
	copy(packet, header)
	if _, err := io.ReadFull(tlsConn, packet[4:]); err != nil {
		return nil, err
	}
	return packet, nil
}

func waitOuterSuccess(transport PeapTransport, deadline time.Time) {
	for time.Now().Before(deadline) &&
		!transport.OuterSuccess() && transport.OuterFailure() == "" {
		time.Sleep(50 * time.Millisecond)
	}
}

func buildTunnelResponse(mode TunnelMode, payload []byte) []byte {
	typ := EapPEAP
	if mode == TunnelEAPTLS {
		typ = EapTLS
	}
	length := 5 + len(payload)
	eap := make([]byte, length)
	eap[0] = EapResponse
	eap[1] = 0 // The transport replaces this with the server identifier.
	eap[2] = byte(length >> 8)
	eap[3] = byte(length)
	eap[4] = typ
	copy(eap[5:], payload)
	return eap
}

func isTunnelRequest(packet []byte, mode TunnelMode) bool {
	typ := EapPEAP
	if mode == TunnelEAPTLS {
		typ = EapTLS
	}
	return len(packet) >= 5 && packet[0] == EapRequest && packet[4] == typ
}

func tryParseTunnel(packet []byte, mode TunnelMode) (byte, []byte, bool) {
	typ := EapPEAP
	if mode == TunnelEAPTLS {
		typ = EapTLS
	}
	if len(packet) < 6 || packet[0] != EapRequest || packet[4] != typ {
		return 0, nil, false
	}
	flags := packet[5]
	offset := 6
	if flags&tunnelFlagLength != 0 {
		offset += 4
	}
	if offset > len(packet) {
		return 0, nil, false
	}
	return flags, packet[offset:], true
}

func tlsVersionName(version uint16) string {
	switch version {
	case tls.VersionTLS10:
		return "Tls10"
	case tls.VersionTLS11:
		return "Tls11"
	case tls.VersionTLS12:
		return "Tls12"
	case tls.VersionTLS13:
		return "Tls13"
	default:
		return fmt.Sprintf("0x%04X", version)
	}
}

func dashIfEmpty(value string) string {
	if value == "" {
		return "-"
	}
	return value
}

// RandomBytes returns n bytes from crypto/rand.
func RandomBytes(n int) []byte {
	buffer := make([]byte, n)
	if _, err := crand.Read(buffer); err != nil {
		panic("crypto/rand: " + err.Error())
	}
	return buffer
}
