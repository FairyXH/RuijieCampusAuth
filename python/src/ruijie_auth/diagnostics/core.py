"""Diagnostics (mirrors Core.Diagnostics): OUI/NIC/ARP/probe/proxy/doctor."""

from __future__ import annotations

import ctypes
import os
import re
import socket
import time
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime

from ..private import normalize_mac

# ------------------------------------------------------------------ OuiDetect

_VENDORS = {
    "00:1a:a9": "Ruijie Networks",
    "00:74:9c": "Ruijie Networks",
    "00:23:89": "Ruijie Networks",
}


def vendor_of(mac: str | None) -> str:
    normalized = normalize_mac(mac)
    if len(normalized) < 8:
        return "Unknown"
    return _VENDORS.get(normalized[:8], "Unknown")


def is_ruijie(mac: str | None) -> bool:
    return vendor_of(mac) != "Unknown"


# ------------------------------------------------------------- NicInventory

_FILTER_MARKERS = [
    "npcap packet driver", "kaspersky lab ndis", "wfp native mac layer",
    "wfp 802.3 mac layer", "qos packet scheduler", "virtual wifi filter",
    "native wifi filter", "microsoft wi-fi direct", "microsoft teredo",
    "ip-https platform", "6to4 adapter", "wan miniport",
]


@dataclass(frozen=True)
class NicInfo:
    id: str
    name: str
    description: str
    type: str
    status: str
    mac: str
    speed_bps: int
    ipv4: str
    mask: str
    gateway: str


def _windows_adapters() -> dict[str, dict]:
    """GUID -> {name, description, index} via GetInterfaceInfo (iphlpapi)."""
    import ipaddress

    class _IpAdapterIndexMap(ctypes.Structure):
        _fields_ = [("Index", ctypes.c_uint),
                    ("Name", ctypes.c_wchar * 128)]

    iphlpapi = ctypes.windll.iphlpapi  # type: ignore[attr-defined]
    size = ctypes.c_uint(0)
    rc = iphlpapi.GetInterfaceInfo(None, ctypes.byref(size))
    if rc not in (0, 111):  # ERROR_SUCCESS / ERROR_INSUFFICIENT_BUFFER
        raise OSError(f"GetInterfaceInfo size probe failed: {rc}")
    if size.value == 0:
        return {}
    buffer = ctypes.create_string_buffer(size.value)
    rc = iphlpapi.GetInterfaceInfo(buffer, ctypes.byref(size))
    if rc != 0:
        raise OSError(f"GetInterfaceInfo failed: {rc}")
    count = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_int)).contents.value
    struct_size = ctypes.sizeof(_IpAdapterIndexMap)
    adapters: dict[str, dict] = {}
    for i in range(count):
        offset = ctypes.sizeof(ctypes.c_int) + i * struct_size
        item = _IpAdapterIndexMap.from_buffer(buffer, offset)
        wide_name = item.Name
        adapter_name = str(ipaddress.ip_address(0))  # placeholder replaced below
        # Name is a wchar buffer; read until NUL
        chars = []
        for ch in item.Name:
            if ch == "\x00":
                break
            chars.append(ch)
        adapter_name = "".join(chars)
        adapters[adapter_name.lower()] = {
            "name": adapter_name,
            "index": item.Index,
        }
    return adapters


def list_nics() -> list[NicInfo]:
    import psutil  # optional but standard for the Python build

    result: list[NicInfo] = []
    seen: set[str] = set()
    stats = psutil.net_if_stats()
    addrs = psutil.net_if_addrs()
    for name, addresses in addrs.items():
        description = name
        if any(marker in name.lower() for marker in _FILTER_MARKERS):
            continue
        ipv4 = mask = gateway = ""
        mac = ""
        for address in addresses:
            family_name = getattr(address.family, "name", "") or str(address.family)
            if socket.AF_INET in (address.family,) or family_name.endswith("AF_INET"):
                ipv4 = address.address
                mask = address.netmask or ""
            elif family_name.endswith("AF_LINK") or address.family == -1:
                mac = normalize_mac(address.address)
        nic_stats = stats.get(name)
        result.append(NicInfo(
            id=name, name=name, description=description,
            type="", status="up" if (nic_stats and nic_stats.isup) else "down",
            mac=mac, speed_bps=(nic_stats.speed * 1_000_000) if nic_stats else 0,
            ipv4=ipv4, mask=mask, gateway=gateway))
    return result


