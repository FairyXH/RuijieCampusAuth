"""Persistent account/settings storage for the GUI.

Windows passwords are encrypted with DPAPI (CryptProtectData), so the JSON
file cannot be moved to another Windows user/machine and still be decrypted.
Other platforms fall back to base64 with 0600 file permissions and a warning;
the GUI/build target is Windows.
"""

from __future__ import annotations

import base64
import ctypes
import json
import logging
import os
import sys
import threading
import uuid
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

from .private import normalize_mac

LOG = logging.getLogger("ruijie-auth")
APP_DIR_NAME = "RuijieCampusAuth"
FILE_VERSION = 1


class _DataBlob(ctypes.Structure):
    _fields_ = [
        ("cbData", ctypes.c_uint32),
        ("pbData", ctypes.POINTER(ctypes.c_char)),
    ]


def _dpapi(data: bytes, protect: bool, machine: bool = False) -> bytes:
    if os.name != "nt":
        raise OSError("DPAPI is only available on Windows")
    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    buffer = ctypes.create_string_buffer(data, len(data))
    in_blob = _DataBlob(
        len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char))
    )
    out_blob = _DataBlob()
    if protect:
        flags = 0x01 | (0x04 if machine else 0)
        ok = crypt32.CryptProtectData(
            ctypes.byref(in_blob),
            None,
            None,
            None,
            None,
            flags,
            ctypes.byref(out_blob),
        )
    else:
        ok = crypt32.CryptUnprotectData(
            ctypes.byref(in_blob),
            None,
            None,
            None,
            None,
            0x01,
            ctypes.byref(out_blob),
        )
    if not ok:
        raise ctypes.WinError()
    try:
        return ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        kernel32.LocalFree(out_blob.pbData)


def protect_password(password: str) -> str:
    raw = password.encode("utf-8")
    if os.name == "nt":
        try:
            return "dpapi:" + base64.b64encode(_dpapi(raw, True)).decode("ascii")
        except OSError as exc:
            LOG.warning("DPAPI 加密失败，退回本机编码: %s", exc)
    return "plain:" + base64.b64encode(raw).decode("ascii")


def unprotect_password(value: str) -> str:
    if not value:
        return ""
    if value.startswith("dpapi:"):
        if os.name != "nt":
            LOG.error("accounts.json 使用 Windows DPAPI 加密，当前平台无法解密")
            return ""
        try:
            return _dpapi(base64.b64decode(value[6:]), False).decode("utf-8")
        except (OSError, ValueError) as exc:
            LOG.error("DPAPI 解密失败: %s", exc)
            return ""
    if value.startswith("plain:"):
        try:
            return base64.b64decode(value[6:]).decode("utf-8")
        except ValueError:
            return ""
    return value


def protect_password_machine(password: str) -> str:
    """Encrypt for a Windows service running as SYSTEM (LocalMachine scope)."""
    raw = password.encode("utf-8")
    if os.name != "nt":
        raise OSError("machine DPAPI is only available on Windows")
    return "machine-dpapi:" + base64.b64encode(
        _dpapi(raw, True, machine=True)
    ).decode("ascii")


def unprotect_password_machine(value: str) -> str:
    if not value:
        return ""
    prefix = "machine-dpapi:"
    if value.startswith(prefix):
        if os.name != "nt":
            raise OSError("machine DPAPI is only available on Windows")
        return _dpapi(
            base64.b64decode(value[len(prefix) :]), False
        ).decode("utf-8")
    return unprotect_password(value)


def default_data_dir() -> Path:
    if getattr(sys, "frozen", False):
        return install_dir()
    if os.name == "nt":
        base = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return base / APP_DIR_NAME


