from __future__ import annotations

from scapy.all import EAPOL, Ether

from ruijie_auth.cli import RUIJIE_GROUP, STANDARD_GROUP, Supplicant
from ruijie_auth.private import NetworkInfo


def make_supplicant(**options) -> Supplicant:
    return Supplicant(
        interface="test0",
        identity=b"user",
        password=b"pass",
        timeout=0.1,
        retries=1,
        repeats=1,
        private=False,
        network=NetworkInfo(),
        source_mac="aa:bb:cc:dd:ee:ff",
        **options,
    )


def test_locked_start_is_unicast_and_logoff_stays_group() -> None:
    supp = make_supplicant(locked_server="00:1A:A9:17:FF:FF")
    assert supp.locked_server == "00:1a:a9:17:ff:ff"
    sent = []
    supp.send = lambda packet, count=1: sent.append(packet)  # type: ignore[method-assign]

    supp.send_start()
    assert sent[-1][Ether].dst == "00:1a:a9:17:ff:ff"
    assert sent[-1][EAPOL].type == 1

    supp.logoff()
    assert sent[-1][Ether].dst == STANDARD_GROUP
    assert sent[-1][EAPOL].type == 2


def test_unlocked_control_frames_use_group_macs() -> None:
    supp = make_supplicant()
    assert supp.locked_server is None
    sent = []
    supp.send = lambda packet, count=1: sent.append(packet)  # type: ignore[method-assign]

    supp.send_start()
    assert sent[-1][Ether].dst == RUIJIE_GROUP

    supp.logoff()
    assert sent[-1][Ether].dst == STANDARD_GROUP
