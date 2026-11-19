"""RC4 stream cipher (mirrors RuijieCampusAuth.Core.Protocols.Rc4)."""


def transform(data: bytes, key: bytes) -> bytes:
    if not key:
        raise ValueError("RC4 key must not be empty")
    state = list(range(256))
    j = 0
    for i in range(256):
        j = (j + state[i] + key[i % len(key)]) & 0xFF
        state[i], state[j] = state[j], state[i]
    output = bytearray(len(data))
    a = 0
    b = 0
    for i, byte in enumerate(data):
        a = (a + 1) & 0xFF
        b = (b + state[a]) & 0xFF
        state[a], state[b] = state[b], state[a]
        output[i] = byte ^ state[(state[a] + state[b]) & 0xFF]
    return bytes(output)
