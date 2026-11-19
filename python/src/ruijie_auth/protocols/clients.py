"""Portal / self-service / client-center / SAM / upgrade clients.

Mirrors Core.Protocols.PortalClient, ClientCenterClient, SelfServiceClient,
UpgradeClient and SamDirectClient.
"""

from __future__ import annotations

import socket
import time
from dataclasses import dataclass
from urllib.parse import quote

from .http_json_client import get as _http_get
from .http_json_client import post_json as _http_post

LOGIN_PATH = "eportal/inferface/authAPI/login"
LOGOUT_PATH = "eportal/inferface/authAPI/logout"
ONLINE_PATH = "eportal/inferface/authAPI/getOnlineUserInfo"
PAGE_INFO_PATH = "eportal/inferface/authAPI/pageInfo"


@dataclass
class PortalRequestOptions:
    portal_url: str = ""
    user_id: str = ""
    password: str = ""
    service: str = ""
    query_string: str = ""
    user_ip: str = ""
    user_agent: str = "HttpClient"
    user_index: str = ""
    extra: dict | None = None


def build_url(portal_url: str, path: str) -> str:
    if not portal_url.strip():
        raise ValueError("portalUrl is empty")
    return portal_url.rstrip("/") + "/" + path.lstrip("/")


def _merge(body: dict, extra: dict | None) -> None:
    if extra:
        body.update(extra)


def login(options: PortalRequestOptions) -> dict:
    body = {
        "userId": options.user_id,
        "password": options.password,
        "service": options.service,
        "queryString": options.query_string,
        "userAgent": options.user_agent,
        "userip": options.user_ip,
    }
    _merge(body, options.extra)
    return _http_post(build_url(options.portal_url, LOGIN_PATH), body)


def logout(options: PortalRequestOptions) -> dict:
    body = {"userip": options.user_ip, "userIndex": options.user_index}
    _merge(body, options.extra)
    return _http_post(build_url(options.portal_url, LOGOUT_PATH), body)


def get_online_user_info(options: PortalRequestOptions) -> dict:
    body = {"userip": options.user_ip, "userIndex": options.user_index}
    _merge(body, options.extra)
    return _http_post(build_url(options.portal_url, ONLINE_PATH), body)


def page_info(options: PortalRequestOptions) -> dict:
    body = {"userip": options.user_ip, "userIndex": options.user_index}
    _merge(body, options.extra)
    return _http_post(build_url(options.portal_url, PAGE_INFO_PATH), body)


# ---------------------------------------------------------------- self-service


def self_service_get(url: str) -> dict:
    if not url.strip():
        raise ValueError("self-service url is empty")
    return _http_get(url)


# ------------------------------------------------------------- client center


@dataclass
class ClientCenterOptions:
    url: str = ""
    mac: str = ""
    ipv4: str = "0"
    product: str = ""
    main_version: str = ""
    sub_version: str = ""
    school_code: str = ""
    os_bits: str = "32"
    release_date: str = ""


def build_query(options: ClientCenterOptions) -> str:
    pairs = [
        ("mac", options.mac),
        ("ipv4", options.ipv4),
        ("ipv61", "0"),
        ("ipv62", "0"),
        ("ipv63", "0"),
        ("ipv64", "0"),
        ("product", options.product),
        ("mainver", options.main_version),
        ("subver", options.sub_version),
        ("schoolcode", options.school_code),
        ("osbits", options.os_bits),
        ("releasedate", options.release_date),
    ]
    return "&".join(
        f"{key}={quote(value or '')}" for key, value in pairs)


def build_center_url(options: ClientCenterOptions) -> str:
    if not options.url.strip():
        raise ValueError("client center url is empty")
    separator = "&" if "?" in options.url else "?"
    return options.url + separator + build_query(options)


def query(options: ClientCenterOptions) -> dict:
    return _http_get(build_center_url(options))


# -------------------------------------------------------------------- SAM


@dataclass(frozen=True)
class SamProbeResult:
    ok: bool
    host: str
    port: int
    request_hex: str
    response_hex: str
    error: str | None
    elapsed_ms: int


class DesMode:
    CBC = "cbc"
    ECB = "ecb"


def sam_probe(host: str, port: int, payload: bytes,
              timeout_ms: int) -> SamProbeResult:
    started = time.monotonic_ns() // 1_000_000
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.settimeout(max(0.1, timeout_ms) / 1000)
            client.sendto(payload, (host, port))
            response, _addr = client.recvfrom(4096)
        return SamProbeResult(True, host, port, payload.hex(),
                              response.hex(), None,
                              time.monotonic_ns() // 1_000_000 - started)
    except socket.timeout:
        return SamProbeResult(False, host, port, payload.hex(), "", "timeout",
                              time.monotonic_ns() // 1_000_000 - started)
    except Exception as exc:
        return SamProbeResult(False, host, port, payload.hex(), "", str(exc),
                              time.monotonic_ns() // 1_000_000 - started)


def des_transform(data: bytes, key: bytes, iv: bytes | None, mode: str,
                  encrypt: bool) -> bytes:
    try:
        from Crypto.Cipher import DES  # type: ignore
    except ImportError as exc:  # pragma: no cover
        raise RuntimeError(
            "pycryptodome is required for SAM DES (pip install pycryptodome)"
        ) from exc
    if len(key) != 8:
        raise ValueError("DES key must be 8 bytes")
    if mode == DesMode.CBC:
        if not iv or len(iv) != 8:
            raise ValueError("CBC IV must be 8 bytes")
        cipher = DES.new(key, DES.MODE_CBC, iv)
    else:
        cipher = DES.new(key, DES.MODE_ECB)
    transform = cipher.encrypt if encrypt else cipher.decrypt
    return transform(data)


# ----------------------------------------------------------------- upgrade


def upgrade_check(url: str) -> dict:
    if not url.strip():
        raise ValueError("upgrade url is empty")
    return _http_get(url)
