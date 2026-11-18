"""Loopback MCP client for the NetworkGuardian desktop app.

NetworkGuardian exposes a stateless HTTP MCP endpoint on 127.0.0.1. The client
only performs the initialize handshake and guardian_status queries; it never
changes Guardian configuration or triggers network actions.
"""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime
from typing import Any

DEFAULT_GUARDIAN_URL = "http://127.0.0.1:18765/mcp"
PROTOCOL_VERSION = "2025-11-25"
_NPF_GUID = re.compile(r"\{([0-9a-fA-F-]{36})\}")


class GuardianError(RuntimeError):
    """Raised when the Guardian MCP endpoint is unreachable or malformed."""


def normalize_guardian_url(value: str) -> str:
    text = (value or "").strip() or DEFAULT_GUARDIAN_URL
    parsed = urllib.parse.urlsplit(text)
    if (
        parsed.scheme != "http"
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
    ):
        raise ValueError("网络管家 MCP 地址必须是 http://127.0.0.1:<端口>/mcp")
    host = (parsed.hostname or "").lower()
    if host == "localhost":
        host = "127.0.0.1"
    if host != "127.0.0.1":
        raise ValueError("网络管家 MCP 只允许本机 127.0.0.1")
    path = parsed.path or "/mcp"
    if path not in ("/mcp", "/mcp/"):
        raise ValueError("网络管家 MCP 路径必须是 /mcp")
    try:
        port = parsed.port
    except ValueError as exc:
        raise ValueError("网络管家 MCP 端口无效") from exc
    if port is None:
        raise ValueError("网络管家 MCP 地址必须包含端口")
    return f"http://127.0.0.1:{port}{path}"


def npf_guid(value: str | None) -> str:
    match = _NPF_GUID.search(value or "")
    return match.group(1).lower() if match else ""


def _same_guid(first: str | None, second: str | None) -> bool:
    return bool(first and second) and first.strip("{}").lower() == second.strip(
        "{}"
    ).lower()


def _local_time(value: str | None) -> str:
    if not value:
        return "-"
    try:
        text = value.replace("Z", "+00:00")
        return datetime.fromisoformat(text).astimezone().strftime("%m-%d %H:%M:%S")
    except ValueError:
        return value


@dataclass
class GuardianProbe:
    online: bool | None
    stable_online: bool = False
    captive_portal: bool = False
    interface_name: str = ""
    interface_id: str = ""
    description: str = ""
    ip: str = ""
    gateway: str = ""
    mac: str = ""
    summary: str = ""
    source: str = "global"
    timestamp: str = ""
    detail: str = ""

    @property
    def is_offline(self) -> bool:
        return self.online is False or self.captive_portal

    def short(self) -> str:
        name = self.interface_name or "认证网卡"
        if self.online is None:
            state = "状态未知"
        elif self.captive_portal:
            state = "疑似强制门户"
        elif self.online:
            state = "在线"
        else:
            state = "离线"
        address = f" | {self.ip}" if self.ip else ""
        return f"{name} {state}{address}"

    def describe(self) -> str:
        if self.online is None:
            state = "未知（无探测数据）"
        elif self.captive_portal:
            state = "疑似强制门户，需要认证"
        elif self.online:
            state = "在线（外网探测成功）"
        else:
            state = "离线（外网探测失败）"
        lines = [
            f"网卡: {self.interface_name or '-'} | {self.description or '-'}",
            f"地址: {self.ip or '-'} | 网关 {self.gateway or '-'} | MAC {self.mac or '-'}",
            f"外网: {state} | 稳定: {'是' if self.stable_online else '否'}",
            f"来源: {self.source} | 探测时间: {_local_time(self.timestamp)}",
        ]
        if self.summary:
            lines.append(f"摘要: {self.summary}")
        if self.detail:
            lines.append(self.detail)
        return "\n".join(lines)


