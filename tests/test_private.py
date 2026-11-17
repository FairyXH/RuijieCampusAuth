from __future__ import annotations

import hashlib

from scapy.all import EAPOL

from ruijie_auth.cli import (
    build_eapol_frame,
    eap_identity_response,
    eap_md5_response,
)
from ruijie_auth.private import (
    MARKER,
    NetworkInfo,
    TailOptions,
    build_tail,
    crc16_xmodem,
    encrypt_credentials,
    obfuscate,
    pad16,
)

NETWORK = NetworkInfo(
    ip="10.0.0.2",
    netmask="255.255.255.0",
    gateway="10.0.0.1",
    dhcp="10.0.0.1",
    ip_string="10.0.0.2",
)
LAYOUT_NETWORK = NetworkInfo(
    ip="10.0.0.2",
    netmask="255.255.255.0",
    gateway="10.0.0.1",
    dhcp="10.0.0.1",
    ip_string="172.22.22.3",
)
OPTIONS = TailOptions(nonce=b"A" * 32, fingerprint=b"B" * 128)


def test_header_vector() -> None:
    raw = bytes.fromhex("0000131101ac121c06ffff8000ac120001ac161602")
    assert crc16_xmodem(raw) == 0x0408
    assert obfuscate(raw + b"\x04\x08") == bytes.fromhex(
        "ffff37777fcab7c79f0000feffcab7ff7fca9797bfdfef"
    )


def test_credential_cipher_first_block() -> None:
    iv = bytes(range(16))
    result = encrypt_credentials(b"user", b"secret", iv)
    expected = bytes(
        left ^ right
        for left, right in zip(pad16(b"secret"), hashlib.md5(b"user" + iv).digest())
    )
    assert result == expected
    assert len(result) % 16 == 0


def test_tail_layout() -> None:
    tail = build_tail(
        b"user", b"secret", "00:11:22:33:44:55", LAYOUT_NETWORK, bytes(16), OPTIONS
    )
    assert len(tail) == 569
    assert tail[23:27] == MARKER
    assert tail[23 + 4 : 23 + 36] == b"8021x.exe".ljust(32, b"\x00")
    assert int.from_bytes(tail[23 + 45 : 23 + 47], "big") == 499
    assert tail.count(MARKER) >= 15


def test_eapol_length_covers_only_standard_eap() -> None:
    tail = build_tail(
        b"user", b"secret", "00:11:22:33:44:55", NETWORK, bytes(16), OPTIONS
    )
    identity = eap_identity_response(5, b"user") + tail
    frame = build_eapol_frame("00:11:22:33:44:55", "00:1a:a9:17:ff:ff", identity)
    assert frame[EAPOL].len == 9
    assert len(bytes(frame)) == 14 + 4 + 9 + len(tail)

    md5 = eap_md5_response(5, b"secret", bytes(16), b"user") + tail
    frame = build_eapol_frame("00:11:22:33:44:55", "00:1a:a9:17:ff:ff", md5)
    assert frame[EAPOL].len == 26
    assert len(bytes(frame)) == 14 + 4 + 26 + len(tail)
