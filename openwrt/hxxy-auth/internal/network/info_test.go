package network

import "testing"

func TestParseDefaultGateway(t *testing.T) {
	data := `Iface	Destination	Gateway 	Flags	RefCnt	Use	Metric	Mask		MTU	Window	IRTT
br-lan	00000000	0201A8C0	0003	0	0	0	00000000	0	0	0
eth0	0001A8C0	00000000	0001	0	0	0	00FFFFFF	0	0	0
`
	if got := parseDefaultGateway(data, "br-lan"); got != "192.168.1.2" {
		t.Fatalf("gateway = %q, want 192.168.1.2", got)
	}
	if got := parseDefaultGateway(data, "eth0"); got != "" {
		t.Fatalf("eth0 gateway = %q, want empty", got)
	}
	if got := parseDefaultGateway(data, "missing"); got != "" {
		t.Fatalf("missing gateway = %q, want empty", got)
	}
}
