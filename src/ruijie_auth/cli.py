#!/usr/bin/env python3
"""Cross-platform Ruijie-compatible EAP-MD5 supplicant."""

from __future__ import annotations

import argparse
import ctypes
import getpass
import hashlib
import logging
import os
import queue
import struct
import sys
import threading
import time
from dataclasses import dataclass, replace
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Iterable

try:
    from scapy.all import AsyncSniffer, EAP, EAPOL, Ether, conf, get_if_hwaddr, sendp
    from scapy.interfaces import resolve_iface
except ImportError as exc:  # pragma: no cover - dependency error path
    raise SystemExit("缺少依赖 Scapy，请执行: python -m pip install scapy") from exc

from .private import (
    NetworkInfo,
    TailOptions,
    build_tail,
    collect_network_info,
    normalize_mac,
)


EAP_REQUEST = 1
EAP_RESPONSE = 2
EAP_SUCCESS = 3
EAP_FAILURE = 4
EAP_IDENTITY = 1
EAP_MD5 = 4
RUIJIE_GROUP = "01:d0:f8:00:00:03"
STANDARD_GROUP = "01:80:c2:00:00:03"
RUIJIE_VENDOR_MARKER = b"\x00\x00\x13\x11"

LOG = logging.getLogger("ruijie-auth")


@dataclass(frozen=True)
class InterfaceInfo:
    key: str
    name: str
    description: str
    mac: str
    ip: str


def setup_logging(path: Path, verbose: bool) -> None:
    LOG.setLevel(logging.DEBUG)
    if any(getattr(handler, "_ruijie_managed", False) for handler in LOG.handlers):
        return
    formatter = logging.Formatter(
        "%(asctime)s [%(levelname)s] %(message)s", "%Y-%m-%d %H:%M:%S"
    )
    console = logging.StreamHandler()
    console.setLevel(logging.DEBUG if verbose else logging.INFO)
    console.setFormatter(formatter)
    console._ruijie_managed = True  # type: ignore[attr-defined]
    LOG.addHandler(console)

    path.parent.mkdir(parents=True, exist_ok=True)
    file_handler = RotatingFileHandler(
        path, maxBytes=2 * 1024 * 1024, backupCount=3, encoding="utf-8"
    )
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(formatter)
    file_handler._ruijie_managed = True  # type: ignore[attr-defined]
    LOG.addHandler(file_handler)


def list_interfaces() -> list[InterfaceInfo]:
    result: list[InterfaceInfo] = []
    for iface in conf.ifaces.values():
        key = str(getattr(iface, "network_name", "") or iface.name)
        mac = str(getattr(iface, "mac", "") or "")
        if not mac or mac == "00:00:00:00:00:00":
            continue
        result.append(
            InterfaceInfo(
                key=key,
                name=str(iface.name),
                description=str(getattr(iface, "description", "") or iface.name),
                mac=mac,
                ip=str(getattr(iface, "ip", "") or ""),
            )
        )
    return result


def probe_authenticator(
    interface: InterfaceInfo,
    timeout: float = 1.2,
    stop_event: threading.Event | None = None,
) -> bool:
    packets: queue.Queue[Ether] = queue.Queue()
    sniffer = AsyncSniffer(
        iface=interface.key,
        filter="ether proto 0x888e",
        prn=packets.put,
        store=False,
    )
    try:
        sniffer.start()
        if stop_event is not None:
            if stop_event.wait(0.15):
                return False
        else:
            time.sleep(0.15)
        frame = Ether(src=interface.mac, dst=RUIJIE_GROUP) / EAPOL(
            version=1, type=1, len=0
        )
        sendp(frame, iface=interface.key, verbose=False)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if stop_event is not None and stop_event.is_set():
                return False
            try:
                packet = packets.get(timeout=max(0.05, deadline - time.monotonic()))
            except queue.Empty:
                break
            if packet[Ether].src.lower() == interface.mac.lower():
                continue
            parsed = parse_eap(packet)
            if parsed is None:
                continue
            code, _, eap = parsed
            if code == EAP_REQUEST and len(eap) >= 5 and eap[4] == EAP_IDENTITY:
                for destination in (RUIJIE_GROUP, STANDARD_GROUP):
                    logoff = Ether(src=interface.mac, dst=destination) / EAPOL(
                        version=1, type=2, len=0
                    )
                    sendp(logoff, iface=interface.key, verbose=False)
                if stop_event is not None:
                    if stop_event.wait(0.5):
                        return False
                else:
                    time.sleep(0.5)
                return True
        return False
    except (OSError, ValueError):
        return False
    finally:
        if sniffer.running:
            sniffer.stop()


