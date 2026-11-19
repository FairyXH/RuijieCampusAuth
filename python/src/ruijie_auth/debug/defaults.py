"""DebugArgumentDefaults mirror: prefill debug args from school settings."""

from __future__ import annotations

import json


def build(feature_name: str | None, settings, account) -> str:
    """JSON string pre-filled from settings/account; keys match the
    server-side DebugArgs lookups exactly."""
    node = _build_node(feature_name, settings, account)
    if node is None:
        return "{}"
    return json.dumps(node, indent=2, ensure_ascii=False)


def _put(node: dict, key: str, value) -> None:
    if value is not None and str(value).strip():
        node[key] = value


def _build_node(feature_name: str | None, settings, account) -> dict | None:
    name = (feature_name or "").lower()
    node: dict = {}
    if name == "portal-auth":
        node["action"] = "login"
        _put(node, "portalUrl", settings.portal_url)
        _put(node, "userId", account.username if account else None)
        _put(node, "password", account.password if account else None)
        _put(node, "service", settings.service_name)
    elif name == "portal-udp":
        node["interval"] = 5
        node["duration"] = 30
    elif name in ("user-messages", "server-list-update"):
        _put(node, "hex")
    elif name == "client-center":
        _put(node, "ipv4")
        node["product"] = "33554432"
        node["mainver"] = "100663296"
        node["subver"] = "1442840576"
        _put(node, "schoolcode", settings.school_code)
        node["osbits"] = "64"
    elif name == "self-service":
        _put(node, "url", settings.self_service_url)
    elif name == "upgrade-check":
        _put(node, "url", settings.update_url)
    elif name == "sam-direct":
        _put(node, "host", settings.sam_host)
        node["port"] = settings.sam_port if settings.sam_port > 0 else 53
        node["timeout_ms"] = 2000
    elif name in ("peap", "eap-tls"):
        _put(node, "interface", settings.interface)
        _put(node, "username", account.username if account else None)
        _put(node, "password", account.password if account else None)
        node["verify_cert"] = False
        node["max_fragment"] = 1200
        node["timeout"] = settings.timeout
    elif name == "dhcp-client":
        _put(node, "interface", settings.interface)
        node["operation"] = "renew"
        node["timeout"] = settings.dhcp_timeout or 30
        node["option82"] = settings.dhcp_option82
    elif name == "network-probe":
        _put(node, "interface", settings.interface)
        _put(node, "url", settings.connectivity_url)
    elif name == "wireless-profile":
        _put(node, "interface", settings.interface)
        _put(node, "ssid")
        _put(node, "identity", account.username if account else None)
        _put(node, "password", account.password if account else None)
        node["authentication"] = "WPA2Enterprise"
        node["encryption"] = "AES"
    elif name == "oui-detect":
        _put(node, "mac")
    elif name in ("arp-monitor", "nic-monitor", "proxy-detect", "dialup-detect",
                  "doctor-pipe", "system-info"):
        _put(node, "interface", settings.interface)
    else:
        return None
    return node