def resolve_nic(hint: str | None):
    """Mirror PrivateTail.FindInterface: match name/description/MAC."""
    if not hint or not hint.strip():
        return None
    hint = hint.strip()
    guid_match = re.search(r"\{([0-9a-fA-F-]{36})\}", hint)
    mac = normalize_mac(hint)
    nics = list_nics()
    if guid_match:
        guid = guid_match.group(1).lower()
        for nic in nics:
            if guid in nic.id.lower():
                return nic
    for nic in nics:
        if hint.lower() in nic.name.lower() or hint.lower() in nic.description.lower():
            return nic
    if mac:
        for nic in nics:
            if normalize_mac(nic.mac) == mac:
                return nic
    return None


def gateway_of(nic: NicInfo) -> str:
    if nic.gateway:
        return nic.gateway
    # psutil does not expose gateways per-IPv4; read via netifaces-free route:
    return _default_gateway_for(nic.name) or ""


def _default_gateway_for(interface: str) -> str | None:
    try:
        import psutil  # noqa: F401
    except ImportError:
        return None
    try:
        gateways = psutil.net_if_addrs()  # placeholder; real lookup below
    except Exception:
        return None
    # psutil.net_gateways? Not available; use psutil's net_if_addrs only.
    # Read the routing table via `route print` (Windows) / `ip route` (Linux).
    import subprocess
    try:
        if os.name == "nt":
            output = subprocess.run(
                ["route", "print", "0.0.0.0"], capture_output=True,
                text=True, timeout=5).stdout
            for line in output.splitlines():
                parts = line.split()
                if len(parts) >= 5 and parts[0] == "0.0.0.0" and interface in line:
                    return parts[2]
        else:
            output = subprocess.run(
                ["ip", "route", "show", "default"], capture_output=True,
                text=True, timeout=5).stdout
            for line in output.splitlines():
                if interface in line:
                    match = re.search(r"via\s+(\d+\.\d+\.\d+\.\d+)", line)
                    if match:
                        return match.group(1)
    except Exception:
        return None
    return None


# --------------------------------------------------------------- ArpMonitor

@dataclass(frozen=True)
class ArpObservation:
    time: str
    ip: str
    mac: str


@dataclass(frozen=True)
class ArpMonitorResult:
    interface: str
    gateway: str
    initial_mac: str | None
    final_mac: str | None
    changes: list[str]
    observations: list[ArpObservation] | None


def _send_arp(ip: str) -> str | None:
    """SendARP via iphlpapi (Windows); returns xx:xx:xx:xx:xx:xx or None."""
    if os.name != "nt":
        return None
    import ipaddress
    try:
        address = ipaddress.IPv4Address(ip)
    except ValueError:
        return None
    iphlpapi = ctypes.windll.iphlpapi  # type: ignore[attr-defined]
    mac = ctypes.create_string_buffer(6)
    length = ctypes.c_uint(6)
    destination = int(address)
    rc = iphlpapi.SendARP(ctypes.c_uint(destination), ctypes.c_uint(0),
                          mac, ctypes.byref(length))
    if rc != 0 or length.value < 6:
        return None
    return ":".join(f"{b:02x}" for b in mac.raw[:6])


def resolve_mac(ip: str) -> str | None:
    return _send_arp(ip)


def monitor_arp(interface_hint: str, duration: float, interval: float):
    nic = resolve_nic(interface_hint)
    if nic is None:
        raise OSError(f"interface not found: {interface_hint}")
    gateway = gateway_of(nic)
    if not gateway:
        raise OSError(f"no IPv4 gateway on {nic.name}")
    observations: list[ArpObservation] = []
    changes: list[str] = []
    initial = None
    previous = None
    deadline = time.monotonic() + duration
    while time.monotonic() < deadline:
        mac = resolve_mac(gateway)
        now = datetime.now().strftime("%H:%M:%S")
        observations.append(ArpObservation(now, gateway, mac or "(no reply)"))
        if initial is None:
            initial = mac
        if previous is not None and (previous or "").lower() != (mac or "").lower():
            changes.append(f"{now} {previous} -> {mac or '(no reply)'}")
        previous = mac
        time.sleep(interval)
    return ArpMonitorResult(nic.name, gateway, initial, previous, changes,
                            observations)


# ------------------------------------------------------------ NetworkProbe

