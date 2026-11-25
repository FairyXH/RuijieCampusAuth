package supplicant

import (
	"context"
	"sync/atomic"
	"testing"
	"time"

	"ruijie-auth/internal/protocol"
	"ruijie-auth/internal/transport"
)

const (
	clientMAC = "aa:bb:cc:dd:ee:ff"
	authMAC   = "00:1a:a9:17:ff:ff"
)

func testOptions(status chan<- string) Options {
	return Options{
		Device:        "fake",
		Username:      "user",
		Password:      "pass",
		ServiceName:   "电信宽带服务",
		StaticID:      "Static:AB45A862",
		Timeout:       5 * time.Second,
		Retries:       2,
		Repeats:       1,
		StartTimeout:  500 * time.Millisecond,
		ResponseDelay: time.Millisecond,
		Network: func() protocol.NetworkInfo {
			return protocol.NetworkInfo{
				IP:       "172.18.28.6",
				Netmask:  "255.255.128.0",
				Gateway:  "172.18.0.1",
				DHCP:     "172.18.0.1",
				IPString: "172.18.28.6",
				StaticID: []byte("Static:AB45A862"),
			}
		},
		Status: func(state, message, server string) {
			select {
			case status <- state:
			default:
			}
		},
	}
}

func TestSessionIdentityMD5SuccessLogoff(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	statusCh := make(chan string, 32)
	commands := make(chan string, 4)
	opener := func(device string) (transport.Transport, error) { return fake, nil }

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(testOptions(statusCh), opener).Run(ctx, commands)
	}()

	waitForControl(t, fake, 1)
	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	identity := waitForEAP(t, fake, protocol.EapResponse, protocol.EapIdentity)
	if len(identity) != 18+9+569 {
		t.Fatalf("identity frame length = %d, want %d", len(identity), 18+9+569)
	}

	challenge := []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	fake.Inject(eapFrame(t, protocol.EapRequest, 2, protocol.EapMD5, challenge))
	md5Frame := waitForEAP(t, fake, protocol.EapResponse, protocol.EapMD5)
	if len(md5Frame) != 18+26+569 {
		t.Fatalf("MD5 frame length = %d, want %d", len(md5Frame), 18+26+569)
	}

	fake.Inject(eapFrame(t, protocol.EapSuccess, 2, 0, nil))
	waitForStatus(t, statusCh, "authenticated")

	commands <- "logoff"
	select {
	case result := <-resultCh:
		if result.Kind != KindLogoff {
			t.Fatalf("result kind = %v, want KindLogoff (%v)", result.Kind, result.Err)
		}
		if !result.Authenticated || !result.Connected || result.ServerMAC != authMAC {
			t.Fatalf("result = %+v", result)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("session did not stop after logoff")
	}
}

func TestSessionFailure(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	statusCh := make(chan string, 32)
	commands := make(chan string, 2)
	opener := func(device string) (transport.Transport, error) { return fake, nil }
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(testOptions(statusCh), opener).Run(ctx, commands)
	}()

	waitForControl(t, fake, 1)
	fake.Inject(eapFrame(t, protocol.EapFailure, 1, 0, nil))
	select {
	case result := <-resultCh:
		if result.Kind != KindFailure {
			t.Fatalf("result kind = %v, want KindFailure", result.Kind)
		}
		if !result.Connected || result.ServerMAC != authMAC {
			t.Fatalf("result = %+v", result)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("session did not return failure")
	}
}

func TestSessionNoServer(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	options := testOptions(make(chan string, 8))
	options.Timeout = 200 * time.Millisecond
	options.Retries = 1
	options.StartTimeout = 50 * time.Millisecond
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(options, func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, make(chan string, 1))
	}()
	select {
	case result := <-resultCh:
		if result.Kind != KindNoServer {
			t.Fatalf("result kind = %v, want KindNoServer", result.Kind)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("session did not return no-server")
	}
}

// TestStartRetryStopsAfterServerReply verifies that EAPOL-Start is only
// repeated while no authenticator has answered. The C# client and the live
// capture both stop hunting after the first server packet; continuing would
// restart authentication mid-handshake.
func TestStartRetryStopsAfterServerReply(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	options := testOptions(make(chan string, 8))
	options.Timeout = 3 * time.Second
	options.Retries = 4
	options.StartTimeout = 100 * time.Millisecond
	options.Repeats = 1
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(options, func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, make(chan string, 1))
	}()

	waitForControl(t, fake, 1)
	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	waitForEAP(t, fake, protocol.EapResponse, protocol.EapIdentity)

	time.Sleep(350 * time.Millisecond)
	if starts := countEAPOL(fake, 1); starts != 1 {
		t.Fatalf("EAPOL-Start sent %d times after server replied, want 1", starts)
	}
	cancel()
	select {
	case <-resultCh:
	case <-time.After(2 * time.Second):
		t.Fatal("session did not stop")
	}
}

