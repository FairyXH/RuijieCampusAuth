from __future__ import annotations

from ruijie_auth.cli import Supplicant
from ruijie_auth.private import NetworkInfo


def make_supplicant(**options) -> Supplicant:
    values = {
        "interface": "test0",
        "identity": b"user",
        "password": b"pass",
        "timeout": 0.1,
        "retries": 1,
        "repeats": 1,
        "private": True,
        "network": NetworkInfo(),
        "source_mac": "aa:bb:cc:dd:ee:ff",
    }
    values.update(options)
    return Supplicant(**values)


def test_custom_service_name_and_static_id_reach_tail() -> None:
    supp = make_supplicant(
        service_name="测试服务",
        static_id="Static:DEADBEEF",
    )
    assert supp.network.static_id == b"Static:DEADBEEF"
    service_bytes = "测试服务".encode("gb18030")

    tail = supp._tail(bytes(16), nonce=b"A" * 32)
    assert service_bytes in tail
    assert b"Static:DEADBEEF" in tail

    control = supp._control_tail()
    assert service_bytes in control
    assert b"Static:DEADBEEF" in control


def test_start_timeout_is_configurable() -> None:
    supp = make_supplicant(start_timeout=4.5)
    assert supp.start_timeout == 4.5
