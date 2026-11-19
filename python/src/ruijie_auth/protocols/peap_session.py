"""PEAP/EAP-TLS tunneling (mirrors Core.Protocols.PeapSession + EapTlsStream).

The C# version drives SslStream over EapTlsStream with a pump thread; Python's
SSLObject over memory BIOs is a non-blocking state machine, so the pump
collapses into SSLWantRead/Write handling inside the same call chain:
run -> _feed_first_payload -> handshake -> _run_phase2 -> _wait_outer_success.
"""

from __future__ import annotations

import secrets
import ssl
import time
from dataclasses import dataclass, field
from enum import Enum

from .mschap_v2 import build_response, is_success, try_parse_challenge


class TunnelMode(Enum):
    PEAP = 25
    EAP_TLS = 13


FLAG_LENGTH = 0x80
FLAG_MORE = 0x40


@dataclass
class PeapOptions:
    mode: TunnelMode = TunnelMode.PEAP
    user_name: str = ""
    password: str = ""
    target_host: str = ""
    verify_server_certificate: bool = True
    client_certificate: tuple[str, str] | None = None  # (cert_file, key_file)
    max_fragment_bytes: int = 1200
    timeout: float = 30.0
    identity_tail: bytes | None = None


@dataclass
class PeapResult:
    ok: bool
    message: str
    tls_protocol: str | None = None
    server_subject: str | None = None
    steps: list[str] = field(default_factory=list)
    outer_failure: str | None = None


class IPeapTransport:
    """Mirror of the C# transport interface: Start/Exchange/OuterSuccess."""

    source_mac: str
    server_mac: str | None
    outer_success: bool
    outer_failure: str | None

    def start(self, identity: str, identity_tail: bytes | None) -> bytes:
        raise NotImplementedError

    def exchange(self, eap_packet: bytearray, tail: bytes | None) -> bytes:
        raise NotImplementedError

    def dispose(self) -> None:
        raise NotImplementedError


@dataclass
class _Session:
    transport: IPeapTransport
    options: PeapOptions
    deadline: float
    bio_in: ssl.MemoryBIO
    bio_out: ssl.MemoryBIO
    steps: list[str]


def run(transport: IPeapTransport, options: PeapOptions) -> PeapResult:
    steps: list[str] = []
    deadline = time.monotonic() + options.timeout
    tls_protocol: str | None = None
    server_subject: str | None = None
    sslobj: ssl.SSLObject | None = None

    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    if not options.verify_server_certificate:
        ctx.verify_mode = ssl.CERT_NONE
    if options.client_certificate:
        try:
            ctx.load_cert_chain(*options.client_certificate)
        except (ssl.SSLError, OSError) as exc:
            return PeapResult(False, f"client cert load failed: {exc}",
                              None, None, steps, None)

    try:
        first = transport.start(options.user_name, options.identity_tail)

        bio_in = ssl.MemoryBIO()
        bio_out = ssl.MemoryBIO()
        sslobj = ctx.wrap_bio(
            bio_in,
            bio_out,
            server_hostname=options.target_host or None,
            do_handshake_on_connect=False,
        )
        session = _Session(transport, options, deadline, bio_in, bio_out, steps)

        _feed_first_payload(session, first)
        _drive_handshake(session)
        tls_protocol = sslobj.version() or "?"
        cert = sslobj.getpeercert()
        server_subject = str(cert.get("subject", "-")) if cert else "-"
        steps.append(f"tls={tls_protocol} server-cert={server_subject}")

        if options.mode == TunnelMode.EAP_TLS:
            _wait_outer_success(transport, deadline)
            return PeapResult(
                transport.outer_success,
                "EAP-TLS 认证成功" if transport.outer_success
                else "EAP-TLS 未收到外层成功",
                tls_protocol, server_subject, steps, transport.outer_failure)

        if not _run_phase2(session):
            return PeapResult(False, "PEAP phase2 失败", tls_protocol,
                              server_subject, steps, transport.outer_failure)
        _wait_outer_success(transport, deadline)
        return PeapResult(
            transport.outer_success,
            "PEAP/MSCHAPv2 认证成功" if transport.outer_success
            else "PEAP 未收到外层成功",
            tls_protocol, server_subject, steps, transport.outer_failure)
    except Exception as exc:
        return PeapResult(False, str(exc), tls_protocol, server_subject, steps,
                          transport.outer_failure)


def _feed_first_payload(session: _Session, first: bytes) -> None:
    payload = _receive_server_payload(session.transport, first, session.options)
    if payload:
        session.steps.append(f"server-tls-fragment={len(payload)}")
        session.bio_in.write(payload)
    else:
        session.steps.append("server-start")


def _drive_handshake(session: _Session) -> None:
    sslobj = session.sslobj
    while True:
        try:
            sslobj.do_handshake()
            return
        except ssl.SSLWantReadError:
            _ship_outgoing(session)
            payload = _receive_server_payload(
                session.transport, _next_server_packet(session), session.options)
            if payload:
                session.bio_in.write(payload)
        except ssl.SSLWantWriteError:
            _ship_outgoing(session)


def _next_server_packet(session: _Session) -> bytes | None:
    """Fetch the next server tunnel packet (exchange an empty ACK)."""
    return session.transport.exchange(
        _build_response(session.options.mode, b"\x00"), session.options.identity_tail)


