package protocol

import "testing"

// TestDecodePrivateNumberWidths locks the widening rules to the C#
// PrivateProperties.DecodeNumber behaviour. In particular, 2-byte values are
// unsigned because C# promotes each byte to int before ORing.
func TestDecodePrivateNumberWidths(t *testing.T) {
	cases := []struct {
		value []byte
		want  int64
	}{
		{[]byte{0xFF}, 255},
		{[]byte{0xFF, 0xFE}, 65534},
		{[]byte{0x00, 0x8A}, 138},
		{[]byte{0xAC, 0x16, 0x0B, 0x01}, 0xAC160B01},
		{[]byte{0x01, 0x00, 0x01, 0x00}, 0x01000100},
	}
	for _, test := range cases {
		got := decodePrivateNumber(test.value)
		if got == nil || *got != test.want {
			t.Fatalf("decode(% x) = %v, want %d", test.value, got, test.want)
		}
	}
	if got := decodePrivateNumber([]byte{1, 2, 3}); got != nil {
		t.Fatalf("3-byte value = %d, want nil", *got)
	}
}
