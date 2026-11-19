package protocol

// RC4Transform is the length-preserving XOR keystream used for the
// ENCRYPT_KEY / ENCRYPT_IV server properties. It mirrors
// RuijieCampusAuth.Core.Protocols.Rc4.Transform one for one: same key
// schedule, same swap order, same output bytes.
//
// RC4 is wire compatibility with the vendor client, not a modern cipher.
func RC4Transform(data, key []byte) []byte {
	if len(key) == 0 {
		return nil
	}
	var state [256]byte
	for i := 0; i < 256; i++ {
		state[i] = byte(i)
	}
	j := 0
	for i := 0; i < 256; i++ {
		j = (j + int(state[i]) + int(key[i%len(key)])) & 0xFF
		state[i], state[j] = state[j], state[i]
	}
	output := make([]byte, len(data))
	a, b := 0, 0
	for i := 0; i < len(data); i++ {
		a = (a + 1) & 0xFF
		b = (b + int(state[a])) & 0xFF
		state[a], state[b] = state[b], state[a]
		k := state[(int(state[a])+int(state[b]))&0xFF]
		output[i] = data[i] ^ k
	}
	return output
}
