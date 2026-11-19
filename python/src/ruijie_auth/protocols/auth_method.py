"""Auth-mode and DHCP-mode resolution (mirrors Core.Protocols.AuthMethod)."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum

from .peap_session import TunnelMode  # noqa: F401  (re-exported convenience)


class AuthMethodKind(Enum):
    EAP_MD5 = "EAPMD5"
    PEAP_V0 = "PEAPV0"
    PEAP_V1 = "PEAPV1"
    EAP_TLS = "EAPTLS"


@dataclass(frozen=True)
class TunnelModeRef:
    """Placeholder re-export holder; real TunnelMode lives in peap_session."""


def parse(value: str | None) -> AuthMethodKind:
    """Parse auth_mode into a concrete method; unknown values fall back to MD5."""
    text = (value or "").strip().replace("-", "").replace("_", "")
    if not text:
        return AuthMethodKind.EAP_MD5
    upper = text.upper()
    return {
        "EAPMD5": AuthMethodKind.EAP_MD5,
        "MD5": AuthMethodKind.EAP_MD5,
        "PEAP": AuthMethodKind.PEAP_V0,
        "PEAPV0": AuthMethodKind.PEAP_V0,
        "PEAPMSCHAPV2": AuthMethodKind.PEAP_V0,
        "PEAPV1": AuthMethodKind.PEAP_V1,
        "EAPTLS": AuthMethodKind.EAP_TLS,
        "TLS": AuthMethodKind.EAP_TLS,
    }.get(upper, AuthMethodKind.EAP_MD5)


def display(kind: AuthMethodKind) -> str:
    return {
        AuthMethodKind.PEAP_V0: "PEAPv0 + MSCHAPv2",
        AuthMethodKind.PEAP_V1: "PEAPv1 + MSCHAPv2",
        AuthMethodKind.EAP_TLS: "EAP-TLS",
        AuthMethodKind.EAP_MD5: "EAP-MD5",
    }[kind]


def wants_dhcp_before(dhcp_mode: str | None) -> bool:
    return _normalize_dhcp(dhcp_mode) == "before"


def wants_dhcp_after(dhcp_mode: str | None) -> bool:
    return _normalize_dhcp(dhcp_mode) == "after"


def _normalize_dhcp(dhcp_mode: str | None) -> str:
    return (dhcp_mode or "").strip().lower()
