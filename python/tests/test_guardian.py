from __future__ import annotations

import pytest

from ruijie_auth.guardian import (
    DEFAULT_GUARDIAN_URL,
    GuardianClient,
    normalize_guardian_url,
    select_probe,
)


def make_snapshot() -> dict:
    return {
        "interfaces": [
            {
                "id": "luid:16",
                "name": "NG-TMP-0000",
                "description": "Realtek USB GbE Family Controller",
                "adapterGuid": "6bd35c5e-c108-4c32-90d2-33b1ac4b9273",
                "macAddress": "00:E0:4C:68:00:C6",
                "primaryIpv4Address": "172.18.28.6",
                "primaryGateway": "172.18.0.1",
                "isDefaultRoute": True,
                "probe": {
                    "isOnline": True,
                    "stableOnline": True,
                    "captivePortalSuspected": False,
                    "summary": "InternetVerified online 1/1",
                    "timestampUtc": "2026-11-18T01:12:18.0852342+00:00",
                },
            },
            {
                "id": "luid:14",
                "name": "WLAN-1",
                "description": "Intel Wi-Fi 6 AX201",
                "adapterGuid": "4ee4a295-db04-424f-8f46-e3b98f79cae2",
                "isDefaultRoute": False,
                "probe": {"isOnline": True, "stableOnline": True},
            },
        ],
        "expectedOutletInterfaceId": "luid:14",
        "globalProbe": {
            "isOnline": False,
            "sourceInterfaceId": "luid:16",
            "summary": "global",
        },
    }


def test_normalize_url_accepts_loopback() -> None:
    assert normalize_guardian_url(DEFAULT_GUARDIAN_URL) == DEFAULT_GUARDIAN_URL
    assert (
        normalize_guardian_url("http://localhost:18765/mcp")
        == "http://127.0.0.1:18765/mcp"
    )
    assert (
        normalize_guardian_url("http://127.0.0.1:18765/mcp/")
        == "http://127.0.0.1:18765/mcp/"
    )


@pytest.mark.parametrize(
    "url",
    [
        "https://127.0.0.1:18765/mcp",
        "http://192.168.1.2:18765/mcp",
        "http://127.0.0.1:18765/other",
        "http://127.0.0.1/mcp",
        "http://user:pass@127.0.0.1:18765/mcp",
        "http://127.0.0.1:18765/mcp?x=1",
    ],
)
def test_normalize_url_rejects_non_loopback_or_bad_path(url: str) -> None:
    with pytest.raises(ValueError):
        normalize_guardian_url(url)


def test_select_probe_matches_auth_interface_by_guid() -> None:
    probe = select_probe(
        make_snapshot(),
        r"\Device\NPF_{6BD35C5E-C108-4C32-90D2-33B1AC4B9273}",
    )
    assert probe.interface_name == "NG-TMP-0000"
    assert probe.online is True
    assert probe.stable_online is True
    assert probe.source == "认证网卡"
    assert probe.ip == "172.18.28.6"


def test_select_probe_falls_back_to_expected_outlet() -> None:
    probe = select_probe(make_snapshot())
    assert probe.interface_name == "WLAN-1"
    assert probe.source == "默认出口"


def test_paused_guardian_returns_unknown() -> None:
    snapshot = make_snapshot()
    snapshot["isPaused"] = True
    probe = select_probe(
        snapshot,
        r"\Device\NPF_{6BD35C5E-C108-4C32-90D2-33B1AC4B9273}",
    )
    assert probe.online is None
    assert "暂停" in probe.detail


def test_non_authoritative_probe_returns_unknown() -> None:
    snapshot = make_snapshot()
    snapshot["interfaces"][0]["probe"]["isAuthoritative"] = False
    probe = select_probe(
        snapshot,
        r"\Device\NPF_{6BD35C5E-C108-4C32-90D2-33B1AC4B9273}",
    )
    assert probe.online is None
    assert "权威" in probe.detail


def test_select_probe_uses_global_probe_for_matching_interface() -> None:
    snapshot = make_snapshot()
    snapshot["interfaces"][0]["probe"] = None
    probe = select_probe(
        snapshot,
        r"\Device\NPF_{6BD35C5E-C108-4C32-90D2-33B1AC4B9273}",
    )
    assert probe.online is False
    assert probe.source == "认证网卡"
    assert probe.summary == "global"


def test_client_status_reads_structured_content() -> None:
    client = GuardianClient()
    calls: list[str] = []

    def fake_rpc(method: str, params=None):
        calls.append(method)
        if method == "initialize":
            return {}
        return {"structuredContent": {"snapshot": make_snapshot()}}

    client._rpc = fake_rpc  # type: ignore[method-assign]
    data = client.status()
    assert calls == ["initialize", "tools/call"]
    assert data["snapshot"]["interfaces"][0]["name"] == "NG-TMP-0000"
