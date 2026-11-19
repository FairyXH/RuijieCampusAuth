package protocol

import "strings"

// AuthMethodKind mirrors AuthMethodKind: the configured auth_mode resolved
// into a concrete authentication path.
type AuthMethodKind int

const (
	// AuthEAPMD5 is the plain 802.1X EAP-MD5 path.
	AuthEAPMD5 AuthMethodKind = iota
	// AuthPEAPv0 is PEAPv0 with MSCHAPv2 inside the tunnel.
	AuthPEAPv0
	// AuthPEAPv1 is PEAPv1 (same tunnel, different vendor label).
	AuthPEAPv1
	// AuthEAPTLS is mutual TLS without an inner method.
	AuthEAPTLS
)

// ParseAuthMethod normalises auth_mode exactly like AuthMethodOptions.Parse.
func ParseAuthMethod(value string) AuthMethodKind {
	text := strings.TrimSpace(value)
	text = strings.ReplaceAll(text, "-", "")
	text = strings.ReplaceAll(text, "_", "")
	if text == "" {
		return AuthEAPMD5
	}
	switch strings.ToUpper(text) {
	case "EAPMD5", "MD5":
		return AuthEAPMD5
	case "PEAP", "PEAPV0", "PEAPMSCHAPV2":
		return AuthPEAPv0
	case "PEAPV1":
		return AuthPEAPv1
	case "EAPTLS", "TLS":
		return AuthEAPTLS
	default:
		return AuthEAPMD5
	}
}

// TunnelMode maps a method kind onto the TLS tunnel mode.
func (k AuthMethodKind) TunnelMode() TunnelMode {
	if k == AuthEAPTLS {
		return TunnelEAPTLS
	}
	return TunnelPEAP
}

// Display returns the operator-facing name.
func (k AuthMethodKind) Display() string {
	switch k {
	case AuthPEAPv0:
		return "PEAPv0 + MSCHAPv2"
	case AuthPEAPv1:
		return "PEAPv1 + MSCHAPv2"
	case AuthEAPTLS:
		return "EAP-TLS"
	default:
		return "EAP-MD5"
	}
}

// String implements fmt.Stringer.
func (k AuthMethodKind) String() string { return k.Display() }

// WantsDHCPBefore reports dhcp_mode == before.
func WantsDHCPBefore(mode string) bool {
	return strings.ToLower(strings.TrimSpace(mode)) == "before"
}

// WantsDHCPAfter reports dhcp_mode == after.
func WantsDHCPAfter(mode string) bool {
	return strings.ToLower(strings.TrimSpace(mode)) == "after"
}