// TestServerInitiatedReauthTimesOut covers a request that arrives after the
// session reached authenticated. The resident loop answers it, but must arm
// the normal auth timer so a lost final result cannot leave it half-open.
func TestServerInitiatedReauthTimesOut(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	statusCh := make(chan string, 32)
	options := testOptions(statusCh)
	options.Timeout = 600 * time.Millisecond
	options.StartTimeout = 500 * time.Millisecond
	options.Retries = 2
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(options, func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, make(chan string, 1))
	}()

	waitForControl(t, fake, 1)
	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	waitForEAP(t, fake, protocol.EapResponse, protocol.EapIdentity)
	challenge := []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	fake.Inject(eapFrame(t, protocol.EapRequest, 2, protocol.EapMD5, challenge))
	waitForEAP(t, fake, protocol.EapResponse, protocol.EapMD5)
	fake.Inject(eapFrame(t, protocol.EapSuccess, 2, 0, nil))
	waitForStatus(t, statusCh, "authenticated")

	fake.Inject(eapFrame(t, protocol.EapRequest, 5, protocol.EapIdentity, nil))
	waitForEAPCount(t, fake, protocol.EapResponse, protocol.EapIdentity, 2)

	select {
	case result := <-resultCh:
		if result.Kind != KindTimeout {
			t.Fatalf("result kind = %v, want KindTimeout", result.Kind)
		}
		if !result.Connected || result.Authenticated {
			t.Fatalf("result = %+v", result)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("server-initiated re-auth did not time out")
	}
}

// TestReauthClearsDuplicateFilter reproduces the resident-session case the C#
// worker never hits: a second Identity request may reuse the identifier, so
// the duplicate filter must be reset on re-authentication.
func TestReauthClearsDuplicateFilter(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	statusCh := make(chan string, 32)
	commands := make(chan string, 4)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(testOptions(statusCh), func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, commands)
	}()

	waitForControl(t, fake, 1)
	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	waitForEAP(t, fake, protocol.EapResponse, protocol.EapIdentity)

	challenge := []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	fake.Inject(eapFrame(t, protocol.EapRequest, 2, protocol.EapMD5, challenge))
	waitForEAP(t, fake, protocol.EapResponse, protocol.EapMD5)
	fake.Inject(eapFrame(t, protocol.EapSuccess, 2, 0, nil))
	waitForStatus(t, statusCh, "authenticated")

	commands <- "auth"
	time.Sleep(250 * time.Millisecond)
	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	waitForEAPCount(t, fake, protocol.EapResponse, protocol.EapIdentity, 2)

	cancel()
	select {
	case <-resultCh:
	case <-time.After(2 * time.Second):
		t.Fatal("session did not stop")
	}
}

// TestSessionTailSemantics locks the three tail flavours observed in the
// live capture: Start keeps the session nonce with a zero fingerprint,
// Identity reuses the session nonce with a random fingerprint, and the MD5
// response uses fresh random nonce/fingerprint.
func TestSessionTailSemantics(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	options := testOptions(make(chan string, 8))
	options.Repeats = 1
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(options, func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, make(chan string, 1))
	}()

	waitForControl(t, fake, 1)
	startNonce, startFP := frameTail(t, firstControl(fake, 1))

	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	identityFrame := waitForEAP(t, fake, protocol.EapResponse, protocol.EapIdentity)
	identityNonce, identityFP := frameTail(t, identityFrame)

	challenge := []byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	fake.Inject(eapFrame(t, protocol.EapRequest, 2, protocol.EapMD5, challenge))
	md5Frame := waitForEAP(t, fake, protocol.EapResponse, protocol.EapMD5)
	md5Nonce, md5FP := frameTail(t, md5Frame)

	if string(startNonce) != string(identityNonce) {
		t.Fatalf("Start nonce %q != Identity nonce %q", startNonce, identityNonce)
	}
	if string(startNonce) == string(md5Nonce) {
		t.Fatal("MD5 response reused the session nonce")
	}
	if !allZero(startFP) {
		t.Fatalf("Start fingerprint is not zero: % x", startFP[:8])
	}
	if allZero(identityFP) || allZero(md5FP) {
		t.Fatal("Identity/MD5 fingerprint must be random")
	}
	if string(identityFP) == string(md5FP) {
		t.Fatal("Identity and MD5 responses reused the fingerprint")
	}

	cancel()
	select {
	case <-resultCh:
	case <-time.After(2 * time.Second):
		t.Fatal("session did not stop")
	}
}

func firstControl(fake *transport.Fake, eapolType byte) []byte {
	for _, frame := range fake.Sent() {
		if len(frame) >= 18 && frame[15] == eapolType {
			return frame
		}
	}
	return nil
}

// frameTail extracts attribute 0x17 (nonce) and 0x4D (fingerprint) from the
// 569-byte private tail that follows the EAPOL length field.
func frameTail(t *testing.T, frame []byte) ([]byte, []byte) {
	t.Helper()
	if len(frame) < 18 {
		t.Fatalf("short frame: %d", len(frame))
	}
	eapLength := int(frame[16])<<8 | int(frame[17])
	tail := frame[18+eapLength:]
	if len(tail) != 569 {
		t.Fatalf("tail length = %d, want 569", len(tail))
	}
	var nonce, fingerprint []byte
	for offset := 70; offset+8 <= len(tail) && tail[offset] == 0x1A; {
		total := int(tail[offset+1])
		typ := tail[offset+6]
		value := tail[offset+8 : offset+total]
		switch typ {
		case 0x17:
			nonce = value
		case 0x4D:
			fingerprint = value
		}
		offset += total
	}
	if nonce == nil || fingerprint == nil {
		t.Fatal("tail is missing 0x17/0x4D")
	}
	return nonce, fingerprint
}

