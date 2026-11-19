"""Protocol-vector tests: all values locked to the original 8021x.exe
outputs or live captures (see docs/DIFFERENTIAL_TESTING.md)."""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ruijie_auth.private import crc16_xmodem, obfuscate, encrypt_credentials
from ruijie_auth.protocols.rc4 import transform as rc4_transform
from ruijie_auth.protocols.private_properties import parse as parse_props
from ruijie_auth.protocols.mschap_v2 import generate_nt_response, md4_hash
from ruijie_auth.protocols import auth_method
from ruijie_auth.debug.defaults import build as build_defaults
from ruijie_auth.storage import AppSettings, Account


# ---------------------------------------------------------------- core vectors


@pytest.mark.parametrize("data,expected", [
    (b"", 0x0000),
    (b"123456789", 0x31C3),
])
def test_crc16_matches_original(data, expected):
    assert crc16_xmodem(data) == expected


def test_crc16_32_byte_vector():
    assert crc16_xmodem(bytes(range(32))) == 0xD2FF


def test_obfuscate_matches_original():
    data = bytes(range(23))
    assert obfuscate(data).hex() == (
        "ff7fbf3fdf5f9f1fef6faf2fcf4f8f0ff777b737d75797")


def test_credential_cipher_one_block():
    cipher = encrypt_credentials(b"user", b"A" * 16, bytes(range(16)))
    assert cipher.hex() == "4062203e75a3ef4031f53933b24a0189"


def test_credential_cipher_chained():
    cipher = encrypt_credentials(b"student01", bytes(range(32)), b"\x11" * 16)
    assert cipher.hex() == (
        "3e13a4539d3d0be514550ddd9dd3263d"
        "05e75c8299442039ad594b768b49c01d")


def test_rc4_standard_vector():
    assert rc4_transform(b"Plaintext", b"Key").hex() == "bbf316e8d940af0ad3"


# ------------------------------------------------------- private properties


REAL_CAPTURE_HEX = (
    "000013110018bbb6d3adcab9d3c3cfc3c3c5bbaacfc3d1a7d4bacdf8c2e7000013116900000000000000"
    "000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
    "000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
    "000000000000000000000000000000000013110100000013110101ffff3777afff6765cda1ffffffd7ff"
    "00001311560600000000000013115a0600000035000013115b06ac160b01000013115c0a6e9bafe984d4"
    "1102000013115d0a538bcffb93b57817000013115e0a000000006afef017000013116e060000008a0000"
    "13117903020000131180060000000000001311980601000100"
)


def test_parse_real_captured_block_matches_original_binary():
    result = parse_props(bytes.fromhex(REAL_CAPTURE_HEX))
    assert result.errors == []
    assert result.messages == ["欢迎使用厦门华厦学院网络"]
    assert result.section2_value == 0
    assert result.section5_hash == 0x19594C7A
    assert result.section5_interval_ms == 20000
    names = [p.name for p in result.properties]
    assert names == [
        "SU_REAUTH_INTERV", "INDICATE_PORT", "INDICATE_SERV_IP",
        "ENCRYPT_KEY", "ENCRYPT_IV", "SERVER_UTC_TIME", "MSG_CLIENT_PORT",
        "DIRECT_COMM_HIGHEST_VERSION", "DIRECT_COMM_HEARTBEAT_FLAGS",
        "WIFI_CNNT_NUM_FLAGS",
    ]
    values = {p.name: p for p in result.properties}
    assert values["INDICATE_PORT"].number == 53
    assert values["INDICATE_SERV_IP"].hex == "ac160b01"
    assert values["ENCRYPT_KEY"].hex == "7c73533126403871"
    assert values["ENCRYPT_IV"].hex == "4163332331215164"
    assert values["SERVER_UTC_TIME"].raw == bytes.fromhex("000000006afef017")
    assert values["MSG_CLIENT_PORT"].number == 138
    assert values["DIRECT_COMM_HIGHEST_VERSION"].number == 2
    assert result.consumed_bytes == len(bytes.fromhex(REAL_CAPTURE_HEX))


# ------------------------------------------------------------------- mschap


