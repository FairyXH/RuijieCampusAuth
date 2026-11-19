"""MSCHAPv2 + MD4 (mirrors Core.Protocols.MschapV2)."""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

from ..private import crc16_xmodem  # noqa: F401  (parity re-export)

CHALLENGE_OPCODE = 1
RESPONSE_OPCODE = 2
SUCCESS_OPCODE = 3
FAILURE_OPCODE = 4


def md4_hash(input_: bytes) -> bytes:
    """Pure-python MD4 (RFC 1320); hashlib lacks it on modern OpenSSL."""
    a0, b0, c0, d0 = 0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476
    length = len(input_)
    padded = bytearray(input_)
    padded.append(0x80)
    while len(padded) % 64 != 56:
        padded.append(0)
    padded += ((length * 8) & 0xFFFFFFFFFFFFFFFF).to_bytes(8, "little")

    def rotl(value: int, bits: int) -> int:
        return ((value << bits) | (value >> (32 - bits))) & 0xFFFFFFFF

    f_x = lambda x, y, z: (x & y) | (~x & z)            # noqa: E731
    g_x = lambda x, y, z: (x & y) | (x & z) | (y & z)   # noqa: E731
    h_x = lambda x, y, z: x ^ y ^ z                     # noqa: E731

    for block in range(0, len(padded), 64):
        x = [int.from_bytes(padded[block + i * 4: block + i * 4 + 4], "little")
             for i in range(16)]
        a, b, c, d = a0, b0, c0, d0
        for i in range(16):
            k = i
            s = [3, 7, 11, 19][i % 4]
            if i % 4 == 0:
                a = rotl((a + f_x(b, c, d) + x[k]) & 0xFFFFFFFF, s)
            elif i % 4 == 1:
                d = rotl((d + f_x(a, b, c) + x[k]) & 0xFFFFFFFF, s)
            elif i % 4 == 2:
                c = rotl((c + f_x(d, a, b) + x[k]) & 0xFFFFFFFF, s)
            else:
                b = rotl((b + f_x(c, d, a) + x[k]) & 0xFFFFFFFF, s)
        for i in range(16):
            order = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15]
            k = order[i]
            s = [3, 5, 9, 13][i % 4]
            if i % 4 == 0:
                a = rotl((a + g_x(b, c, d) + x[k] + 0x5A827999) & 0xFFFFFFFF, s)
            elif i % 4 == 1:
                d = rotl((d + g_x(a, b, c) + x[k] + 0x5A827999) & 0xFFFFFFFF, s)
            elif i % 4 == 2:
                c = rotl((c + g_x(d, a, b) + x[k] + 0x5A827999) & 0xFFFFFFFF, s)
            else:
                b = rotl((b + g_x(c, d, a) + x[k] + 0x5A827999) & 0xFFFFFFFF, s)
        for i in range(16):
            order = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15]
            k = order[i]
            s = [3, 9, 11, 15][i % 4]
            if i % 4 == 0:
                a = rotl((a + h_x(b, c, d) + x[k] + 0x6ED9EBA1) & 0xFFFFFFFF, s)
            elif i % 4 == 1:
                d = rotl((d + h_x(a, b, c) + x[k] + 0x6ED9EBA1) & 0xFFFFFFFF, s)
            elif i % 4 == 2:
                c = rotl((c + h_x(d, a, b) + x[k] + 0x6ED9EBA1) & 0xFFFFFFFF, s)
            else:
                b = rotl((b + h_x(c, d, a) + x[k] + 0x6ED9EBA1) & 0xFFFFFFFF, s)
        a0 = (a0 + a) & 0xFFFFFFFF
        b0 = (b0 + b) & 0xFFFFFFFF
        c0 = (c0 + c) & 0xFFFFFFFF
        d0 = (d0 + d) & 0xFFFFFFFF
    return (a0 + (b0 << 32) + (c0 << 64) + (d0 << 96)).to_bytes(16, "little")


def _expand_des_key(key7: bytes) -> bytes:
    return bytes([
        key7[0],
        ((key7[0] << 7) | (key7[1] >> 1)) & 0xFF,
        ((key7[1] << 6) | (key7[2] >> 2)) & 0xFF,
        ((key7[2] << 5) | (key7[3] >> 3)) & 0xFF,
        ((key7[3] << 4) | (key7[4] >> 4)) & 0xFF,
        ((key7[4] << 3) | (key7[5] >> 5)) & 0xFF,
        ((key7[5] << 2) | (key7[6] >> 6)) & 0xFF,
        (key7[6] << 1) & 0xFF,
    ])


