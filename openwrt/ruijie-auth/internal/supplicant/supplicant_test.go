package supplicant

import (
	"context"
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