func allZero(value []byte) bool {
	for _, item := range value {
		if item != 0 {
			return false
		}
	}
	return true
}

func countEAPOL(fake *transport.Fake, eapolType byte) int {
	count := 0
	for _, frame := range fake.Sent() {
		if len(frame) >= 18 && frame[15] == eapolType {
			count++
		}
	}
	return count
}

func eapFrame(t *testing.T, code, identifier, typ byte, data []byte) []byte {
	t.Helper()
	var eap []byte
	if typ == 0 {
		eap = []byte{code, identifier, 0, 4}
	} else if typ == protocol.EapMD5 {
		length := 6 + len(data)
		eap = []byte{code, identifier, byte(length >> 8), byte(length), typ, byte(len(data))}
		eap = append(eap, data...)
	} else {
		length := 5 + len(data)
		eap = []byte{code, identifier, byte(length >> 8), byte(length), typ}
		eap = append(eap, data...)
	}
	frame, err := protocol.BuildFrame(authMAC, clientMAC, eap, nil)
	if err != nil {
		t.Fatal(err)
	}
	return frame
}

func waitForControl(t *testing.T, fake *transport.Fake, eapolType byte) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		for _, frame := range fake.Sent() {
			if len(frame) >= 18 && frame[15] == eapolType {
				return
			}
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("EAPOL control type %d was not sent", eapolType)
}

func waitForEAP(t *testing.T, fake *transport.Fake, code, typ byte) []byte {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		for _, frame := range fake.Sent() {
			parsedCode, _, eap, ok := protocol.TryParseEAP(frame)
			if !ok || parsedCode != code || len(eap) < 5 || eap[4] != typ {
				continue
			}
			return frame
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("EAP response code=%d type=%d was not sent", code, typ)
	return nil
}

func waitForEAPCount(t *testing.T, fake *transport.Fake, code, typ byte, want int) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		count := 0
		for _, frame := range fake.Sent() {
			parsedCode, _, eap, ok := protocol.TryParseEAP(frame)
			if !ok || parsedCode != code || len(eap) < 5 || eap[4] != typ {
				continue
			}
			count++
		}
		if count >= want {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("EAP response code=%d type=%d was sent fewer than %d times", code, typ, want)
}

func waitForStatus(t *testing.T, statusCh <-chan string, want string) {
	t.Helper()
	deadline := time.After(3 * time.Second)
	for {
		select {
		case state := <-statusCh:
			if state == want {
				return
			}
		case <-deadline:
			t.Fatalf("status %q was not reported", want)
		}
	}
}

// TestSessionStopsWhenQuietWindowOpens covers the resident retry loop: once a
// session is holding, the daemon never regains control, so the auth-off window
// has to end the session from inside it. Without this the daemon retried every
// few seconds straight through 00:00-06:00.
func TestSessionStopsWhenQuietWindowOpens(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	statusCh := make(chan string, 32)
	options := testOptions(statusCh)
	options.QuietPoll = 10 * time.Millisecond
	var quiet atomic.Bool
	options.Quiet = func() bool { return quiet.Load() }

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(options, func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, make(chan string, 1))
	}()

	waitForControl(t, fake, 1)
	quiet.Store(true)

	select {
	case result := <-resultCh:
		if result.Kind != KindQuiet {
			t.Fatalf("result kind = %v, want KindQuiet (%v)", result.Kind, result.Err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("session kept running after the quiet window opened")
	}
}

// TestSessionIgnoresOpenQuietWindow covers a forced auth: the daemon clears
// its override flag and the session must not pause again.
func TestSessionIgnoresClosedQuietWindow(t *testing.T) {
	fake := transport.NewFake(clientMAC)
	statusCh := make(chan string, 32)
	options := testOptions(statusCh)
	options.QuietPoll = 10 * time.Millisecond
	options.Quiet = func() bool { return false }

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	resultCh := make(chan Result, 1)
	go func() {
		resultCh <- New(options, func(device string) (transport.Transport, error) {
			return fake, nil
		}).Run(ctx, make(chan string, 1))
	}()

	waitForControl(t, fake, 1)
	fake.Inject(eapFrame(t, protocol.EapRequest, 1, protocol.EapIdentity, nil))
	waitForEAP(t, fake, protocol.EapResponse, protocol.EapIdentity)
	fake.Inject(eapFrame(t, protocol.EapSuccess, 1, 0, nil))
	waitForStatus(t, statusCh, "authenticated")

	select {
	case result := <-resultCh:
		t.Fatalf("session stopped with an inactive window: %+v", result)
	case <-time.After(200 * time.Millisecond):
		// Still resident, as expected.
	}
	cancel()
}

