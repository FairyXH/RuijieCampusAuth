"""Scapy-based PEAP/EAP-TLS transport (mirrors Core.Protocols.PcapEapTransport)."""

from __future__ import annotations

import queue
import threading
import time
from typing import Callable

from scapy.all import Ether, get_if_hwaddr  # type: ignore

from .peap_session import IPeapTransport, TunnelMode

RUIJIE_GROUP = "01:d0:f8:00:00:03"
STANDARD_GROUP = "01:80:c2:00:00:03"
ETHERTYPE_EAPOL = 0x888E


def _build_frame(source: str, destination: str, eap: bytes,
                 tail: bytes | None = None) -> bytes:
    eap_length = (eap[2] << 8) | eap[3] if len(eap) >= 4 else len(eap)
    if eap_length <= 0 or eap_length > len(eap):
        eap_length = len(eap)
    frame = bytearray()
    frame += bytes.fromhex(destination.replace(":", ""))
    frame += bytes.fromhex(source.replace(":", ""))
    frame += ETHERTYPE_EAPOL.to_bytes(2, "big")
    frame += bytes([1, 0]) + eap_length.to_bytes(2, "big")
    frame += eap[:eap_length]
    frame += tail or b""
    return bytes(frame)


def _build_control(source: str, destination: str, eapol_type: int,
                   tail: bytes | None = None) -> bytes:
    frame = bytearray()
    frame += bytes.fromhex(destination.replace(":", ""))
    frame += bytes.fromhex(source.replace(":", ""))
    frame += ETHERTYPE_EAPOL.to_bytes(2, "big")
    frame += bytes([1, eapol_type, 0, 0])
    frame += tail or b""
    return bytes(frame)


def _format_mac(raw: bytes) -> str:
    return ":".join(f"{b:02x}" for b in raw[:6])


class PcapEapTransport(IPeapTransport):
    """Scapy L2 transport: Start, Identity, tunnel Exchange with identifier
    tracking. Call chain mirrors the C# version."""

    def __init__(self, interface: str, mode: TunnelMode, logoff_first: bool = True,
                 repeats: int = 3, timeout: float | None = None,
                 log: Callable[[str], None] | None = None) -> None:
        self.interface = interface
        self.mode = mode
        self.logoff_first = logoff_first
        self.repeats = max(1, repeats)
        self.timeout = timeout or 20.0
        self.log = log or (lambda message: None)
        self.source_mac = get_if_hwaddr(interface).lower()
        if not self.source_mac:
            raise OSError("interface has no MAC address")
        self.server_mac: str | None = None
        self.outer_success = False
        self.outer_failure: str | None = None
        self._last_identifier = 0
        self._received: queue.Queue[bytes] = queue.Queue()
        self._device = None
        self._stop = threading.Event()

    def _start_capture(self) -> None:
        from scapy.all import AsyncSniffer  # type: ignore

        self._device = AsyncSniffer(
            iface=self.interface,
            filter="ether proto 0x888e",
            prn=lambda packet: self._on_packet(packet),
            store=False)
        self._device.start()

    def _on_packet(self, packet: Ether) -> None:
        raw = bytes(packet)
        if len(raw) < 18:
            return
        if not self._stop.is_set():
            self._received.put(raw)

    def _next_eap(self, deadline: float) -> bytes | None:
        while time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            try:
                frame = self._received.get(timeout=max(0.001, remaining))
            except queue.Empty:
                return None
            if len(frame) < 18:
                continue
            if frame[15] != 0:  # EAPOL type must be EAP-Payload
                continue
            source = _format_mac(frame[6:12])
            if source == self.source_mac.lower():
                continue
            eap_length = (frame[20] << 8) | frame[21]
            if eap_length < 4 or 22 + (eap_length - 4) > len(frame):
                continue
            eap = frame[22:22 + eap_length]
            if not self.server_mac:
                self.server_mac = source
            self._last_identifier = eap[1] if len(eap) > 1 else 0
            return bytes(eap)
        return None

    def _send_eap(self, eap: bytes) -> None:
        if self._device is None or not self.server_mac:
            raise OSError("transport is not started")
        frame = _build_frame(self.source_mac, self.server_mac, eap)
        from scapy.sendrecv import sendp  # type: ignore
        from scapy.all import RadioTap, raw  # type: ignore  # noqa: F401
        # Send raw L2 frame via scapy using the existing conf iface
        from scapy.config import conf  # type: ignore
        packet = Ether(frame)
        for _ in range(self.repeats):
            from scapy.all import sendp as scapy_sendp  # type: ignore
            scapy_sendp(packet, iface=self.interface, verbose=False)

    def _send_control(self, eapol_type: int, tail: bytes | None) -> None:
        destination = STANDARD_GROUP if eapol_type == 2 else RUIJIE_GROUP
        frame = _build_control(self.source_mac, destination, eapol_type, tail)
        from scapy.all import Ether, sendp  # type: ignore
        sendp(Ether(frame), iface=self.interface, verbose=False, count=self.repeats)

    def start(self, identity: str, identity_tail: bytes | None) -> bytes:
        self._received = queue.Queue()
        self._stop.clear()
        self._start_capture()
        time.sleep(0.3)
        if self.logoff_first:
            self._send_control(2, identity_tail)
            time.sleep(0.1)
        self._send_control(1, identity_tail)

        deadline = time.monotonic() + self.timeout
        tunnel_type = self.mode.value
        while True:
            packet = self._next_eap(deadline)
            if packet is None:
                raise TimeoutError("no EAP identity request")
            if packet[0] == 3:
                self.outer_success = True
                raise RuntimeError("server sent EAP-Success before PEAP start")
            if packet[0] == 4:
                self.outer_failure = "EAP-Failure before PEAP start"
                raise RuntimeError(self.outer_failure)
            if packet[0] == 1 and len(packet) >= 5 and packet[4] == 1:
                self.log("收到 Identity 请求，提交用户身份")
                response = bytes([2, packet[1],
                                  (5 + len(identity)) >> 8,
                                  (5 + len(identity)) & 0xFF, 1]) + identity.encode("utf-8")
                self._send_eap(response + (identity_tail or b""))
                break

        while True:
            packet = self._next_eap(deadline)
            if packet is None:
                raise TimeoutError("no PEAP/EAP-TLS request")
            if packet[0] == 3:
                self.outer_success = True
                return packet
            if packet[0] == 4:
                self.outer_failure = "EAP-Failure"
                return packet
            if packet[0] == 1 and len(packet) >= 5 and packet[4] == tunnel_type:
                self.log(f"收到 {'PEAP' if tunnel_type == 25 else 'EAP-TLS'} 请求")
                return packet

    def exchange(self, eap_packet: bytearray, tail: bytes | None) -> bytes:
        eap_packet[1] = self._last_identifier
        self._send_eap(bytes(eap_packet) + (tail or b""))
        deadline = time.monotonic() + self.timeout
        while True:
            packet = self._next_eap(deadline)
            if packet is None:
                raise TimeoutError("no server EAP response")
            if packet[0] == 3:
                self.outer_success = True
                return packet
            if packet[0] == 4:
                self.outer_failure = "EAP-Failure"
                return packet
            return packet

    def dispose(self) -> None:
        self._stop.set()
        device = self._device
        self._device = None
        if device is not None:
            try:
                device.stop()
            except Exception:
                pass
