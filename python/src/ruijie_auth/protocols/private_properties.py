"""Server private-property parser (mirrors Core.Protocols.PrivateProperties).

The wire format is the *original sectioned* layout recovered from
8021x.exe ParsePrivateProperty (0x537EE0) and verified against a live campus
capture:

  [1311][2B len][GBK message]                        section 1
  [1311][2B][4B value][100B zero][1B count]          section 2 (0x6F layout)
  [1311][01][00]                                     section 3 gate
  [1311][01][01]                                     section 4 gate
  [15 obfuscated bytes] -> [1311][0A gate][1B][4B hash][4B sec][1B]
  [1311][type][len=value+2][value] ...               property TLV loop
"""

from __future__ import annotations

from dataclasses import dataclass, field

from ..private import obfuscate
from .rc4 import transform as _rc4_transform

MAXIMUM_PACKET_LENGTH = 0x578
RC4_KEY = b"com.ruijie.www"
_MARKER = b"\x00\x00\x13\x11"

_STRING_TYPES = {
    0x37,  # SU_UPGRADE_URL
    0x3C,  # ACCOUNT_INFO
    0x3D,  # PERSIONAL_INFO
    0x42,  # SERVER_LIST_UPDATE
    0x52,  # BROADCASE_INFO
    0x53,  # REJECT_REASON
    0x66,  # SERVER_LIST_UPDATE_LEGACY
    0x68,  # USER_LOGIN_URL
    0x72,  # UTRUST_URL
}

NAMES = {
    0x05: "SU_NEWEST_VER",
    0x14: "PROXY_AVOID",
    0x15: "DIALUP_AVOID",
    0x37: "SU_UPGRADE_URL",
    0x3C: "ACCOUNT_INFO",
    0x3D: "PERSIONAL_INFO",
    0x42: "SERVER_LIST_UPDATE",
    0x52: "BROADCASE_INFO",
    0x53: "REJECT_REASON",
    0x56: "SU_REAUTH_INTERV",
    0x59: "HELLO_INTERV",
    0x5A: "INDICATE_PORT",
    0x5B: "INDICATE_SERV_IP",
    0x5C: "ENCRYPT_KEY",
    0x5D: "ENCRYPT_IV",
    0x5E: "SERVER_UTC_TIME",
    0x61: "RADIUS_SERV_TYPE",
    0x66: "SERVER_LIST_UPDATE_LEGACY",
    0x68: "USER_LOGIN_URL",
    0x6E: "MSG_CLIENT_PORT",
    0x72: "UTRUST_URL",
    0x73: "SMP_IPADDRESS",
    0x74: "PROXY_DECTECT_KINDS",
    0x75: "CLIENT_MSG_PERIOD",
    0x77: "AUTO_OPEN_SSO_URL",
    0x79: "DIRECT_COMM_HIGHEST_VERSION",
    0x80: "DIRECT_COMM_HEARTBEAT_FLAGS",
    0x98: "WIFI_CNNT_NUM_FLAGS",
}


def name_of(property_type: int) -> str:
    return NAMES.get(property_type, f"UNKNOWN_0x{property_type:02X}")


@dataclass(frozen=True)
class PrivateProperty:
    type: int
    name: str
    raw: bytes
    text: str | None
    number: int | None
    hex: str

    @property
    def display_value(self) -> str:
        return self.text if self.text is not None else (
            str(self.number) if self.number is not None else self.hex)


@dataclass(frozen=True)
class PrivatePropertiesResult:
    properties: list[PrivateProperty]
    messages: list[str]
    section2_value: int | None
    section5_hash: int | None
    section5_interval_ms: int | None
    consumed_bytes: int
    errors: list[str] = field(default_factory=list)


def _read_u16_be(data: bytes, offset: int) -> int:
    return (data[offset] << 8) | data[offset + 1]


def _read_u32_be(data: bytes, offset: int) -> int:
    return int.from_bytes(data[offset: offset + 4], "big")


def _marker_at(data: bytes, offset: int) -> bool:
    return offset + 4 <= len(data) and data[offset: offset + 4] == _MARKER


def _decode(type_: int, value: bytes) -> PrivateProperty:
    name = name_of(type_)
    hex_text = value.hex()
    if type_ in (0x5C, 0x5D):  # ENCRYPT_KEY / ENCRYPT_IV
        decoded = _rc4_transform(value, RC4_KEY)
        return PrivateProperty(type_, name, decoded, None, None, decoded.hex())
    if type_ in _STRING_TYPES:
        try:
            text = value.decode("gb18030", errors="replace").rstrip("\x00")
        except Exception:
            text = value.decode("latin-1", errors="replace")
        return PrivateProperty(type_, name, value, text, None, hex_text)
    number = None
    if len(value) == 1:
        number = value[0]
    elif len(value) == 2:
        number = int.from_bytes(value, "big")
    elif len(value) == 4:
        number = int.from_bytes(value, "big")
    elif len(value) == 8:
        number = int.from_bytes(value, "big")
    return PrivateProperty(type_, name, value, None, number, hex_text)


