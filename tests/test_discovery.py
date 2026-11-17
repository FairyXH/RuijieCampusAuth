from __future__ import annotations

from ruijie_auth.storage import DiscoveryCache


def test_discovery_cache_roundtrip(tmp_path) -> None:
    cache = DiscoveryCache(tmp_path / "discovery.json")
    assert cache.load() is None

    cache.save(
        r"\Device\NPF_TEST",
        description="Realtek USB GbE",
        mac="00:11:22:33:44:55",
        server_mac="00:1a:a9:17:ff:ff",
    )
    info = cache.load()
    assert info is not None
    assert info.interface == r"\Device\NPF_TEST"
    assert info.description == "Realtek USB GbE"
    assert info.mac == "00:11:22:33:44:55"
    assert info.server_mac == "00:1a:a9:17:ff:ff"
    assert info.updated_at

    cache.clear()
    assert cache.load() is None
