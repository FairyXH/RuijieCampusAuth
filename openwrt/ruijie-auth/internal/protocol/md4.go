package protocol

import "encoding/binary"

// MD4Hash is RFC 1320 MD4, required by MSCHAPv2. The implementation follows
// RuijieCampusAuth.Core.Protocols.Md4.Hash operation for operation so the
// digests are identical for every input length.
func MD4Hash(input []byte) []byte {
	a := uint32(0x67452301)
	b := uint32(0xefcdab89)
	c := uint32(0x98badcfe)
	d := uint32(0x10325476)

	length := len(input)
	paddedLength := ((length+8)/64 + 1) * 64
	buffer := make([]byte, paddedLength)
	copy(buffer, input)
	buffer[length] = 0x80
	bitLength := uint64(length) * 8
	for i := 0; i < 8; i++ {
		buffer[paddedLength-8+i] = byte(bitLength >> (8 * i))
	}

	round2 := [16]int{0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15}
	round3 := [16]int{0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15}
	shift2 := [4]int{3, 5, 9, 13}
	shift3 := [4]int{3, 9, 11, 15}

	var x [16]uint32
	for offset := 0; offset < paddedLength; offset += 64 {
		for i := 0; i < 16; i++ {
			x[i] = binary.LittleEndian.Uint32(buffer[offset+i*4:])
		}
		aa, bb, cc, dd := a, b, c, d

		for i := 0; i < 16; i++ {
			sum := x[i]
			switch i % 4 {
			case 0:
				a = rotl(a+md4F(b, c, d)+sum, 3)
			case 1:
				d = rotl(d+md4F(a, b, c)+sum, 7)
			case 2:
				c = rotl(c+md4F(d, a, b)+sum, 11)
			case 3:
				b = rotl(b+md4F(c, d, a)+sum, 19)
			}
		}
		for i := 0; i < 16; i++ {
			sum := x[round2[i]] + 0x5a827999
			shift := shift2[i%4]
			switch i % 4 {
			case 0:
				a = rotl(a+md4G(b, c, d)+sum, shift)
			case 1:
				d = rotl(d+md4G(a, b, c)+sum, shift)
			case 2:
				c = rotl(c+md4G(d, a, b)+sum, shift)
			case 3:
				b = rotl(b+md4G(c, d, a)+sum, shift)
			}
		}
		for i := 0; i < 16; i++ {
			sum := x[round3[i]] + 0x6ed9eba1
			shift := shift3[i%4]
			switch i % 4 {
			case 0:
				a = rotl(a+md4H(b, c, d)+sum, shift)
			case 1:
				d = rotl(d+md4H(a, b, c)+sum, shift)
			case 2:
				c = rotl(c+md4H(d, a, b)+sum, shift)
			case 3:
				b = rotl(b+md4H(c, d, a)+sum, shift)
			}
		}

		a += aa
		b += bb
		c += cc
		d += dd
	}

	result := make([]byte, 16)
	binary.LittleEndian.PutUint32(result[0:], a)
	binary.LittleEndian.PutUint32(result[4:], b)
	binary.LittleEndian.PutUint32(result[8:], c)
	binary.LittleEndian.PutUint32(result[12:], d)
	return result
}

func rotl(value uint32, bits int) uint32 {
	return value<<bits | value>>(32-bits)
}

func md4F(x, y, z uint32) uint32 { return (x & y) | (^x & z) }
func md4G(x, y, z uint32) uint32 { return (x & y) | (x & z) | (y & z) }
func md4H(x, y, z uint32) uint32 { return x ^ y ^ z }