class GuardianClient:
    def __init__(
        self,
        url: str = DEFAULT_GUARDIAN_URL,
        timeout: float = 8.0,
    ) -> None:
        self.url = normalize_guardian_url(url)
        self.timeout = max(1.0, min(float(timeout), 30.0))
        self._sequence = 0
        self._initialized = False
        # Never route loopback MCP traffic through an environment proxy.
        self._opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({})
        )

    def _rpc(self, method: str, params: dict[str, Any] | None = None) -> Any:
        self._sequence += 1
        payload: dict[str, Any] = {
            "jsonrpc": "2.0",
            "id": self._sequence,
            "method": method,
        }
        if params is not None:
            payload["params"] = params
        request = urllib.request.Request(
            self.url,
            json.dumps(payload, allow_nan=False).encode("utf-8"),
            {
                "Content-Type": "application/json",
                "Accept": "application/json, text/event-stream",
                "MCP-Protocol-Version": PROTOCOL_VERSION,
            },
            method="POST",
        )
        try:
            with self._opener.open(request, timeout=self.timeout) as response:
                raw = response.read()
                content_type = response.headers.get("Content-Type", "")
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise GuardianError(f"无法连接网络管家 MCP: {exc}") from exc
        message = self._decode(raw, content_type)
        if message.get("id") != self._sequence:
            raise GuardianError("MCP 响应 ID 不匹配")
        if "error" in message:
            raise GuardianError(
                json.dumps(message["error"], ensure_ascii=False)[:400]
            )
        return message.get("result")

    @staticmethod
    def _decode(raw: bytes, content_type: str) -> dict[str, Any]:
        text = raw.decode("utf-8", "replace").strip()
        try:
            message = json.loads(text)
            if isinstance(message, dict):
                return message
        except ValueError:
            pass
        if "text/event-stream" in content_type or "data:" in text:
            for line in text.splitlines():
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if not data:
                    continue
                try:
                    message = json.loads(data)
                except ValueError:
                    continue
                if isinstance(message, dict):
                    return message
        raise GuardianError("MCP 返回了无法解析的响应")

    def initialize(self) -> None:
        self._rpc(
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {
                    "name": "ruijie-campus-auth",
                    "version": "0.7.0",
                },
            },
        )

    def status(self) -> dict[str, Any]:
        if not self._initialized:
            self.initialize()
            self._initialized = True
        result = self._rpc(
            "tools/call",
            {"name": "guardian_status", "arguments": {}},
        )
        if not isinstance(result, dict):
            raise GuardianError("guardian_status 返回格式无效")
        data = result.get("structuredContent")
        if data is None:
            content = result.get("content") or []
            text = "\n".join(
                item.get("text", "")
                for item in content
                if isinstance(item, dict) and item.get("type") == "text"
            )
            if result.get("isError"):
                raise GuardianError(text or "guardian_status 调用失败")
            try:
                data = json.loads(text)
            except ValueError as exc:
                raise GuardianError("guardian_status 内容不是 JSON") from exc
        if result.get("isError"):
            raise GuardianError("guardian_status 调用失败")
        if not isinstance(data, dict):
            raise GuardianError("guardian_status 返回格式无效")
        return data

    def probe(self, interface_hint: str | None = None) -> GuardianProbe:
        data = self.status()
        snapshot = data.get("snapshot")
        if not isinstance(snapshot, dict):
            snapshot = data
        return select_probe(snapshot, interface_hint)


def _probe_from_interface(
    interface: dict[str, Any],
    snapshot: dict[str, Any],
    source: str,
) -> GuardianProbe:
    probe = interface.get("probe")
    if not isinstance(probe, dict):
        global_probe = snapshot.get("globalProbe")
        if (
            isinstance(global_probe, dict)
            and global_probe.get("sourceInterfaceId") == interface.get("id")
        ):
            probe = global_probe
        else:
            probe = {}
    online = probe.get("isOnline")
    return GuardianProbe(
        online=bool(online) if online is not None else None,
        stable_online=bool(probe.get("stableOnline")),
        captive_portal=bool(probe.get("captivePortalSuspected")),
        interface_name=str(interface.get("name") or ""),
        interface_id=str(interface.get("id") or ""),
        description=str(interface.get("description") or ""),
        ip=str(interface.get("primaryIpv4Address") or ""),
        gateway=str(interface.get("primaryGateway") or ""),
        mac=str(interface.get("macAddress") or ""),
        summary=str(probe.get("summary") or ""),
        source=source,
        timestamp=str(probe.get("timestampUtc") or ""),
        detail="" if probe else "该接口没有独立探测数据，回退到本机检测",
    )


def select_probe(
    snapshot: dict[str, Any], interface_hint: str | None = None
) -> GuardianProbe:
    interfaces = [
        item
        for item in snapshot.get("interfaces") or []
        if isinstance(item, dict)
    ]
    chosen: dict[str, Any] | None = None
    source = ""

    guid = npf_guid(interface_hint)
    if guid:
        chosen = next(
            (
                item
                for item in interfaces
                if _same_guid(str(item.get("adapterGuid") or ""), guid)
            ),
            None,
        )
        if chosen is not None:
            source = "认证网卡"
    if chosen is None and interface_hint:
        text = interface_hint.lower()
        chosen = next(
            (
                item
                for item in interfaces
                if text
                in (
                    str(item.get("name") or "").lower(),
                    str(item.get("description") or "").lower(),
                )
            ),
            None,
        )
        if chosen is not None:
            source = "认证网卡"
    if chosen is None:
        outlet = snapshot.get("expectedOutletInterfaceId")
        if outlet:
            chosen = next(
                (item for item in interfaces if item.get("id") == outlet),
                None,
            )
            if chosen is not None:
                source = "默认出口"
    if chosen is None:
        chosen = next(
            (item for item in interfaces if item.get("isDefaultRoute")),
            None,
        )
        if chosen is not None:
            source = "默认路由"
    if chosen is not None:
        return _probe_from_interface(chosen, snapshot, source)

    global_probe = snapshot.get("globalProbe")
    if isinstance(global_probe, dict):
        online = global_probe.get("isOnline")
        return GuardianProbe(
            online=bool(online) if online is not None else None,
            stable_online=bool(global_probe.get("stableOnline")),
            captive_portal=bool(global_probe.get("captivePortalSuspected")),
            interface_name="全局默认出口",
            summary=str(global_probe.get("summary") or ""),
            source="全局探测",
            timestamp=str(global_probe.get("timestampUtc") or ""),
        )
    return GuardianProbe(online=None, detail="网络管家没有返回探测数据")