def choose_interface(
    requested: str | None,
    stop_event: threading.Event | None = None,
) -> str:
    if stop_event is not None and stop_event.is_set():
        raise SystemExit("已取消")
    interfaces = list_interfaces()
    if requested:
        try:
            return str(resolve_iface(requested).network_name)
        except ValueError:
            folded = requested.casefold()
            matches = [
                item
                for item in interfaces
                if folded in item.name.casefold()
                or folded in item.description.casefold()
                or folded in item.key.casefold()
            ]
            if len(matches) == 1:
                return matches[0].key
            raise SystemExit(f"找不到唯一匹配的网卡: {requested}")

    if not interfaces:
        raise SystemExit("未找到可用的有线或无线网卡")

    excluded = (
        "virtual",
        "vmware",
        "hyper-v",
        "loopback",
        "bluetooth",
        "wi-fi direct",
        "wifi direct",
        "vpn",
        "tap",
    )
    candidates = [
        item
        for item in interfaces
        if not any(
            word in f"{item.name} {item.description}".casefold() for word in excluded
        )
    ]
    candidates.sort(
        key=lambda item: (
            not any(
                word in item.description.casefold()
                for word in ("realtek", "gbe", "ethernet")
            ),
            not bool(item.ip and item.ip != "0.0.0.0"),
        )
    )
    LOG.info("自动探测连接锐捷认证服务器的网卡...")
    for candidate in candidates:
        if stop_event is not None and stop_event.is_set():
            raise SystemExit("已取消")
        LOG.debug(
            "探测网卡: %s | %s | %s",
            candidate.description,
            candidate.ip or "-",
            candidate.mac,
        )
        if probe_authenticator(candidate, stop_event=stop_event):
            LOG.info(
                "自动选择认证网卡: %s | %s | %s",
                candidate.description,
                candidate.ip or "-",
                candidate.mac,
            )
            return candidate.key

    if stop_event is not None and stop_event.is_set():
        raise SystemExit("已取消")
    routed = str(conf.route.route("0.0.0.0")[0])
    try:
        selected = resolve_iface(routed)
        selected_mac = str(getattr(selected, "mac", "") or "")
        selected_ip = str(getattr(selected, "ip", "") or "")
        if selected_mac and selected_mac != "00:00:00:00:00:00" and selected_ip:
            LOG.warning(
                "未探测到认证服务器，回退到默认路由网卡: %s | %s | %s",
                getattr(selected, "description", selected.name),
                selected_ip,
                selected_mac,
            )
            return str(selected.network_name)
    except ValueError:
        pass

    def score(item: InterfaceInfo) -> tuple[int, int]:
        text = f"{item.name} {item.description}".casefold()
        virtual = any(
            word in text
            for word in ("virtual", "vmware", "hyper-v", "loopback", "vpn", "tap")
        )
        usable_ip = bool(item.ip and item.ip != "0.0.0.0" and not item.ip.startswith("169.254."))
        return (50 if usable_ip else 0) + (0 if virtual else 10), len(item.ip)

    fallback = max(interfaces, key=score)
    LOG.warning(
        "默认路由网卡不可用，自动选择候选网卡: %s | %s | %s",
        fallback.description,
        fallback.ip or "-",
        fallback.mac,
    )
    return fallback.key