def test_md4_known_vector():
    assert md4_hash(b"").hex() == "31d6cfe0d16ae931b73c59d7e0c089c0"


def test_nt_response_rfc2759_vector():
    try:
        import Crypto  # noqa: F401
    except ImportError:
        pytest.skip("pycryptodome not installed")
    authenticator = bytes.fromhex("5B5D7C7D7B3F2F3E3C2C602132262628")
    peer = bytes.fromhex("21402324255E262A28295F2B3A337C7E")
    challenge = generate_nt_response.__wrapped__ if False else None
    from ruijie_auth.protocols.mschap_v2 import challenge_hash
    assert challenge_hash(peer, authenticator, "User").hex() == "d02e4386bce91226"
    response = generate_nt_response(authenticator, peer, "User", "clientPass")
    assert response.hex() == (
        "82309ecd8d708b5ea08faa3981cd83544233114a3d85d6df")


# --------------------------------------------------------------- auth mode


@pytest.mark.parametrize("value,expected", [
    (None, "EAPMD5"), ("", "EAPMD5"), ("EAPMD5", "EAPMD5"),
    ("peap", "PEAPV0"), ("PEAPV1", "PEAPV1"), ("tls", "EAPTLS"),
    ("bogus", "EAPMD5"),
])
def test_auth_mode_parse(value, expected):
    kind = auth_method.parse(value)
    assert kind.value == expected


def test_dhcp_mode_gates():
    assert auth_method.wants_dhcp_before("before")
    assert auth_method.wants_dhcp_after("after")
    assert not auth_method.wants_dhcp_before("none")
    assert not auth_method.wants_dhcp_after(None)


# ------------------------------------------------------------------- rc4 key


def test_encrypt_key_iv_matches_sam_pair():
    # ENCRYPT_KEY/IV are RC4("com.ruijie.www") over the wire values
    from ruijie_auth.protocols.rc4 import transform
    key = transform(bytes.fromhex("6e9bafe984d41102"), b"com.ruijie.www")
    iv = transform(bytes.fromhex("538bcffb93b57817"), b"com.ruijie.www")
    assert key.hex() == "7c73533126403871"
    assert iv.hex() == "4163332331215164"


# ------------------------------------------------------------------ profiles


def test_profile_import_switches_auth_mode_and_propagates(tmp_path):
    from ruijie_auth.school_profile import import_profile, load_profile

    from ruijie_auth.storage import AccountStore
    store = AccountStore(tmp_path)
    profile_path = tmp_path / "peap-school.json"
    profile_path.write_text(json.dumps({
        "name": "PEAP大学",
        "auth_mode": "PEAP",
        "service_name": "校园网服务",
        "static_id": "Static:PEAP01",
        "dhcp_mode": "before",
        "dhcp_timeout": 15,
    }), encoding="utf-8")

    profile = load_profile(profile_path)
    result = import_profile(profile, store, str(profile_path))

    settings = store.load_settings()
    assert settings.auth_mode == "PEAP"
    assert settings.dhcp_mode == "before"
    assert settings.service_name == "校园网服务"
    assert "auth_mode" in result.applied
    assert auth_method.parse(settings.auth_mode).value == "PEAPV0"
    assert auth_method.wants_dhcp_before(settings.dhcp_mode)


# -------------------------------------------------------------- debug prefill


def test_debug_defaults_prefill_school_config():
    settings = AppSettings()
    settings.self_service_url = "http://netss.hxxy.edu.cn/selfservice/"
    settings.school_code = "HPVTC"
    settings.sam_host = "172.22.11.1"
    settings.sam_port = 53
    account = Account(username="S12016004", password="pw")

    args = build_defaults("self-service", settings, account)
    parsed = json.loads(args)
    assert parsed["url"] == "http://netss.hxxy.edu.cn/selfservice/"

    args = build_defaults("sam-direct", settings, account)
    parsed = json.loads(args)
    assert parsed["host"] == "172.22.11.1"
    assert parsed["port"] == 53

    args = build_defaults("portal-auth", settings, account)
    parsed = json.loads(args)
    assert parsed["userId"] == "S12016004"
    assert parsed["service"] == settings.service_name