def _set_odd_parity(key: bytearray) -> None:
    for i, value in enumerate(key):
        v = value & 0xFE
        parity = sum((v >> bit) & 1 for bit in range(8))
        if parity % 2 == 0:
            v |= 1
        key[i] = v


def _des_ecb_encrypt(key: bytes, block: bytes) -> bytes:
    try:
        from Crypto.Cipher import DES  # type: ignore
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError(
            "pycryptodome is required for MSCHAPv2 DES (pip install pycryptodome)"
        ) from exc
    cipher = DES.new(key, DES.MODE_ECB)
    return cipher.encrypt(block)


@dataclass(frozen=True)
class MschapChallenge:
    identifier: int
    mschap_identifier: int
    challenge: bytes
    name: str


@dataclass(frozen=True)
class MschapResponse:
    peer_challenge: bytes
    nt_response: bytes
    user_name: str


def try_parse_challenge(eap_packet: bytes):
    """Return (MschapChallenge|None, error|None)."""
    if len(eap_packet) < 5 + 21 or eap_packet[0] != 1 or eap_packet[4] != 26:
        return None, "not an EAP-MSCHAPv2 packet"
    data = eap_packet[5:]
    if data[0] != CHALLENGE_OPCODE or data[4] != 16:
        return None, "not an MSCHAPv2 challenge"
    name_length = len(data) - 21
    if name_length < 0:
        return None, "challenge too short"
    return (MschapChallenge(
        eap_packet[1], data[1], bytes(data[5:21]),
        data[21:].decode("utf-8", errors="replace")), None)


def is_success(eap_packet: bytes) -> bool:
    return len(eap_packet) >= 5 and eap_packet[4] == 26 and eap_packet[5] == SUCCESS_OPCODE


def is_failure(eap_packet: bytes) -> bool:
    return len(eap_packet) >= 5 and eap_packet[4] == 26 and eap_packet[5] == FAILURE_OPCODE


def challenge_hash(peer_challenge: bytes, authenticator_challenge: bytes,
                   user_name: str) -> bytes:
    name = user_name.split("\\")[-1].encode("utf-8")
    return hashlib.sha1(peer_challenge + authenticator_challenge + name).digest()[:8]


def generate_nt_response(authenticator_challenge: bytes, peer_challenge: bytes,
                          user_name: str, password: str) -> bytes:
    challenge = challenge_hash(peer_challenge, authenticator_challenge, user_name)
    password_hash = md4_hash(password.encode("utf-16-le"))
    z_password_hash = password_hash + b"\x00" * 5
    response = bytearray(24)
    for i in range(3):
        key = bytearray(_expand_des_key(z_password_hash[i * 7: i * 7 + 7]))
        _set_odd_parity(key)
        response[i * 8: i * 8 + 8] = _des_ecb_encrypt(bytes(key), challenge)
    return bytes(response)


def build_response(challenge: MschapChallenge, user_name: str, password: str,
                   peer_challenge: bytes) -> bytes:
    if len(peer_challenge) != 16:
        raise ValueError("peer challenge must be 16 bytes")
    nt_response = generate_nt_response(challenge.challenge, peer_challenge,
                                       user_name, password)
    name = user_name.encode("utf-8")
    ms_length = 5 + 49 + len(name)
    packet = bytearray(5 + ms_length)
    packet[0] = RESPONSE_OPCODE
    packet[1] = challenge.identifier
    packet[2] = (5 + ms_length) >> 8
    packet[3] = (5 + ms_length) & 0xFF
    packet[4] = 26
    packet[5] = RESPONSE_OPCODE
    packet[6] = challenge.mschap_identifier
    packet[7] = ms_length >> 8
    packet[8] = ms_length & 0xFF
    packet[9] = 49  # value size
    packet[10:26] = peer_challenge
    # bytes 26..33 reserved (zero)
    packet[34:58] = nt_response
    packet[58] = 0  # flags
    packet[59:59 + len(name)] = name
    return bytes(packet)