def print_interfaces() -> None:
    print("可用网卡：")
    for index, item in enumerate(list_interfaces(), start=1):
        print(
            f"  {index}. {item.description} | {item.ip or '-'} | "
            f"{item.mac} | {item.name}"
        )


def build_eapol_frame(source: str, destination: str, eap: bytes) -> Ether:
    # The Ruijie tail sits outside the EAPOL length field: the EAPOL header
    # carries only the standard EAP packet length, exactly like the client.
    eapol_len = int.from_bytes(eap[2:4], "big") if len(eap) >= 4 else len(eap)
    if not 0 < eapol_len <= len(eap):
        eapol_len = len(eap)
    return Ether(src=source, dst=destination) / EAPOL(
        version=1, type=0, len=eapol_len
    ) / eap


class _PcapTimeval(ctypes.Structure):
    _fields_ = [("tv_sec", ctypes.c_long), ("tv_usec", ctypes.c_long)]


class _PcapPkthdr(ctypes.Structure):
    _fields_ = [
        ("ts", _PcapTimeval),
        ("caplen", ctypes.c_uint32),
        ("len", ctypes.c_uint32),
    ]


def _send_queue(interface: str, frames: list[Ether]) -> None:
    """Send frames back-to-back through Npcap's send queue."""
    lib = ctypes.WinDLL("wpcap.dll")
    lib.pcap_open_live.argtypes = [
        ctypes.c_char_p,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_char_p,
    ]
    lib.pcap_open_live.restype = ctypes.c_void_p
    lib.pcap_sendqueue_alloc.argtypes = [ctypes.c_uint]
    lib.pcap_sendqueue_alloc.restype = ctypes.c_void_p
    lib.pcap_sendqueue_queue.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(_PcapPkthdr),
        ctypes.c_char_p,
    ]
    lib.pcap_sendqueue_queue.restype = ctypes.c_int
    lib.pcap_sendqueue_transmit.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_int,
    ]
    lib.pcap_sendqueue_transmit.restype = ctypes.c_int
    lib.pcap_sendqueue_destroy.argtypes = [ctypes.c_void_p]
    lib.pcap_close.argtypes = [ctypes.c_void_p]

    payloads = [bytes(frame) for frame in frames]
    errbuf = ctypes.create_string_buffer(256)
    handle = lib.pcap_open_live(interface.encode(), 65536, 0, 100, errbuf)
    if not handle:
        raise OSError(errbuf.value.decode(errors="replace"))
    queue = lib.pcap_sendqueue_alloc(sum(len(item) for item in payloads) + 64)
    if not queue:
        lib.pcap_close(handle)
        raise OSError("pcap_sendqueue_alloc failed")
    try:
        for payload in payloads:
            header = _PcapPkthdr()
            header.caplen = len(payload)
            header.len = len(payload)
            if lib.pcap_sendqueue_queue(queue, ctypes.byref(header), payload) != 0:
                raise OSError("pcap_sendqueue_queue failed")
        if lib.pcap_sendqueue_transmit(handle, queue, 0) < 0:
            raise OSError("pcap_sendqueue_transmit failed")
    finally:
        lib.pcap_sendqueue_destroy(queue)
        lib.pcap_close(handle)


def send_burst(interface: str, frames: list[Ether]) -> None:
    if not frames:
        return
    if sys.platform == "win32":
        try:
            _send_queue(interface, frames)
            return
        except Exception as exc:  # noqa: BLE001 - fall back to Scapy
            LOG.debug("Npcap 发送队列失败，回退 Scapy: %s", exc)
    sendp(frames, iface=interface, inter=0, verbose=False)


def eap_identity_response(identifier: int, identity: bytes) -> bytes:
    length = 5 + len(identity)
    return struct.pack("!BBHB", EAP_RESPONSE, identifier, length, EAP_IDENTITY) + identity


