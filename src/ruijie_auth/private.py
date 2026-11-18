"""Ruijie private vendor tail appended to EAP-MD5 responses.

The vendor tail is not part of the EAP payload length. The EAP header keeps the
standard length while the EAPOL header covers EAP payload + this tail.
"""

from __future__ import annotations

import hashlib
import ipaddress
import os
import re
from dataclasses import dataclass, field

MARKER = b"\x00\x00\x13\x11"
MAC_PATTERN = re.compile(r"^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$")


def normalize_mac(value: str | None) -> str:
    """Return a lowercase colon-separated MAC, or '' when invalid."""
    text = str(value or "").strip().lower().replace("-", ":")
    if re.fullmatch(r"[0-9a-f]{12}", text):
        text = ":".join(text[index : index + 2] for index in range(0, 12, 2))
    return text if MAC_PATTERN.fullmatch(text) else ""
CLIENT_NAME = b"8021x.exe"
CLIENT_VERSION = b"\x06\x56\x00\x00"
CLIENT_FLAG = b"\x01"
DEFAULT_STATIC_ID = b"Static:AB45A862"
ATTR_TAG = 0x1A

ZERO_IP = b"\x00\x00\x00\x00"


def crc16_xmodem(data: bytes) -> int:
    """CRC-16/XMODEM, matching the client's private header checksum."""
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = (
                ((crc << 1) ^ 0x1021) & 0xFFFF
                if crc & 0x8000
                else (crc << 1) & 0xFFFF
            )
    return crc


def obfuscate(data: bytes) -> bytes:
    """Bit-reverse every byte, then invert it (the client's header shuffle)."""
    result = bytearray()
    for byte in data:
        value = (~byte) & 0xFF
        result.append(
            ((value & 0x10) >> 1)
            | ((value & 0x20) >> 3)
            | ((value & 0x40) >> 5)
            | ((value & 0x80) >> 7)
            | (2 * (value & 0x08))
            | (8 * (value & 0x04))
            | (32 * (value & 0x02))
            | ((value & 0x01) << 7)
        )
    return bytes(result)


def pad16(data: bytes) -> bytes:
    """Zero-pad to a 16-byte boundary; empty input becomes one block."""
    if not data:
        return bytes(16)
    remainder = len(data) % 16
    if remainder:
        return data + bytes(16 - remainder)
    return data


def encrypt_credentials(username: bytes, password: bytes, iv: bytes) -> bytes:
    """Ruijie's MD5-chained XOR cipher.

    cipher_block = plain_block XOR MD5(username || previous_block)
    where previous_block is ``iv`` for the first block and the previous
    ciphertext for later blocks.
    """
    if len(iv) != 16:
        raise ValueError("iv must be exactly 16 bytes")
    plain = pad16(password)
    previous = iv
    output = bytearray()
    for offset in range(0, len(plain), 16):
        block = plain[offset : offset + 16]
        stream = hashlib.md5(username + previous).digest()
        cipher = bytes(a ^ b for a, b in zip(block, stream))
        output.extend(cipher)
        previous = cipher
    return bytes(output)


def _ip_bytes(value: str) -> bytes:
    try:
        return ipaddress.IPv4Address(value).packed
    except (ipaddress.AddressValueError, ValueError):
        return ZERO_IP


@dataclass(frozen=True)
class NetworkInfo:
    ip: str = ""
    netmask: str = ""
    gateway: str = ""
    dhcp: str = ""
    ipv6: bytes = bytes(16)
    ip_string: str = ""
    static_id: bytes = DEFAULT_STATIC_ID