@dataclass(frozen=True)
class NetworkProbeResult:
    interface: str
    gateway: str
    gateway_reachable: bool
    gateway_ms: int | None
    dns_ok: bool
    dns_detail: str
    http_ok: bool
    http_status: int | None
    detail: str
    elapsed_ms: int


def probe_network(interface_hint: str | None, url: str) -> NetworkProbeResult:
    started = time.monotonic_ns() // 1_000_000
    nic = resolve_nic(interface_hint) if interface_hint and interface_hint.strip() else None
    gateway = gateway_of(nic) if nic else ""
    details: list[str] = []
    gateway_reachable = False
    gateway_ms = None

    if gateway:
        try:
            import subprocess
            if os.name == "nt":
                output = subprocess.run(
                    ["ping", "-n", "1", "-w", "2000", gateway],
                    capture_output=True, text=True, timeout=8)
                gateway_reachable = "TTL=" in output.stdout or "time=" in output.stdout
            else:
                output = subprocess.run(
                    ["ping", "-c", "1", "-W", "2", gateway],
                    capture_output=True, text=True, timeout=8)
                gateway_reachable = output.returncode == 0
            if gateway_reachable:
                match = re.search(r"[=<](\d+)ms", output.stdout)
                gateway_ms = int(match.group(1)) if match else None
            details.append(f"gateway={gateway} reachable={gateway_reachable}")
        except Exception as exc:
            details.append(f"gateway ping failed: {exc}")
    else:
        details.append("gateway=unavailable")

    dns_ok = False
    dns_detail = ""
    try:
        from urllib.parse import urlparse
        host = urlparse(url).hostname
    except Exception:
        host = None
    if host:
        try:
            addresses = socket.gethostbyname_ex(host)[2]
            dns_ok = len(addresses) > 0
            dns_detail = f"{host} -> {', '.join(addresses[:2])}"
        except Exception as exc:
            dns_detail = f"{host} -> {exc}"
    else:
        dns_detail = f"invalid url: {url}"

    http_ok = False
    http_status = None
    if url and "://" in url:
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            request = urllib.request.Request(url)
            with opener.open(request, timeout=5) as response:
                http_ok = True
                http_status = response.status
            details.append(f"http={http_status}")
        except Exception as exc:
            details.append(f"http failed: {exc}")

    return NetworkProbeResult(nic.name if nic else "(default)", gateway,
                              gateway_reachable, gateway_ms, dns_ok, dns_detail,
                              http_ok, http_status, "; ".join(details),
                              time.monotonic_ns() // 1_000_000 - started)


# -------------------------------------------------------------- ProxyDetect

DEFAULT_PORTS = [1080, 3128, 8000, 8080, 8888]


@dataclass(frozen=True)
class ProxyProbeResult:
    gateway: str
    open_ports: list[int]
    environment_proxy: str | None


def probe_proxy(gateway: str, ports: list[int],
                timeout_ms: int) -> ProxyProbeResult:
    open_ports: list[int] = []
    if gateway.strip():
        for port in ports:
            try:
                with socket.create_connection(
                        (gateway, port), timeout=max(0.1, timeout_ms) / 1000):
                    open_ports.append(port)
            except Exception:
                pass
    environment = (os.environ.get("HTTP_PROXY")
                   or os.environ.get("HTTPS_PROXY")
                   or os.environ.get("ALL_PROXY"))
    return ProxyProbeResult(gateway, open_ports, environment)


def detect_dialup() -> list[str]:
    reasons: list[str] = []
    try:
        import psutil
    except ImportError:
        return reasons
    for name, stats in psutil.net_if_stats().items():
        if not stats.isup:
            continue
        if re.search(r"ppp|vpn|4g|5g|拨号", name, re.IGNORECASE):
            reasons.append(f"{name}")
    return reasons


# -------------------------------------------------------------- DoctorPipe

PIPE_PATH = r"\\.\pipe\SU_DOCTOR_INTERFACE"


def doctor_available(timeout_ms: int = 100) -> bool:
    if os.name != "nt":
        return False
    import msvcrt
    import winreg  # noqa: F401
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    GENERIC_READ = 0x80000000
    GENERIC_WRITE = 0x40000000
    OPEN_EXISTING = 3
    handle = kernel32.CreateFileW(
        PIPE_PATH, GENERIC_READ | GENERIC_WRITE, 0, None,
        OPEN_EXISTING, 0, None)
    if handle == ctypes.c_void_p(-1).value or handle == 0xFFFFFFFFFFFFFFFF:
        return False
    kernel32.CloseHandle(handle)
    return True