def eap_md5_response(
    identifier: int, password: bytes, challenge: bytes, identity: bytes
) -> bytes:
    digest = hashlib.md5(bytes([identifier]) + password + challenge).digest()
    length = 6 + len(digest) + len(identity)
    return (
        struct.pack("!BBHBB", EAP_RESPONSE, identifier, length, EAP_MD5, 16)
        + digest
        + identity
    )


def parse_eap(packet: Ether) -> tuple[int, int, bytes] | None:
    if EAPOL not in packet or packet[EAPOL].type != 0:
        return None
    payload = bytes(packet[EAPOL].payload)
    if len(payload) < 4:
        return None
    code, identifier, length = struct.unpack("!BBH", payload[:4])
    if length < 4 or len(payload) < length:
        LOG.warning("收到长度异常的 EAP 包: declared=%d actual=%d", length, len(payload))
        return None
    return code, identifier, payload[:length]


def printable_ratio(text: str) -> float:
    if not text:
        return 0.0
    accepted = sum(char.isprintable() and char not in "\x00\ufffd" for char in text)
    return accepted / len(text)


def extract_server_messages(eap: bytes) -> list[str]:
    """Extract GB18030 text from Ruijie attributes appended to EAP result packets."""
    data = eap[4:]
    positions: list[int] = []
    start = 0
    while True:
        position = data.find(RUIJIE_VENDOR_MARKER, start)
        if position < 0:
            break
        positions.append(position)
        start = position + len(RUIJIE_VENDOR_MARKER)

    messages: list[str] = []
    for index, position in enumerate(positions):
        end = positions[index + 1] if index + 1 < len(positions) else len(data)
        attribute_type = data[position + 4] if position + 4 < len(data) else -1
        if attribute_type != 0:
            continue
        chunk = data[position + 6 : end].rstrip(b"\x00")
        if not chunk:
            continue
        candidates = [chunk]
        declared = data[position + 5] if position + 5 < len(data) else 0
        if declared and len(chunk) >= declared:
            candidates.insert(0, chunk[:declared])
        for candidate in candidates:
            try:
                text = candidate.decode("gb18030").strip("\x00 \r\n\t")
            except UnicodeDecodeError:
                continue
            contains_language = any("\u4e00" <= char <= "\u9fff" for char in text)
            if contains_language and printable_ratio(text) >= 0.8:
                if text not in messages:
                    messages.append(text)
                break
    return messages