def collect_network_info(interface: str) -> NetworkInfo:
    """Read the IPv4 details used in the private header from the interface."""
    from scapy.all import conf
    from scapy.interfaces import resolve_iface

    info = resolve_iface(interface)
    ip = str(getattr(info, "ip", "") or "")
    netmask = ""
    gateway = ""
    try:
        for destination, mask, route_gateway, route_iface, output_ip, _ in conf.route.routes:
            if route_iface != interface:
                continue
            if destination == 0 and mask == 0 and route_gateway != "0.0.0.0":
                gateway = str(route_gateway)
                break
        for destination, mask, route_gateway, route_iface, output_ip, _ in conf.route.routes:
            if route_iface != interface or destination == 0:
                continue
            if mask and route_gateway == "0.0.0.0" and (not ip or output_ip == ip):
                netmask = str(ipaddress.IPv4Address(mask))
                break
    except Exception:  # pragma: no cover - routing table is platform specific
        pass
    return NetworkInfo(
        ip=ip,
        netmask=netmask,
        gateway=gateway,
        dhcp=gateway,
        ip_string=ip,
    )


@dataclass(frozen=True)
class TailOptions:
    client_name: bytes = CLIENT_NAME
    client_version: bytes = CLIENT_VERSION
    client_flag: bytes = CLIENT_FLAG
    header_flag: int = 1
    nonce: bytes = field(default_factory=lambda: os.urandom(16).hex().upper().encode())
    fingerprint: bytes = field(
        default_factory=lambda: os.urandom(64).hex().upper().encode()
    )
    service_name: bytes = "电信宽带服务".encode("gb18030")


def _attribute(typ: int, payload: bytes) -> bytes:
    total = 8 + len(payload)
    if total > 0xFF:
        raise ValueError(f"attribute 0x{typ:02x} is too long: {total}")
    return (
        bytes([ATTR_TAG, total])
        + MARKER
        + bytes([typ, 2 + len(payload)])
        + payload
    )


def _header(network: NetworkInfo, flag: int) -> bytes:
    raw = (
        MARKER
        + bytes([flag & 0xFF])
        + _ip_bytes(network.ip)
        + _ip_bytes(network.netmask)
        + _ip_bytes(network.gateway)
        + _ip_bytes(network.dhcp)
    )
    raw += crc16_xmodem(raw).to_bytes(2, "big")
    return obfuscate(raw)


def build_tail(
    username: bytes,
    password: bytes,
    mac: str,
    network: NetworkInfo,
    iv: bytes,
    options: TailOptions | None = None,
) -> bytes:
    """Build the 569-byte private tail for one EAP response."""
    options = options or TailOptions()
    mac_bytes = bytes.fromhex(mac.replace(":", "").replace("-", ""))
    if len(mac_bytes) != 6:
        raise ValueError(f"invalid MAC address: {mac}")

    encrypted = encrypt_credentials(username, password, iv)
    attributes = b"".join(
        [
            _attribute(0x17, options.nonce),
            _attribute(0x18, bytes(4)),
            _attribute(0x2D, mac_bytes),
            _attribute(0x2F, encrypted),
            _attribute(0x35, b"\x01"),
            _attribute(0x36, bytes(16)),
            _attribute(0x38, network.ipv6[:16].ljust(16, b"\x00")),
            _attribute(0x4E, bytes(16)),
            _attribute(0x4D, options.fingerprint),
            _attribute(0x39, options.service_name[:32].ljust(32, b"\x00")),
            _attribute(0x54, network.static_id[:64].ljust(64, b"\x00")),
            _attribute(0x62, b"\x00"),
            _attribute(0x6B, b"\x00"),
            _attribute(0x7E, b"\x00\x00\x01\x00\x00\x0c\x00\x00"),
            _attribute(0x70, b"\x40"),
            _attribute(0x6F, b"\x00"),
            _attribute(0x79, b"\x02"),
            _attribute(0x76, (network.ip_string or network.ip or "0.0.0.0").encode("ascii")),
        ]
    )
    preamble = (
        MARKER
        + options.client_name[:32].ljust(32, b"\x00")
        + options.client_version
        + options.client_flag
        + MARKER
        + len(attributes).to_bytes(2, "big")
    )
    return _header(network, options.header_flag) + preamble + attributes