def parse(payload: bytes) -> PrivatePropertiesResult:
    properties: list[PrivateProperty] = []
    messages: list[str] = []
    errors: list[str] = []
    data = bytes(payload)
    total = len(data)
    if total > MAXIMUM_PACKET_LENGTH:
        errors.append(f"payload length {total} exceeds {MAXIMUM_PACKET_LENGTH}")
        return PrivatePropertiesResult([], messages, None, None, None, 0, errors)

    pos = 0
    # Section 1: marker + 2B length + GBK message
    if total < 6 or not _marker_at(data, 0):
        errors.append("missing section 1 marker")
        return PrivatePropertiesResult([], messages, None, None, None, 0, errors)
    message_length = _read_u16_be(data, 4)
    if message_length + 6 > total:
        errors.append(f"section 1 length {message_length} overruns payload")
        return PrivatePropertiesResult([], messages, None, None, None, 0, errors)
    try:
        message = data[6: 6 + message_length].decode("gb18030", errors="replace")
    except Exception:
        message = ""
    if message.strip():
        messages.append(message)
    pos = 6 + message_length

    # Section 2: fixed 0x6F layout with count-prefixed 32-byte entries
    section2_value = None
    if pos + 0x6F <= total:
        if not _marker_at(data, pos):
            errors.append(f"missing section 2 marker at offset {pos}")
            return PrivatePropertiesResult([], messages, section2_value, None,
                                           None, pos, errors)
        pos += 6
        section2_value = _read_u32_be(data, pos)
        count = data[pos + 0x68]
        pos += 0x69
        if count * 0x20 + pos <= total:
            pos += count * 0x20
        else:
            errors.append(f"section 2 count {count} overruns payload")
            return PrivatePropertiesResult([], messages, section2_value, None,
                                           None, pos, errors)
    else:
        return PrivatePropertiesResult([], messages, section2_value, None,
                                       None, pos, errors)

    # Sections 3/4: marker + ver + sub; ver must be 1 and sub < 2
    for section in (3, 4):
        if pos + 6 > total:
            return PrivatePropertiesResult([], messages, section2_value, None,
                                           None, pos, errors)
        if not _marker_at(data, pos):
            errors.append(f"missing section {section} marker at offset {pos}")
            return PrivatePropertiesResult([], messages, section2_value, None,
                                           None, pos, errors)
        version = data[pos + 4]
        sub = data[pos + 5]
        if version == 1 and sub < 2:
            pos += 6
        else:
            errors.append(
                f"section {section} version {version}/sub {sub} rejected")
            return PrivatePropertiesResult([], messages, section2_value, None,
                                           None, pos, errors)

    # Section 5: 15 obfuscated bytes, de-obfuscated in place
    hash_value = None
    interval_ms = None
    if pos + 15 > total:
        return PrivatePropertiesResult([], messages, section2_value, hash_value,
                                       interval_ms, pos, errors)
    plain = obfuscate(data[pos: pos + 15])
    if not _marker_at(plain, 0):
        errors.append(f"section 5 de-obfuscated marker mismatch: {plain.hex()}")
        return PrivatePropertiesResult([], messages, section2_value, hash_value,
                                       interval_ms, pos, errors)
    if plain[4] != 0x0A:
        errors.append(f"section 5 gate byte {plain[4]:X2} != 0x0A")
        return PrivatePropertiesResult([], messages, section2_value, hash_value,
                                       interval_ms, pos, errors)
    hash_value = _read_u32_be(plain, 6)
    interval_ms = _read_u32_be(plain, 10) * 1000
    pos += 15

    # Property TLV loop: [1311][type][len=value+2][value]
    while pos + 6 <= total:
        if not _marker_at(data, pos):
            break
        type_ = data[pos + 4]
        declared = data[pos + 5]
        if declared < 2:
            errors.append(f"type {type_} length {declared} too small at offset {pos}")
            break
        value_length = declared - 2
        if pos + 6 + value_length > total:
            errors.append(f"type {type_} overruns payload at offset {pos}")
            break
        value = data[pos + 6: pos + 6 + value_length]
        pos += 6 + value_length
        properties.append(_decode(type_, value))

    if pos != total:
        errors.append(f"trailing {total - pos} byte(s)")
    return PrivatePropertiesResult(properties, messages, section2_value,
                                   hash_value, interval_ms, pos, errors)