class Supplicant:
    def __init__(
        self,
        interface: str,
        identity: bytes,
        password: bytes,
        timeout: float,
        retries: int,
        repeats: int,
        *,
        private: bool = True,
        logoff_first: bool = True,
        response_delay: float = 0.04,
        network: NetworkInfo | None = None,
        tail_options: TailOptions | None = None,
        source_mac: str | None = None,
        locked_server: str | None = None,
        service_name: str | None = None,
        static_id: str | None = None,
        start_timeout: float = 3.0,
    ) -> None:
        self.interface = interface
        self.identity = identity
        self.password = password
        self.timeout = timeout
        self.retries = retries
        self.repeats = repeats
        self.private = private
        self.logoff_first = logoff_first
        self.response_delay = max(0.0, response_delay)
        self.service_name = (service_name or "").strip()
        self.static_id = (static_id or "").strip()
        self.start_timeout = max(0.5, float(start_timeout))
        self._service_bytes = b""
        if self.service_name:
            try:
                self._service_bytes = self.service_name.encode("gb18030")
            except UnicodeEncodeError:
                LOG.warning("服务名无法用 GB18030 编码，使用默认服务名")
        network_obj = network or collect_network_info(interface)
        if self.static_id:
            try:
                static_bytes = self.static_id.encode("ascii")
            except UnicodeEncodeError:
                static_bytes = b""
                LOG.warning("静态 ID 必须是 ASCII，使用默认值")
            if static_bytes:
                network_obj = replace(network_obj, static_id=static_bytes)
        self.network = network_obj
        self.tail_options = tail_options
        self._session_nonce = os.urandom(16).hex().upper().encode()
        self._stop_event = threading.Event()
        self.source = source_mac or get_if_hwaddr(interface)
        self.locked_server = normalize_mac(locked_server) or None
        if self.locked_server:
            LOG.info(
                "已锁定认证服务器 %s，EAPOL-Start 将单播发送",
                self.locked_server,
            )
        self.packets: queue.Queue[Ether] = queue.Queue()
        self.server_mac: str | None = None
        self.connected = False
        self.connected_at: float | None = None

    def stop(self) -> None:
        """Request cancellation of a running authentication attempt."""
        self._stop_event.set()

    def logoff(self) -> None:
        """Send a private-tail EAPOL-Logoff for the current account.

        Logoff always goes to the standard PAE group. A unicast Logoff to the
        authenticator does not clear the switch session on HXXY, which makes the
        next Start fail with EAP-Failure.
        """
        frame = (
            Ether(src=self.source, dst=STANDARD_GROUP)
            / EAPOL(version=1, type=2, len=0)
            / self._control_tail()
        )
        self.send(frame, count=self.repeats)
        LOG.info("已发送带私有尾的 EAPOL-Logoff")

    def _tail(self, iv: bytes, nonce: bytes | None = None) -> bytes:
        if not self.private:
            return b""
        options = self.tail_options
        if options is None:
            options = TailOptions()
        if self._service_bytes:
            options = replace(options, service_name=self._service_bytes)
        if nonce is not None:
            options = replace(options, nonce=nonce)
        return build_tail(
            self.identity,
            self.password,
            self.source,
            self.network,
            iv,
            options,
        )

    def _control_tail(self) -> bytes:
        """Tail carried by EAPOL-Start/Logoff: session nonce, zero fingerprint."""
        if not self.private:
            return b""
        nonce = self.tail_options.nonce if self.tail_options else self._session_nonce
        options = TailOptions(
            nonce=nonce,
            fingerprint=bytes(128),
            client_flag=b"\x01",
        )
        if self._service_bytes:
            options = replace(options, service_name=self._service_bytes)
        return build_tail(
            self.identity,
            self.password,
            self.source,
            self.network,
            bytes(16),
            options,
        )

    def send(self, packet: Ether, count: int = 1) -> None:
        if count > 1:
            send_burst(self.interface, [packet] * count)
        else:
            sendp(packet, iface=self.interface, verbose=False)

    def send_start(self) -> None:
        frame = (
            Ether(src=self.source, dst=self.locked_server or RUIJIE_GROUP)
            / EAPOL(version=1, type=1, len=0)
            / self._control_tail()
        )
        self.send(frame, count=self.repeats)
        if self.locked_server:
            LOG.info("正在请求锁定的认证服务器 %s ...", self.locked_server)
        else:
            LOG.info("正在寻找认证服务器...")

    def authenticate(self) -> bool:
        self.connected = False
        self.connected_at = None
        sniffer = AsyncSniffer(
            iface=self.interface,
            filter="ether proto 0x888e",
            prn=self.packets.put,
            store=False,
        )
        try:
            sniffer.start()
            if self._stop_event.wait(0.3):
                LOG.info("认证已取消")
                return False
            if not sniffer.running:
                raise RuntimeError("抓包线程启动失败")
            if self.logoff_first:
                LOG.info("正在清理可能存在的旧会话...")
                logoff = (
                    Ether(src=self.source, dst=STANDARD_GROUP)
                    / EAPOL(version=1, type=2, len=0)
                    / self._control_tail()
                )
                self.send(logoff, count=self.repeats)
                if self._stop_event.wait(0.1):
                    LOG.info("认证已取消")
                    return False
            self.send_start()
            deadline = time.monotonic() + self.timeout
            starts_sent = 1
            next_retry = time.monotonic() + self.start_timeout
            handled: dict[tuple[int, int, bytes], bytes] = {}

            while time.monotonic() < deadline:
                if self._stop_event.is_set():
                    LOG.info("认证已取消")
                    return False
                now = time.monotonic()
                if self.server_mac is None and now >= next_retry:
                    if starts_sent >= self.retries:
                        break
                    self.send_start()
                    starts_sent += 1
                    next_retry = now + self.start_timeout
                try:
                    packet = self.packets.get(timeout=min(0.5, deadline - now))
                except queue.Empty:
                    continue
                source = packet[Ether].src.lower()
                if source == self.source.lower():
                    continue
                if self.locked_server and source != self.locked_server:
                    LOG.debug("忽略非锁定服务器 %s 的 EAP 帧", source)
                    continue
                parsed = parse_eap(packet)
                if parsed is None:
                    continue
                code, identifier, eap = parsed
                self.connected = True
                self.connected_at = time.monotonic()
                self.server_mac = source
                LOG.debug(
                    "RX EAP code=%d id=%d length=%d from=%s",
                    code,
                    identifier,
                    len(eap),
                    source,
                )

                if code == EAP_SUCCESS:
                    for message in extract_server_messages(eap):
                        LOG.info("服务器消息: %s", message)
                    LOG.info("认证成功")
                    return True
                if code == EAP_FAILURE:
                    messages = extract_server_messages(eap)
                    if messages:
                        for message in messages:
                            LOG.error("服务器消息: %s", message)
                    else:
                        LOG.error("认证失败（服务器未返回可读原因）")
                    LOG.debug("EAP-Failure payload=%s", eap[4:].hex())
                    return False
                if code != EAP_REQUEST or len(eap) < 5:
                    continue

                eap_type = eap[4]
                destination = source
                self.server_mac = destination
                key = (identifier, eap_type, eap[5:])
                response = handled.get(key)
                is_new = response is None

                if eap_type == EAP_IDENTITY:
                    if is_new:
                        LOG.info("已连接认证服务器，提交用户身份...")
                        response = eap_identity_response(
                            identifier, self.identity
                        ) + self._tail(bytes(16), nonce=self._session_nonce)
                        handled[key] = response
                elif eap_type == EAP_MD5:
                    if len(eap) < 6:
                        LOG.error("服务器 MD5 Challenge 格式不完整")
                        return False
                    value_size = eap[5]
                    if value_size != 16 or len(eap) < 6 + value_size:
                        LOG.error("不支持的 MD5 Challenge 长度: %d", value_size)
                        return False
                    if is_new:
                        challenge = eap[6 : 6 + value_size]
                        LOG.info("正在进行 EAP-MD5 认证...")
                        response = eap_md5_response(
                            identifier, self.password, challenge, self.identity
                        ) + self._tail(challenge)
                        handled[key] = response
                else:
                    LOG.error("服务器请求了未实现的 EAP 类型: %d", eap_type)
                    return False

                if not is_new:
                    LOG.debug("忽略重复的 EAP 请求 id=%d type=%d", identifier, eap_type)
                    continue

                if self.response_delay:
                    if self._stop_event.wait(self.response_delay):
                        LOG.info("认证已取消")
                        return False
                self.send(
                    build_eapol_frame(self.source, destination, response),
                    count=self.repeats,
                )

            if self.server_mac is None:
                if self.locked_server:
                    LOG.error("锁定的认证服务器 %s 未响应", self.locked_server)
                else:
                    LOG.error("网卡连接失败或未找到认证服务器")
            else:
                LOG.error("认证超时，服务器未返回最终结果")
            return False
        except PermissionError:
            LOG.error("没有原始网卡访问权限；Windows 请以管理员运行，Linux 请用 sudo")
            return False
        except OSError as exc:
            LOG.error("网卡访问失败: %s", exc)
            return False
        finally:
            if sniffer.running:
                sniffer.stop()


