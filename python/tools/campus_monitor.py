#!/usr/bin/env python3
"""Sample campus-network state every N minutes.

Passive monitor: it never sends EAPOL frames and never touches the running
supplicant. It records the USB NIC link/IP state, source-bound egress probes,
the GUI process state, takeover/IFEO state, and the authentication events that
the GUI writes to app.log.

Outputs (under %APPDATA%\\RuijieCampusAuth by default):
  logs/campus_status.jsonl   one JSON snapshot per line
  campus_status.json         compact state, including recovery timestamps

Usage:
  python tools/campus_monitor.py --once
  python tools/campus_monitor.py --interval 15 --until 09:00
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import math
import os
import re
import socket
import ssl
import struct
import subprocess
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path

APP_DIR_NAME = "RuijieCampusAuth"
ADAPTER_DESC = "Realtek USB GbE Family Controller"
LOG_FILES = ("app.log.3", "app.log.2", "app.log.1", "app.log")
LINE_RE = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(\w+)\] (.*)$")

PS_ADAPTER = r"""
$ErrorActionPreference = 'SilentlyContinue'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$n = Get-NetAdapter | Where-Object { $_.InterfaceDescription -eq 'Realtek USB GbE Family Controller' } | Select-Object -First 1
if (-not $n) { '{"present":false}' | Write-Output; exit 0 }
$ip = Get-NetIPAddress -InterfaceIndex $n.ifIndex -AddressFamily IPv4 | Select-Object -First 1
$gw = Get-NetRoute -InterfaceIndex $n.ifIndex -DestinationPrefix '0.0.0.0/0' | Select-Object -First 1
[pscustomobject]@{
  present = $true
  name = $n.Name
  status = [string]$n.Status
  mac = $n.MacAddress
  speed = [string]$n.LinkSpeed
  ip = $ip.IPAddress
  prefix = $ip.PrefixLength
  gateway = $gw.NextHop
  ifIndex = $n.ifIndex
} | ConvertTo-Json -Compress
"""


def emit(message: str) -> None:
    """pythonw has no stdout; never let logging kill the sampler."""
    try:
        print(message, flush=True)
    except Exception:
        pass


def now_text() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def iso_now() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")


def data_dir() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
    return Path(base) / APP_DIR_NAME


def decode(raw: bytes) -> str:
    for encoding in ("utf-8-sig", "mbcs"):
        try:
            return raw.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", "replace")


def run(cmd: list[str], timeout: float = 20.0) -> tuple[int, str]:
    try:
        proc = subprocess.run(
            cmd, capture_output=True, timeout=timeout, creationflags=0
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return -1, str(exc)
    return proc.returncode, decode(proc.stdout) + decode(proc.stderr)


def read_json(path: Path) -> dict | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    os.replace(temp, path)


def adapter_state() -> dict:
    code, output = run(
        [
            "powershell",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            PS_ADAPTER,
        ],
        timeout=20.0,
    )
    if code != 0:
        return {"present": None, "error": output.strip()[:400]}
    lines = [line for line in output.splitlines() if line.strip()]
    if not lines:
        return {"present": None, "error": "no output"}
    try:
        payload = json.loads(lines[-1])
    except ValueError:
        return {"present": None, "error": output.strip()[:400]}
    if payload.get("present") and not payload.get("ip"):
        payload["ip"] = None
    return payload


def process_state() -> dict:
    code, output = run(["tasklist", "/FO", "CSV", "/NH"], timeout=20.0)
    wanted = {
        "ruijiecampusauth.exe": "RuijieCampusAuth",
        "8021x.exe": "8021x",
        "ruijiesupplicant.exe": "RuijieSupplicant",
        "rjsuservice.exe": "RJSuService",
    }
    found: dict[str, list[dict[str, str]]] = {name: [] for name in wanted.values()}
    if code != 0:
        return {"error": output.strip()[:300]}
    for row in csv.reader(io.StringIO(output)):
        if len(row) < 2:
            continue
        key = row[0].strip().lower()
        if key in wanted:
            found[wanted[key]].append({"pid": row[1].strip()})
    return found


def _resolve(host: str, port: int) -> str:
    infos = socket.getaddrinfo(host, port, socket.AF_INET, socket.SOCK_STREAM)
    return infos[0][4][0]


# Windows IP_UNICAST_IF. Binding a source address alone still lets the route
# table pick the WLAN default route, so probes must pin the egress interface.
IP_UNICAST_IF = 31


def force_interface(sock: socket.socket, ifindex: int | None) -> bool:
    if os.name != "nt" or not ifindex:
        return False
    try:
        sock.setsockopt(
            socket.IPPROTO_IP, IP_UNICAST_IF, struct.pack("!I", int(ifindex))
        )
        return True
    except OSError:
        return False


def tcp_probe(
    source_ip: str, ifindex: int | None, host: str, port: int, timeout: float
) -> dict:
    started = time.monotonic()
    try:
        destination = _resolve(host, port)
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(timeout)
        try:
            forced = force_interface(sock, ifindex)
            sock.bind((source_ip, 0))
            sock.connect((destination, port))
        finally:
            sock.close()
        return {
            "ok": True,
            "ms": round((time.monotonic() - started) * 1000),
            "target": f"{host}:{port}",
            "destination": destination,
            "forced": forced,
        }
    except OSError as exc:
        return {
            "ok": False,
            "ms": round((time.monotonic() - started) * 1000),
            "target": f"{host}:{port}",
            "error": str(exc)[:200],
        }


def tls_probe(
    source_ip: str, ifindex: int | None, host: str, port: int, timeout: float
) -> dict:
    started = time.monotonic()
    context = ssl.create_default_context()
    try:
        destination = _resolve(host, port)
        raw = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        raw.settimeout(timeout)
        try:
            forced = force_interface(raw, ifindex)
            raw.bind((source_ip, 0))
            raw.connect((destination, port))
            with context.wrap_socket(raw, server_hostname=host) as tls:
                peer = tls.getpeercert().get("subject", [])
        finally:
            raw.close()
        return {
            "ok": True,
            "ms": round((time.monotonic() - started) * 1000),
            "target": f"{host}:{port}",
            "destination": destination,
            "peer": str(peer)[:160],
            "forced": forced,
        }
    except (OSError, ssl.SSLError, ValueError) as exc:
        return {
            "ok": False,
            "ms": round((time.monotonic() - started) * 1000),
            "target": f"{host}:{port}",
            "error": str(exc)[:200],
        }


def http_probe(
    source_ip: str, ifindex: int | None, host: str, timeout: float
) -> dict:
    """Small GET used only to identify a captive portal when TLS fails."""
    started = time.monotonic()
    try:
        destination = _resolve(host, 80)
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(timeout)
        try:
            force_interface(sock, ifindex)
            sock.bind((source_ip, 0))
            sock.connect((destination, 80))
            request = (
                f"GET / HTTP/1.1\r\nHost: {host}\r\n"
                "User-Agent: campus-monitor/1.0\r\nConnection: close\r\n\r\n"
            )
            sock.sendall(request.encode("ascii"))
            data = b""
            while len(data) < 8192:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                data += chunk
        finally:
            sock.close()
        head, _, body = data.partition(b"\r\n\r\n")
        lines = head.decode("latin-1", "replace").split("\r\n")
        status = lines[0] if lines else ""
        location = ""
        for line in lines[1:]:
            if line.lower().startswith("location:"):
                location = line.split(":", 1)[1].strip()
        return {
            "ok": status.startswith("HTTP/1.1 200") or status.startswith("HTTP/1.0 200"),
            "ms": round((time.monotonic() - started) * 1000),
            "status": status[:120],
            "location": location[:200],
            "body_len": len(body),
        }
    except OSError as exc:
        return {
            "ok": False,
            "ms": round((time.monotonic() - started) * 1000),
            "error": str(exc)[:200],
        }


def connectivity_state(source_ip: str | None, ifindex: int | None) -> dict:
    if not source_ip:
        return {"online_via_usb": False, "error": "USB NIC has no IPv4 address"}
    dns_tcp = tcp_probe(source_ip, ifindex, "223.5.5.5", 53, 4.0)
    tls_web = tls_probe(source_ip, ifindex, "www.baidu.com", 443, 6.0)
    http_web = (
        None if tls_web["ok"] else http_probe(source_ip, ifindex, "www.baidu.com", 5.0)
    )
    return {
        "online_via_usb": bool(tls_web["ok"] or dns_tcp["ok"]),
        "internet_via_usb": bool(tls_web["ok"]),
        "tcp_dns": dns_tcp,
        "tls_baidu": tls_web,
        "http_baidu": http_web,
    }


def scan_app_log(log_dir: Path, since: str) -> dict:
    response_times: list[str] = []
    success_times: list[str] = []
    window_times: list[str] = []
    last_event: dict | None = None
    last_server_message: dict | None = None

    for name in LOG_FILES:
        path = log_dir / name
        if not path.exists():
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line in text.splitlines():
            match = LINE_RE.match(line)
            if not match:
                continue
            stamp, level, message = match.groups()
            if stamp < since:
                continue
            if last_event is None or stamp >= last_event["time"]:
                last_event = {"time": stamp, "level": level, "message": message[:300]}
            if message == "认证成功":
                success_times.append(stamp)
                response_times.append(stamp)
            elif message.startswith("服务器消息: "):
                text_message = message[len("服务器消息: ") :]
                response_times.append(stamp)
                last_server_message = {"time": stamp, "message": text_message[:300]}
                if "不在认证时段内" in text_message:
                    window_times.append(stamp)
            elif message.startswith("已连接认证服务器"):
                response_times.append(stamp)

    return {
        "responses": response_times,
        "successes": success_times,
        "windows": window_times,
        "last_event": last_event,
        "last_server_message": last_server_message,
    }


def read_takeover(path: Path) -> dict | None:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    payload: dict[str, str] = {}
    for line in text.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            payload[key.strip()] = value.strip()
    return payload or None


def ifeo_state() -> dict:
    key = r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\8021x.exe"
    code, output = run(["reg", "query", key], timeout=10.0)
    return {
        "present": code == 0,
        "marker": "HXXYTakeover" in output,
        "debugger": "Debugger" in output,
    }


def take_snapshot(state: dict) -> dict:
    base = data_dir()
    logs_dir = base / "logs"
    started_at = state["started_at"]
    adapter = adapter_state()
    source_ip = adapter.get("ip") if adapter.get("present") else None
    ifindex = adapter.get("ifIndex") if adapter.get("present") else None
    events = scan_app_log(logs_dir, started_at)

    if events["responses"] and not state.get("first_server_response_at"):
        state["first_server_response_at"] = min(events["responses"])
    if events["successes"] and not state.get("first_success_at"):
        state["first_success_at"] = min(events["successes"])
    if events["windows"]:
        latest_window = max(events["windows"])
        if not state.get("last_window_at") or latest_window > state["last_window_at"]:
            state["last_window_at"] = latest_window

    window_at = state.get("last_window_at")
    if window_at:
        after = [stamp for stamp in events["responses"] if stamp > window_at]
        if after and not state.get("first_response_after_window_at"):
            state["first_response_after_window_at"] = min(after)

    if events["last_event"]:
        state["last_event"] = events["last_event"]
    if events["last_server_message"]:
        state["last_server_message"] = events["last_server_message"]

    state["snapshot_count"] = int(state.get("snapshot_count", 0)) + 1
    state["last_snapshot_at"] = iso_now()

    snapshot = {
        "time": iso_now(),
        "adapter": adapter,
        "processes": process_state(),
        "connectivity": connectivity_state(source_ip, ifindex),
        "app_log": {
            "last_event": events["last_event"],
            "last_server_message": events["last_server_message"],
            "first_server_response_at": state.get("first_server_response_at"),
            "first_success_at": state.get("first_success_at"),
            "last_window_at": state.get("last_window_at"),
            "first_response_after_window_at": state.get(
                "first_response_after_window_at"
            ),
        },
        "discovery": read_json(base / "discovery.json"),
        "takeover": read_takeover(
            Path(os.environ.get("ProgramData", r"C:\ProgramData"))
            / APP_DIR_NAME
            / "takeover.ini"
        ),
        "ifeo": ifeo_state(),
        "monitor": {
            "started_at": started_at,
            "snapshot_count": state["snapshot_count"],
        },
    }

    return snapshot


def persist_snapshot(snapshot: dict, state: dict) -> None:
    base = data_dir()
    logs_dir = base / "logs"
    logs_dir.mkdir(parents=True, exist_ok=True)
    with (logs_dir / "campus_status.jsonl").open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(snapshot, ensure_ascii=False) + "\n")
    write_json(base / "campus_status.json", state)


def next_boundary(after: float, interval_seconds: int) -> float:
    return (math.floor(after / interval_seconds) + 1) * interval_seconds


def parse_until(value: str) -> float:
    hour, minute = (int(part) for part in value.split(":", 1))
    target = datetime.now().replace(
        hour=hour, minute=minute, second=0, microsecond=0
    )
    if target.timestamp() <= time.time():
        target += timedelta(days=1)
    return target.timestamp()


def sleep_until(deadline: float) -> None:
    while True:
        remaining = deadline - time.time()
        if remaining <= 0:
            return
        time.sleep(min(remaining, 30.0))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="校园网状态定时采样器")
    parser.add_argument("--interval", type=int, default=15, help="采样间隔（分钟）")
    parser.add_argument("--until", help="停止时间 HH:MM（默认一直运行）")
    parser.add_argument("--once", action="store_true", help="立即采样一次后退出")
    parser.add_argument("--reset", action="store_true", help="清空累计状态")
    args = parser.parse_args(argv)

    if args.interval < 1:
        parser.error("--interval 必须 >= 1")

    base = data_dir()
    state_path = base / "campus_status.json"
    state = None if args.reset else read_json(state_path)
    if not state or "started_at" not in state:
        state = {"started_at": now_text(), "snapshot_count": 0}
        write_json(state_path, state)

    if args.once:
        snapshot = take_snapshot(state)
        persist_snapshot(snapshot, state)
        print(json.dumps(snapshot, ensure_ascii=False, indent=2))
        return 0

    deadline = parse_until(args.until) if args.until else None
    interval_seconds = args.interval * 60
    while True:
        boundary = next_boundary(time.time(), interval_seconds)
        if deadline and boundary > deadline:
            break
        try:
            sleep_until(boundary)
        except KeyboardInterrupt:
            break
        try:
            snapshot = take_snapshot(state)
            persist_snapshot(snapshot, state)
            event = snapshot.get("app_log", {}).get("last_event") or {}
            emit(
                f"{snapshot['time']} snapshot#{state['snapshot_count']} "
                f"usb={snapshot['connectivity'].get('online_via_usb')} "
                f"event={event.get('message', '')[:80]}"
            )
        except Exception as exc:  # keep the sampler alive across probe failures
            error = {"time": iso_now(), "error": f"{type(exc).__name__}: {exc}"}
            logs_dir = base / "logs"
            logs_dir.mkdir(parents=True, exist_ok=True)
            with (logs_dir / "campus_status.jsonl").open(
                "a", encoding="utf-8"
            ) as handle:
                handle.write(json.dumps(error, ensure_ascii=False) + "\n")
            emit(json.dumps(error, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
