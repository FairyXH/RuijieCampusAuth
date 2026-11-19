"""PEAP vendor segments (mirrors Core.Protocols.PeapVendorSegments)."""

from __future__ import annotations

from enum import Enum

from ..private import encrypt_credentials
from .rc4 import transform as rc4  # noqa: F401  (parity re-export)


class TunnelMode(Enum):
    PEAP = 25
    EAP_TLS = 13


CREDENTIAL_IV = b"!jierui9002pmsus"


def credential(username: bytes, password: bytes) -> bytes:
    cipher = encrypt_credentials(username, password, CREDENTIAL_IV)
    return bytes([0x5F, len(cipher)]) + cipher


def mac(mac: str) -> bytes:
    from ..private import normalize_mac
    normalized = normalize_mac(mac)
    if not normalized:
        raise ValueError(f"invalid MAC address: {mac}")
    return bytes([0x2D, 0x06]) + bytes.fromhex(normalized.replace(":", ""))


def service_name(service: bytes, wide: bool = False) -> bytes:
    length = 0x40 if wide else 0x20
    payload = bytearray(length)
    payload[: min(len(service), length)] = service[:length]
    return bytes([0x39, length]) + bytes(payload)


def dhcp_phase(phase: int) -> bytes:
    if phase == 0:
        return b""
    return bytes([0x63, 0x01, phase])


def client_version_segment(payload: bytes) -> bytes:
    return bytes([0x11, 0x24]) + payload[:0x24]