def install_dir() -> Path:
    """Machine-wide state lives next to the installed exe, not ProgramData.

    The deployment path is D:\\Storageredirect\\Program\\RuijieCampusAuth,
    which is outside the frozen C: volume.
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[2]


def program_data_dir() -> Path:
    """Legacy machine-wide directory; runtime state uses install_dir()."""
    if os.name == "nt":
        base = Path(os.environ.get("ProgramData", r"C:\ProgramData"))
    else:
        base = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return base / APP_DIR_NAME


@dataclass
class Account:
    id: str = field(default_factory=lambda: uuid.uuid4().hex)
    username: str = ""
    password: str = ""
    note: str = ""
    enabled: bool = True


@dataclass
class AppSettings:
    active_account_id: str | None = None
    interface: str = ""
    preempt: bool = True
    auto_start: bool = False
    minimize_to_tray: bool = True
    start_minimized: bool = False
    check_interval: float = 2.0
    retry_interval: float = 3.0
    timeout: float = 20.0
    retries: int = 3
    repeats: int = 3
    connectivity_url: str = "http://www.baidu.com/"
    verbose: bool = False
    logoff_on_stop: bool = False
    block_official_client: bool = False
    server_lock: bool = False
    locked_server: str = ""
    guardian_enabled: bool = False
    guardian_url: str = "http://127.0.0.1:18765/mcp"
    guardian_interval: float = 5.0
    guardian_failures: int = 2
    service_name: str = "电信宽带服务"
    static_id: str = "Static:AB45A862"
    start_timeout: float = 3.0
    dhcp_mode: str = "before"
    dhcp_timeout: float = 30.0
    dhcp_option82: bool = False
    school_code: str = ""

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "AppSettings":
        settings = cls()
        for field_name in settings.__dataclass_fields__:
            if field_name in data:
                setattr(settings, field_name, data[field_name])
        settings.check_interval = max(0.5, float(settings.check_interval))
        settings.retry_interval = max(0.5, float(settings.retry_interval))
        settings.timeout = max(5.0, float(settings.timeout))
        settings.retries = max(1, int(settings.retries))
        settings.repeats = max(1, int(settings.repeats))
        settings.locked_server = normalize_mac(settings.locked_server)
        settings.guardian_url = str(settings.guardian_url or "").strip()
        settings.guardian_interval = min(
            60.0, max(1.0, float(settings.guardian_interval))
        )
        settings.guardian_failures = min(
            10, max(1, int(settings.guardian_failures))
        )
        settings.service_name = (
            str(settings.service_name or "").strip() or "电信宽带服务"
        )
        settings.static_id = (
            str(settings.static_id or "").strip() or "Static:AB45A862"
        )
        settings.start_timeout = min(
            30.0, max(0.5, float(settings.start_timeout))
        )
        if settings.dhcp_mode not in ("before", "after", "none"):
            settings.dhcp_mode = "before"
        settings.dhcp_timeout = min(
            300.0, max(5.0, float(settings.dhcp_timeout))
        )
        settings.school_code = str(settings.school_code or "").strip()
        return settings

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class DiscoveryInfo:
    interface: str
    description: str = ""
    mac: str = ""
    server_mac: str = ""
    updated_at: str = ""


class DiscoveryCache:
    """Remember the working interface/authenticator between attempts."""

    def __init__(self, path: Path) -> None:
        self.path = Path(path)
        self._lock = threading.Lock()

    def load(self) -> DiscoveryInfo | None:
        with self._lock:
            if not self.path.exists():
                return None
            try:
                data = json.loads(self.path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                return None
            interface = str(data.get("interface", "") or "")
            if not interface:
                return None
            return DiscoveryInfo(
                interface=interface,
                description=str(data.get("description", "") or ""),
                mac=str(data.get("mac", "") or ""),
                server_mac=str(data.get("server_mac", "") or ""),
                updated_at=str(data.get("updated_at", "") or ""),
            )

    def save(
        self,
        interface: str,
        description: str = "",
        mac: str = "",
        server_mac: str = "",
    ) -> None:
        payload = {
            "version": FILE_VERSION,
            "interface": interface,
            "description": description,
            "mac": mac,
            "server_mac": server_mac,
            "updated_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        }
        with self._lock:
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                temp = self.path.with_suffix(self.path.suffix + ".tmp")
                temp.write_text(
                    json.dumps(payload, ensure_ascii=False, indent=2),
                    encoding="utf-8",
                )
                os.replace(temp, self.path)
            except OSError as exc:
                LOG.warning("写入认证通道缓存失败: %s", exc)

    def clear(self) -> None:
        with self._lock:
            try:
                self.path.unlink()
            except FileNotFoundError:
                pass
            except OSError as exc:
                LOG.warning("清除认证通道缓存失败: %s", exc)


@dataclass
class ServerRecord:
    mac: str
    name: str = ""
    note: str = ""
    interface: str = ""
    description: str = ""
    first_seen: str = ""
    last_seen: str = ""
    last_success: str = ""
    success_count: int = 0

    @property
    def label(self) -> str:
        return self.name or self.description or self.mac


class ServerStore:
    """Authenticator list learned from live EAP responses.

    Every server that answers EAPOL is recorded; a successful EAP-MD5 exchange
    also increments ``success_count``. The GUI can lock one MAC so EAPOL-Start
    and Logoff are sent unicast to that authenticator instead of the group MAC.
    """

    def __init__(self, path: Path) -> None:
        self.path = Path(path)
        self._lock = threading.Lock()

    def _load_unlocked(self) -> list[ServerRecord]:
        if not self.path.exists():
            return []
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        records: list[ServerRecord] = []
        seen: set[str] = set()
        for item in data.get("servers", []):
            if not isinstance(item, dict):
                continue
            mac = normalize_mac(str(item.get("mac", "")))
            if not mac or mac in seen:
                continue
            seen.add(mac)
            records.append(
                ServerRecord(
                    mac=mac,
                    name=str(item.get("name", "") or ""),
                    note=str(item.get("note", "") or ""),
                    interface=str(item.get("interface", "") or ""),
                    description=str(item.get("description", "") or ""),
                    first_seen=str(item.get("first_seen", "") or ""),
                    last_seen=str(item.get("last_seen", "") or ""),
                    last_success=str(item.get("last_success", "") or ""),
                    success_count=max(0, int(item.get("success_count", 0) or 0)),
                )
            )
        return records

    def _save_unlocked(self, records: list[ServerRecord]) -> None:
        payload = {
            "version": FILE_VERSION,
            "servers": [asdict(record) for record in records],
        }
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(self.path.suffix + ".tmp")
        temp.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        os.replace(temp, self.path)

    def load(self) -> list[ServerRecord]:
        with self._lock:
            return self._load_unlocked()

    def save(self, records: list[ServerRecord]) -> None:
        with self._lock:
            self._save_unlocked(records)

    def get(self, mac: str) -> ServerRecord | None:
        target = normalize_mac(mac)
        if not target:
            return None
        return next((item for item in self.load() if item.mac == target), None)

    def upsert(
        self,
        mac: str,
        *,
        name: str | None = None,
        note: str | None = None,
        interface: str | None = None,
        description: str | None = None,
        success: bool = False,
        seen_at: str | None = None,
    ) -> ServerRecord | None:
        target = normalize_mac(mac)
        if not target:
            return None
        stamp = seen_at or datetime.now().astimezone().isoformat(
            timespec="seconds"
        )
        with self._lock:
            records = self._load_unlocked()
            record = next((item for item in records if item.mac == target), None)
            if record is None:
                record = ServerRecord(mac=target, first_seen=stamp)
                records.append(record)
            if not record.first_seen or stamp < record.first_seen:
                record.first_seen = stamp
            if stamp > (record.last_seen or ""):
                record.last_seen = stamp
            if name is not None:
                record.name = name
            if note is not None:
                record.note = note
            if interface:
                record.interface = interface
            if description:
                record.description = description
            if success:
                record.success_count += 1
                if stamp > (record.last_success or ""):
                    record.last_success = stamp
            self._save_unlocked(records)
        return record

    def rename(self, mac: str, name: str) -> bool:
        target = normalize_mac(mac)
        if not target:
            return False
        with self._lock:
            records = self._load_unlocked()
            record = next((item for item in records if item.mac == target), None)
            if record is None:
                return False
            record.name = name.strip()
            self._save_unlocked(records)
            return True

    def remove(self, mac: str) -> bool:
        target = normalize_mac(mac)
        if not target:
            return False
        with self._lock:
            records = self._load_unlocked()
            kept = [item for item in records if item.mac != target]
            if len(kept) == len(records):
                return False
            self._save_unlocked(kept)
            return True

    def import_discovery(self, path: Path) -> ServerRecord | None:
        """Seed the list from discovery.json without overwriting known servers."""
        try:
            data = json.loads(Path(path).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        mac = normalize_mac(str(data.get("server_mac", "")))
        if not mac:
            return None
        stamp = str(data.get("updated_at", "") or "")
        with self._lock:
            records = self._load_unlocked()
            existing = next((item for item in records if item.mac == mac), None)
            if existing is not None:
                return existing
            record = ServerRecord(
                mac=mac,
                interface=str(data.get("interface", "") or ""),
                description=str(data.get("description", "") or ""),
                first_seen=stamp,
                last_seen=stamp,
            )
            records.append(record)
            self._save_unlocked(records)
            return record


class AccountStore:
    def __init__(self, base_dir: Path | None = None) -> None:
        self.base_dir = Path(base_dir) if base_dir else default_data_dir()
        self.accounts_path = self.base_dir / "accounts.json"
        self.settings_path = self.base_dir / "settings.json"
        self.logs_dir = self.base_dir / "logs"
        self.base_dir.mkdir(parents=True, exist_ok=True)
        self.logs_dir.mkdir(parents=True, exist_ok=True)

    def _write_json(self, path: Path, payload: dict[str, Any]) -> None:
        temp = path.with_suffix(path.suffix + ".tmp")
        temp.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        os.replace(temp, path)
        if os.name != "nt":
            try:
                os.chmod(path, 0o600)
            except OSError:
                pass

    def load_accounts(self) -> list[Account]:
        if not self.accounts_path.exists():
            return []
        try:
            data = json.loads(self.accounts_path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            LOG.error("读取账号文件失败: %s", exc)
            return []
        accounts: list[Account] = []
        for item in data.get("accounts", []):
            if not isinstance(item, dict) or not item.get("username"):
                continue
            accounts.append(
                Account(
                    id=str(item.get("id") or uuid.uuid4().hex),
                    username=str(item["username"]),
                    password=unprotect_password(str(item.get("password_enc", ""))),
                    note=str(item.get("note", "")),
                    enabled=bool(item.get("enabled", True)),
                )
            )
        return accounts

    def save_accounts(self, accounts: list[Account]) -> None:
        payload = {
            "version": FILE_VERSION,
            "accounts": [
                {
                    "id": account.id,
                    "username": account.username,
                    "password_enc": protect_password(account.password),
                    "note": account.note,
                    "enabled": account.enabled,
                }
                for account in accounts
            ],
        }
        self._write_json(self.accounts_path, payload)

    def load_settings(self) -> AppSettings:
        if not self.settings_path.exists():
            return AppSettings()
        try:
            data = json.loads(self.settings_path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            LOG.error("读取设置失败: %s", exc)
            return AppSettings()
        return AppSettings.from_dict(data)

    def save_settings(self, settings: AppSettings) -> None:
        self._write_json(
            self.settings_path,
            {"version": FILE_VERSION, **settings.to_dict()},
        )