def send_logoff(interface: str) -> None:
    source = get_if_hwaddr(interface)
    for destination in (RUIJIE_GROUP, STANDARD_GROUP):
        frame = Ether(src=source, dst=destination) / EAPOL(version=1, type=2, len=0)
        sendp(frame, iface=interface, count=3, inter=0.001, verbose=False)
    LOG.info("已发送 EAPOL-Logoff")


def parse_args(argv: Iterable[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="跨平台锐捷 EAP-MD5 校园网认证工具")
    parser.add_argument("-i", "--interface", help="网卡名称、描述或 NPF 设备名")
    parser.add_argument("--list-interfaces", action="store_true", help="列出网卡后退出")
    parser.add_argument("-u", "--username", help="校园网用户名；省略时交互输入")
    parser.add_argument("-p", "--password", help="校园网密码；省略时隐藏交互输入")
    parser.add_argument(
        "--encoding", default="gb18030", help="账号密码编码（默认: gb18030）"
    )
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--retries", type=int, default=3)
    parser.add_argument(
        "--repeats", type=int, default=3, help="每个请求的应答次数（默认与原客户端一致: 3）"
    )
    parser.add_argument(
        "--no-private",
        action="store_true",
        help="不附加锐捷私有尾，仅发送标准 EAP-MD5（调试用）",
    )
    parser.add_argument("--log-file", type=Path, default=Path("ruijie-auth.log"))
    parser.add_argument(
        "--server",
        help="锁定认证服务器 MAC，EAPOL-Start 单播发往该地址（Logoff 仍用标准组播）",
    )
    parser.add_argument(
        "--service-name",
        help="服务名（私有属性 0x39），默认 电信宽带服务",
    )
    parser.add_argument(
        "--static-id",
        help="静态 ID（私有属性 0x54），默认 Static:AB45A862",
    )
    parser.add_argument(
        "--start-timeout",
        type=float,
        default=3.0,
        help="Start 重试间隔秒数（默认 3）",
    )
    parser.add_argument("--logoff", action="store_true", help="仅注销当前 802.1X 会话")
    parser.add_argument("-v", "--verbose", action="store_true")
    return parser.parse_args(argv)


def main(argv: Iterable[str] | None = None) -> int:
    args = parse_args(argv)
    setup_logging(args.log_file, args.verbose)
    if args.list_interfaces:
        print_interfaces()
        return 0
    interface = choose_interface(args.interface)
    info = resolve_iface(interface)
    LOG.info(
        "使用网卡: %s | %s | %s",
        getattr(info, "description", info.name),
        getattr(info, "ip", "-"),
        get_if_hwaddr(interface),
    )

    if args.logoff:
        send_logoff(interface)
        return 0

    username = args.username or input("校园网用户名: ").strip()
    if not username:
        LOG.error("用户名不能为空")
        return 2
    password = args.password if args.password is not None else getpass.getpass("校园网密码: ")
    if not password:
        LOG.error("密码不能为空")
        return 2

    try:
        identity = username.encode(args.encoding)
        password_bytes = password.encode(args.encoding)
    except LookupError:
        LOG.error("未知字符编码: %s", args.encoding)
        return 2
    except UnicodeEncodeError as exc:
        LOG.error("账号或密码无法用 %s 编码: %s", args.encoding, exc)
        return 2

    LOG.info("开始认证（用户名和密码不会写入日志）")
    supplicant = Supplicant(
        interface=interface,
        identity=identity,
        password=password_bytes,
        timeout=args.timeout,
        retries=max(1, args.retries),
        repeats=max(1, args.repeats),
        private=not args.no_private,
        locked_server=args.server,
        service_name=args.service_name,
        static_id=args.static_id,
        start_timeout=args.start_timeout,
    )
    try:
        return 0 if supplicant.authenticate() else 1
    finally:
        password = ""
        password_bytes = b""


if __name__ == "__main__":
    raise SystemExit(main())