def _ship_outgoing(session: _Session) -> None:
    data = session.bio_out.read(16384)
    if data:
        _send_client_payload(session.transport, data, session.options)


def _send_client_payload(transport: IPeapTransport, tls_data: bytes,
                         options: PeapOptions) -> bytes | None:
    offset = 0
    first = True
    max_fragment = max(200, options.max_fragment_bytes)
    last_server: bytes | None = None
    while offset < len(tls_data):
        length = min(max_fragment, len(tls_data) - offset)
        last = offset + length >= len(tls_data)
        flags = 0
        if first:
            flags |= FLAG_LENGTH
        if not last:
            flags |= FLAG_MORE
        header = 4 if first else 0
        payload = bytearray(1 + header + length)
        payload[0] = flags
        if first:
            total = len(tls_data)
            payload[1] = (total >> 24) & 0xFF
            payload[2] = (total >> 16) & 0xFF
            payload[3] = (total >> 8) & 0xFF
            payload[4] = total & 0xFF
        payload[1 + header: 1 + header + length] = tls_data[offset: offset + length]
        eap = _build_response(options.mode, bytes(payload))
        last_server = transport.exchange(eap, options.identity_tail)
        offset += length
        first = False
        if transport.outer_success or transport.outer_failure is not None:
            return last_server
    return last_server


def _receive_server_payload(transport: IPeapTransport, first_packet: bytes | None,
                            options: PeapOptions) -> bytes | None:
    if first_packet is None:
        return None
    assembled = bytearray()
    packet = first_packet
    while True:
        ok, flags, data = _try_parse_tunnel(packet, options.mode)
        if not ok:
            return None
        assembled.extend(data)
        if not (flags & FLAG_MORE):
            return bytes(assembled)
        packet = transport.exchange(_build_response(options.mode, b"\x00"),
                                    options.identity_tail)
        if transport.outer_success or transport.outer_failure is not None:
            return None


def _run_phase2(session: _Session) -> bool:
    sslobj = session.sslobj
    while time.monotonic() < session.deadline:
        packet = _read_eap_packet(session)
        if len(packet) < 4:
            return False
        if packet[0] == 3:
            session.steps.append("phase2-eap-success")
            return True
        if packet[0] == 4:
            session.steps.append("phase2-eap-failure")
            return False
        if packet[0] != 1 or len(packet) < 5:
            session.steps.append(f"phase2-unexpected-code={packet[0]}")
            return False
        eap_type = packet[4]
        if eap_type == 1:
            identity = session.options.user_name.encode("utf-8")
            sslobj.write(_identity_response(packet[1], identity))
            session.steps.append("phase2-identity")
            _ship_outgoing(session)
            continue
        if eap_type == 26:
            challenge, error = try_parse_challenge(packet)
            if challenge is None:
                if is_success(packet):
                    session.steps.append("phase2-mschap-success")
                    continue
                session.steps.append(f"phase2-mschap-error={error}")
                return False
            peer_challenge = secrets.token_bytes(16)
            sslobj.write(build_response(challenge, session.options.user_name,
                                        session.options.password, peer_challenge))
            session.steps.append("phase2-mschap-response")
            _ship_outgoing(session)
            continue
        session.steps.append(f"phase2-unsupported-eap-type={eap_type}")
        return False
    return False


def _identity_response(identifier: int, identity: bytes) -> bytes:
    length = 5 + len(identity)
    return bytes([2, identifier, (length >> 8) & 0xFF, length & 0xFF, 1]) + identity


def _read_eap_packet(session: _Session) -> bytes:
    header = _read_exact(session, 4)
    length = (header[2] << 8) | header[3]
    if length < 4:
        return header
    return header + _read_exact(session, length - 4)


def _read_exact(session: _Session, count: int) -> bytes:
    data = bytearray()
    while len(data) < count:
        remaining = session.deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("phase2 read timeout")
        try:
            chunk = session.sslobj.read(count - len(data))
        except ssl.SSLWantReadError:
            _ship_outgoing(session)
            packet = _next_server_packet(session)
            ok, _flags, payload = _try_parse_tunnel(packet or b"", session.options.mode)
            if ok and payload:
                session.bio_in.write(payload)
            continue
        if not chunk:
            raise EOFError("TLS stream closed")
        data.extend(chunk)
    return bytes(data)


def _wait_outer_success(transport: IPeapTransport, deadline: float) -> None:
    while time.monotonic() < deadline and not transport.outer_success \
            and transport.outer_failure is None:
        time.sleep(0.05)


def _build_response(mode: TunnelMode, payload: bytes) -> bytes:
    eap_type = mode.value
    length = 5 + len(payload)
    eap = bytearray([2, 0, (length >> 8) & 0xFF, length & 0xFF, eap_type])
    eap.extend(payload)
    return bytes(eap)


def _try_parse_tunnel(packet: bytes, mode: TunnelMode) -> tuple[bool, int, bytes]:
    eap_type = mode.value
    if len(packet) < 6 or packet[0] != 1 or packet[4] != eap_type:
        return False, 0, b""
    flags = packet[5]
    offset = 6
    if flags & FLAG_LENGTH:
        offset += 4
    if offset > len(packet):
        return False, flags, b""
    return True, flags, packet[offset:]


def is_tunnel_request(packet: bytes, mode: TunnelMode) -> bool:
    eap_type = mode.value
    return len(packet) >= 5 and packet[0] == 1 and packet[4] == eap_type
