"""DHCP control (mirrors Core.Protocols.DhcpControl, Windows iphlpapi)."""

from __future__ import annotations

import ctypes
import re
import time
from dataclasses import dataclass
from enum import Enum
from typing import Any

ERROR_SUCCESS = 0
ERROR_INSUFFICIENT_BUFFER = 122
GAA_FLAG_INCLUDE_GATEWAYS = 0x0010


class DhcpOperation(Enum):
    STATUS = "status"
    RENEW = "renew"
    RELEASE = "release"
    RELEASE_RENEW = "release-renew"


@dataclass(frozen=True)
class DhcpLease:
    interface: str
    adapter_name: str
    adapter_index: int
    ip: str
    mask: str
    gateway: str


@dataclass(frozen=True)
class DhcpOperationResult:
    ok: bool
    message: str
    operation: DhcpOperation
    lease: DhcpLease | None
    option82: bool
    elapsed_ms: int


class _IpAdapterIndexMap(ctypes.Structure):
    _fields_ = [("Index", ctypes.c_uint), ("Name", ctypes.c_wchar * 128)]


def _iphlpapi():
    if os_name() != "nt":
        raise OSError("DHCP control requires Windows (iphlpapi)")
    return ctypes.windll.iphlpapi  # type: ignore[attr-defined]


def os_name() -> str:
    import os
    return os.name


def _enumerate_adapters() -> list[_IpAdapterIndexMap]:
    api = _iphlpapi()
    size = ctypes.c_uint(0)
    rc = api.GetInterfaceInfo(None, ctypes.byref(size))
    if rc not in (0, ERROR_INSUFFICIENT_BUFFER):
        raise OSError(f"GetInterfaceInfo size probe failed: {rc}")
    if size.value == 0:
        return []
    buffer = ctypes.create_string_buffer(size.value)
    rc = api.GetInterfaceInfo(buffer, ctypes.byref(size))
    if rc != 0:
        raise OSError(f"GetInterfaceInfo failed: {rc}")
    count = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_int)).contents.value
    struct_size = ctypes.sizeof(_IpAdapterIndexMap)
    result = []
    base = ctypes.sizeof(ctypes.c_int)
    for i in range(count):
        result.append(_IpAdapterIndexMap.from_buffer(buffer, base + i * struct_size))
    return result


def _adapter_matches(adapter_name: str, nic) -> bool:
    if adapter_name.lower() in (nic.name.lower(), nic.description.lower()):
        return True
    match = re.search(r"\{([0-9a-fA-F-]{36})\}", adapter_name)
    return match is not None and match.group(1).lower() == nic.id.strip("{}").lower()


def _read_lease(nic, adapter: _IpAdapterIndexMap | None) -> DhcpLease | None:
    import psutil

    addresses = psutil.net_if_addrs().get(nic.name, [])
    ipv4 = mask = ""
    for address in addresses:
        if address.family == socket_af_inet():
            ipv4 = address.address
            mask = address.netmask or ""
            break
    if not ipv4:
        return None
    return DhcpLease(nic.id, adapter.Name if adapter else nic.name,
                     adapter.Index if adapter else 0, ipv4, mask, nic.gateway)


def socket_af_inet() -> int:
    import socket
    return socket.AF_INET


def run(interface_hint: str, operation: DhcpOperation, timeout: float,
        option82: bool = False) -> DhcpOperationResult:
    """Mirror DhcpControl.RunAsync: release/renew/status with lease polling."""
    from ..diagnostics.core import resolve_nic

    started = time.monotonic_ns() // 1_000_000
    messages: list[str] = []
    nic = resolve_nic(interface_hint)
    if nic is None:
        raise OSError(f"interface not found: {interface_hint}")
    api = _iphlpapi()

    adapters = _enumerate_adapters()
    matches = [a for a in adapters if _adapter_matches(a.Name, nic)]
    if not matches:
        available = ", ".join(a.Name for a in adapters)
        raise OSError(f"adapter index not found for {nic.name}; available: {available}")
    adapter = matches[0]

    if operation == DhcpOperation.STATUS:
        lease = _read_lease(nic, adapter)
        elapsed = time.monotonic_ns() // 1_000_000 - started
        return DhcpOperationResult(lease is not None,
                                   "no IPv4 lease" if lease is None else "lease read",
                                   operation, lease, option82, elapsed)

    if operation in (DhcpOperation.RELEASE, DhcpOperation.RELEASE_RENEW):
        adapter_struct = _IpAdapterIndexMap(Index=adapter.Index, Name=adapter.Name)
        rc = api.IpReleaseAddress(ctypes.byref(adapter_struct))
        messages.append(f"IpReleaseAddress={rc}")
        if rc != ERROR_SUCCESS:
            elapsed = time.monotonic_ns() // 1_000_000 - started
            return DhcpOperationResult(False, f"IpReleaseAddress failed: {rc}",
                                       operation, _read_lease(nic, adapter),
                                       option82, elapsed)

    if operation in (DhcpOperation.RENEW, DhcpOperation.RELEASE_RENEW):
        adapter_struct = _IpAdapterIndexMap(Index=adapter.Index, Name=adapter.Name)
        rc = api.IpRenewAddress(ctypes.byref(adapter_struct))
        messages.append(f"IpRenewAddress={rc}")
        if rc != ERROR_SUCCESS:
            elapsed = time.monotonic_ns() // 1_000_000 - started
            return DhcpOperationResult(False, f"IpRenewAddress failed: {rc}",
                                       operation, _read_lease(nic, adapter),
                                       option82, elapsed)
        _wait_for_lease(nic, timeout)

    lease = _read_lease(nic, adapter)
    ok = operation == DhcpOperation.RELEASE or bool(lease and lease.ip)
    elapsed = time.monotonic_ns() // 1_000_000 - started
    detail = "; ".join(messages)
    if not ok:
        detail = f"{detail}; no IPv4 lease within {timeout:0f}s"
    return DhcpOperationResult(ok, detail, operation, lease, option82, elapsed)


def _wait_for_lease(nic, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        lease = _read_lease(nic, None)
        if lease and lease.ip and not lease.ip.startswith("169.254."):
            return
        time.sleep(0.5)
