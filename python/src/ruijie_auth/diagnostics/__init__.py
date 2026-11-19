"""Diagnostics package (mirrors Core.Diagnostics)."""

from .core import (  # noqa: F401
    ArpMonitorResult,
    ArpObservation,
    DEFAULT_PORTS,
    NicInfo,
    NetworkProbeResult,
    PIPE_PATH,
    ProxyProbeResult,
    detect_dialup,
    doctor_available,
    gateway_of,
    is_ruijie,
    list_nics,
    monitor_arp,
    probe_network,
    probe_proxy,
    resolve_mac,
    resolve_nic,
    vendor_of,
)
