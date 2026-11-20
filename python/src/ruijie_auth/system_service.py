"""Windows system service for boot-time campus authentication.

The service runs as LocalSystem before any user logs in. It reads a
machine-scoped config from %ProgramData%, authenticates with the normal
supplicant, and keeps the session alive. The GUI talks to it through sc.exe
and a status JSON file; the service never imports Qt.
"""

from __future__ import annotations

import ctypes
import json
import logging
import os
import re
import socket
import subprocess
import sys
import threading
import time
from ctypes import wintypes as wt
from datetime import datetime
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

from .guardian import GuardianClient, GuardianError
from .storage import (
    Account,
    AppSettings,
    DiscoveryCache,
    ServerStore,
    protect_password_machine,
    unprotect_password_machine,
)

LOG = logging.getLogger("ruijie-auth")

SERVICE_NAME = "RuijieCampusAuth"
SERVICE_DISPLAY = "Ruijie Campus Auth"
SERVICE_DESCRIPTION = "Ruijie-compatible campus network supplicant"
CONFIG_FILE = "service.json"
STATUS_FILE = "service_status.json"
LOG_FILE = "service.log"

SERVICE_WIN32_OWN_PROCESS = 0x10
SERVICE_AUTO_START = 0x02
SERVICE_ERROR_NORMAL = 0x01
SERVICE_NO_CHANGE = 0xFFFFFFFF
SC_MANAGER_ALL_ACCESS = 0xF003F
SERVICE_ALL_ACCESS = 0xF01FF

SERVICE_STOPPED = 1
SERVICE_START_PENDING = 2
SERVICE_STOP_PENDING = 3
SERVICE_RUNNING = 4
SERVICE_ACCEPT_STOP = 0x01
SERVICE_ACCEPT_SHUTDOWN = 0x04
ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063


def _iso() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")


def service_root() -> Path:
    override = os.environ.get("RUIJIE_SERVICE_DIR", "").strip()
    if override:
        return Path(override)
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[2] / "RuijieCampusAuth"


def ensure_service_dir() -> Path:
    root = service_root()
    root.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        try:
            subprocess.run(
                [
                    "icacls",
                    str(root),
                    "/inheritance:r",
                    "/grant:r",
                    "*S-1-5-18:(OI)(CI)(F)",
                    "*S-1-5-32-544:(OI)(CI)(F)",
                    "*S-1-5-32-545:(OI)(CI)(RX)",
                ],
                capture_output=True,
                timeout=20,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            LOG.warning("设置服务目录 ACL 失败: %s", exc)
    return root


def service_config_path() -> Path:
    return service_root() / CONFIG_FILE


def service_status_path() -> Path:
    return service_root() / STATUS_FILE


def service_log_path() -> Path:
    return service_root() / "logs" / LOG_FILE


def service_executable_path() -> Path:
    """The dedicated onedir service host, not the onefile GUI."""
    return service_root() / "RuijieService.exe"


def service_host_path() -> Path:
    """Locate the executable the SCM should start.

    The service needs a real host process: the frozen onedir build
    (dist/RuijieService/RuijieService.exe) or, when already frozen, the
    running executable. A plain ``python.exe`` cannot be registered as a
    service because CreateService takes a single image path.
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve()
    candidates = [
        Path(__file__).resolve().parents[2]
        / "dist"
        / "RuijieService"
        / "RuijieService.exe",
        install_dir() / "RuijieService.exe",
        service_executable_path(),
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    raise RuntimeError(
        "未找到 RuijieService.exe：请先运行 python/build.ps1 构建服务宿主，"
        "或改用 C# 版的 service install"
    )


def _decode(raw: bytes) -> str:
    for encoding in ("utf-8-sig", "mbcs"):
        try:
            return raw.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", "replace")


def _run_sc(arguments: list[str]) -> tuple[int, str]:
    try:
        proc = subprocess.run(
            ["sc.exe", *arguments],
            capture_output=True,
            timeout=20,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return -1, str(exc)
    return proc.returncode, _decode(proc.stdout) + _decode(proc.stderr)


def _restrict_acl(path: Path) -> None:
    if os.name != "nt":
        return
    try:
        subprocess.run(
            [
                "icacls",
                str(path),
                "/inheritance:r",
                "/grant:r",
                "*S-1-5-18:(F)",
                "*S-1-5-32-544:(F)",
            ],
            capture_output=True,
            timeout=20,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        LOG.warning("设置服务配置 ACL 失败: %s", exc)


def write_service_config(account: Account, settings: AppSettings) -> Path:
    payload = {
        "version": 1,
        "updated_at": _iso(),
        "account": {
            "username": account.username,
            "password_enc": protect_password_machine(account.password),
        },
        "settings": settings.to_dict(),
    }
    ensure_service_dir()
    path = service_config_path()
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    os.replace(temp, path)
    _restrict_acl(path)
    return path


def load_service_config() -> tuple[Account, AppSettings]:
    data = json.loads(service_config_path().read_text(encoding="utf-8"))
    account_data = data.get("account") or {}
    username = str(account_data.get("username", "") or "")
    password = unprotect_password_machine(
        str(account_data.get("password_enc", "") or "")
    )
    if not username or not password:
        raise ValueError("系统服务配置缺少账号或密码")
    settings = AppSettings.from_dict(data.get("settings") or {})
    return Account(username=username, password=password), settings


def remove_service_config() -> None:
    try:
        service_config_path().unlink()
    except FileNotFoundError:
        pass


def write_service_status(state: str, message: str, **extra) -> None:
    payload = {"time": _iso(), "state": state, "message": message, **extra}
    ensure_service_dir()
    path = service_status_path()
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    os.replace(temp, path)


def read_service_status() -> dict | None:
    try:
        return json.loads(service_status_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _boot_log(message: str) -> None:
    try:
        path = service_root() / "service_boot.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as handle:
            handle.write(f"{_iso()} {message}\n")
    except Exception:
        pass


def _setup_service_logging(path: Path) -> None:
    LOG.setLevel(logging.DEBUG)
    if any(getattr(handler, "_ruijie_service", False) for handler in LOG.handlers):
        return
    ensure_service_dir()
    path.parent.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(
        path, maxBytes=2 * 1024 * 1024, backupCount=3, encoding="utf-8"
    )
    handler.setFormatter(
        logging.Formatter(
            "%(asctime)s [%(levelname)s] %(message)s", "%Y-%m-%d %H:%M:%S"
        )
    )
    handler._ruijie_service = True  # type: ignore[attr-defined]
    LOG.addHandler(handler)


def _tcp_online(settings: AppSettings) -> bool:
    url = settings.connectivity_url.strip()
    if not url:
        return True
    parsed = urlsplit(url)
    host = parsed.hostname
    if not host or host in ("localhost", "127.0.0.1", "::1"):
        return True
    port = parsed.port or (443 if parsed.scheme == "https" else 80)
    try:
        with socket.create_connection((host, port), timeout=3):
            return True
    except OSError:
        return False


class SystemAgent:
    """Headless preempt authenticator used by the Windows service."""

    def __init__(
        self,
        account: Account,
        settings: AppSettings,
        stop_event: threading.Event,
    ) -> None:
        self.account = account
        self.settings = settings
        self.stop_event = stop_event
        self.discovery = DiscoveryCache(
            service_root() / "discovery_service.json"
        )
        self.servers = ServerStore(service_root() / "servers_service.json")
        self._resolved: str | None = None
        self._supp: Any | None = None
        self._guardian_next = 0.0
        self._guardian_decision: bool | None = None
        self._guardian_fail_streak = 0
        self._guardian_last_state: bool | None = None
        self._guardian_warned_at = 0.0
        self.guardian_client: GuardianClient | None = None
        if settings.guardian_enabled:
            try:
                self.guardian_client = GuardianClient(
                    settings.guardian_url,
                    timeout=min(8.0, max(3.0, settings.timeout / 2)),
                )
            except ValueError as exc:
                LOG.warning("网络管家地址无效，系统服务不启用联动: %s", exc)

    def _status(self, state: str, message: str, **extra) -> None:
        try:
            write_service_status(
                state,
                message,
                interface=self._resolved or "",
                **extra,
            )
        except OSError as exc:
            LOG.warning("写入服务状态失败: %s", exc)

    def _stop_watcher(self, supp: Any) -> None:
        self.stop_event.wait()
        supp.stop()

    def _logon(self) -> bool:
        # Keep scapy out of the service import path so SCM sees RUNNING fast.
        from .cli import Supplicant, choose_interface, list_interfaces

        requested = self.settings.interface or None
        if self._resolved is None:
            if requested:
                self._status("selecting", "正在解析指定网卡")
                self._resolved = choose_interface(
                    requested, stop_event=self.stop_event
                )
            else:
                cached = self.discovery.load()
                valid = False
                if cached is not None:
                    keys = {item.key for item in list_interfaces()}
                    if cached.interface in keys:
                        self._resolved = cached.interface
                        valid = True
                if not valid:
                    self._status("selecting", "正在探测认证网卡")
                    self._resolved = choose_interface(
                        None, stop_event=self.stop_event
                    )
            LOG.info("系统服务使用网卡: %s", self._resolved)

        locked = (
            self.settings.locked_server
            if self.settings.server_lock
            else None
        )
        self._status("authenticating", f"正在认证 {self.account.username}")
        self._run_dhcp_phase("before")
        supp = Supplicant(
            interface=self._resolved,
            identity=self.account.username.encode("gb18030"),
            password=self.account.password.encode("gb18030"),
            timeout=self.settings.timeout,
            auth_mode=self.settings.auth_mode,
            retries=self.settings.retries,
            repeats=self.settings.repeats,
            private=True,
            logoff_first=True,
            locked_server=locked,
            service_name=self.settings.service_name,
            static_id=self.settings.static_id,
            start_timeout=self.settings.start_timeout,
        )
        self._supp = supp
        watcher = threading.Thread(
            target=self._stop_watcher, args=(supp,), daemon=True
        )
        watcher.start()
        try:
            ok = supp.authenticate()
        finally:
            self._supp = None

        if supp.connected:
            try:
                from scapy.interfaces import resolve_iface

                info = resolve_iface(self._resolved)
                description = str(getattr(info, "description", info.name))
                mac = str(getattr(info, "mac", "") or "")
            except Exception:  # noqa: BLE001 - best effort metadata
                description = self._resolved
                mac = ""
            self.discovery.save(
                self._resolved,
                description=description,
                mac=mac,
                server_mac=supp.server_mac or "",
            )
            record = self.servers.upsert(
                supp.server_mac or "",
                interface=self._resolved,
                description=description,
                success=ok,
            )
            if record is not None and ok:
                LOG.info(
                    "系统服务认证服务器: %s | %s | 成功 %d 次",
                    record.mac,
                    record.label,
                    record.success_count,
                )
        else:
            self.discovery.clear()
            if not requested:
                self._resolved = None
        return ok

    def _guardian_online(self) -> bool | None:
        client = self.guardian_client
        if client is None:
            return None
        now = time.monotonic()
        interval = max(1.0, float(self.settings.guardian_interval))
        if now < self._guardian_next and self._guardian_decision is not None:
            return self._guardian_decision
        self._guardian_next = now + interval
        try:
            probe = client.probe(self._resolved)
        except (GuardianError, OSError, ValueError) as exc:
            self._guardian_decision = None
            self._guardian_fail_streak = 0
            if now - self._guardian_warned_at >= 60:
                self._guardian_warned_at = now
                LOG.warning("系统服务查询网络管家失败，回退本机探测: %s", exc)
            return None
        if probe.online is None:
            self._guardian_fail_streak = 0
            self._guardian_decision = None
            if self._guardian_last_state is not False:
                self._guardian_last_state = False
                LOG.info("系统服务网络管家: %s", probe.short())
            return None
        if probe.online and not probe.captive_portal:
            self._guardian_fail_streak = 0
            self._guardian_decision = True
            if self._guardian_last_state is not True:
                self._guardian_last_state = True
                LOG.info("系统服务网络管家: %s", probe.short())
                self._status("authenticated", f"在线 | {probe.short()}")
            return True
        self._guardian_fail_streak += 1
        threshold = max(1, int(self.settings.guardian_failures))
        if self._guardian_fail_streak in (1, threshold):
            LOG.warning(
                "系统服务网络管家: %s | 连续异常 %d/%d",
                probe.short(),
                self._guardian_fail_streak,
                threshold,
            )
            self._status(
                "offline",
                f"{probe.short()} | 连续异常 "
                f"{self._guardian_fail_streak}/{threshold}",
            )
        if self._guardian_fail_streak >= threshold:
            self._guardian_decision = False
            return False
        self._guardian_decision = True
        return True

    def _online(self) -> bool:
        if self.guardian_client is not None:
            state = self._guardian_online()
            if state is not None:
                return state
        return _tcp_online(self.settings)

    def _hold(self) -> None:
        self._guardian_next = 0.0
        self._guardian_decision = None
        self._guardian_fail_streak = 0
        self._guardian_last_state = None
        while not self.stop_event.is_set():
            if not self._online():
                LOG.warning("系统服务检测到掉线，开始补认证")
                self._status("offline", "检测到掉线，开始补认证")
                return
            if self.stop_event.wait(self.settings.check_interval):
                return

    def _run_dhcp_phase(self, phase: str) -> None:
        """Mirror C# AuthWorker: dhcp_mode before/after renewal."""
        mode = (self.settings.dhcp_mode or "").strip().lower()
        if phase == "before":
            wants = mode == "before"
        else:
            wants = mode == "after"
        if not wants or not self._resolved:
            return
        try:
            from .protocols.dhcp_control import DhcpOperation, run as dhcp_run

            LOG.info("DHCP %s: dhcp_mode=%s 开始续租", phase, mode)
            result = dhcp_run(
                self._resolved,
                DhcpOperation.RENEW,
                max(1.0, float(self.settings.dhcp_timeout)),
                bool(self.settings.dhcp_option82),
            )
            LOG.info("DHCP %s 完成: %s 租约 %s", phase, result.message,
                     result.lease.ip if result.lease else "-")
        except Exception as exc:  # noqa: BLE001
            LOG.warning("DHCP %s 失败，继续认证流程: %s", phase, exc)

    def run(self) -> None:
        _setup_service_logging(service_log_path())
        LOG.info("系统认证服务启动")
        self._status("starting", "服务启动")
        while not self.stop_event.is_set():
            try:
                # Mirror C# AuthWorker: reload account/settings every attempt
                # so config/profile changes apply without reinstalling.
                account, settings = load_service_config()
                self.account = account
                self.settings = settings
            except Exception as exc:  # noqa: BLE001
                LOG.warning("系统服务配置读取失败，沿用上一轮: %s", exc)
            try:
                ok = self._logon()
            except SystemExit:
                if self.stop_event.is_set():
                    break
                ok = False
            except Exception as exc:  # noqa: BLE001 - keep service alive
                LOG.exception("系统服务认证异常")
                self._status("error", f"认证异常: {exc}")
                ok = False
            if self.stop_event.is_set():
                break
            if ok:
                self._status("authenticated", "认证成功")
                self._run_dhcp_phase("after")
                if not self.settings.preempt:
                    self._status("idle", "认证成功，服务保持运行")
                    while not self.stop_event.wait(5):
                        pass
                    break
                self._hold()
            else:
                self._status("retrying", "认证失败，等待重试")
                if self.stop_event.wait(self.settings.retry_interval):
                    break
        self._status("stopped", "服务停止")
        LOG.info("系统认证服务停止")


# ---------------------------------------------------------------- Windows SCM


class SERVICE_STATUS(ctypes.Structure):
    _fields_ = [
        ("dwServiceType", wt.DWORD),
        ("dwCurrentState", wt.DWORD),
        ("dwControlsAccepted", wt.DWORD),
        ("dwWin32ExitCode", wt.DWORD),
        ("dwServiceSpecificExitCode", wt.DWORD),
        ("dwCheckPoint", wt.DWORD),
        ("dwWaitHint", wt.DWORD),
    ]


HANDLER_FN = ctypes.WINFUNCTYPE(
    wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_void_p, ctypes.c_void_p
)
SERVICE_MAIN_FN = ctypes.WINFUNCTYPE(None, wt.DWORD, ctypes.POINTER(wt.LPWSTR))


class SERVICE_TABLE_ENTRY(ctypes.Structure):
    _fields_ = [
        ("lpServiceName", wt.LPWSTR),
        ("lpServiceProc", SERVICE_MAIN_FN),
    ]


advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
advapi32.OpenSCManagerW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, wt.DWORD]
advapi32.OpenSCManagerW.restype = wt.HANDLE
advapi32.CreateServiceW.argtypes = [
    wt.HANDLE,
    wt.LPCWSTR,
    wt.LPCWSTR,
    wt.DWORD,
    wt.DWORD,
    wt.DWORD,
    wt.DWORD,
    wt.LPCWSTR,
    wt.LPCWSTR,
    ctypes.POINTER(wt.DWORD),
    wt.LPCWSTR,
    wt.LPCWSTR,
    wt.LPCWSTR,
]
advapi32.CreateServiceW.restype = wt.HANDLE
advapi32.OpenServiceW.argtypes = [wt.HANDLE, wt.LPCWSTR, wt.DWORD]
advapi32.OpenServiceW.restype = wt.HANDLE
advapi32.ChangeServiceConfigW.argtypes = [
    wt.HANDLE,
    wt.DWORD,
    wt.DWORD,
    wt.DWORD,
    wt.LPCWSTR,
    wt.LPCWSTR,
    ctypes.POINTER(wt.DWORD),
    wt.LPCWSTR,
    wt.LPCWSTR,
    wt.LPCWSTR,
    wt.LPCWSTR,
]
advapi32.ChangeServiceConfigW.restype = wt.BOOL
advapi32.CloseServiceHandle.argtypes = [wt.HANDLE]
advapi32.CloseServiceHandle.restype = wt.BOOL
advapi32.RegisterServiceCtrlHandlerExW.argtypes = [
    wt.LPCWSTR,
    HANDLER_FN,
    ctypes.c_void_p,
]
advapi32.RegisterServiceCtrlHandlerExW.restype = wt.HANDLE
advapi32.SetServiceStatus.argtypes = [
    wt.HANDLE,
    ctypes.POINTER(SERVICE_STATUS),
]
advapi32.SetServiceStatus.restype = wt.BOOL
advapi32.StartServiceCtrlDispatcherW.argtypes = [
    ctypes.POINTER(SERVICE_TABLE_ENTRY)
]
advapi32.StartServiceCtrlDispatcherW.restype = wt.BOOL

_status_handle: wt.HANDLE | None = None
_stop_event = threading.Event()
_agent_thread: threading.Thread | None = None


def _set_status(
    state: int,
    accepted: int = 0,
    exit_code: int = 0,
    wait_hint: int = 0,
) -> None:
    if _status_handle is None:
        return
    status = SERVICE_STATUS(
        SERVICE_WIN32_OWN_PROCESS,
        state,
        accepted,
        exit_code,
        0,
        0,
        wait_hint,
    )
    advapi32.SetServiceStatus(_status_handle, ctypes.byref(status))


def _handler(
    control: int, _event_type: int, _event_data: ctypes.c_void_p, _context: ctypes.c_void_p
) -> int:
    if control in (1, 5):  # SERVICE_CONTROL_STOP / SHUTDOWN
        _stop_event.set()
        _set_status(SERVICE_STOP_PENDING, wait_hint=10000)
    return 0


_HANDLER = HANDLER_FN(_handler)


def _service_main(_argc: int, _argv) -> None:
    global _status_handle, _agent_thread
    _boot_log("service_main entry")
    _status_handle = advapi32.RegisterServiceCtrlHandlerExW(
        SERVICE_NAME, _HANDLER, None
    )
    if not _status_handle:
        _boot_log("RegisterServiceCtrlHandlerExW failed")
        return
    _boot_log(f"handler registered {_status_handle}")
    _set_status(SERVICE_START_PENDING, wait_hint=10000)
    _setup_service_logging(service_log_path())
    try:
        account, settings = load_service_config()
    except Exception as exc:  # noqa: BLE001 - service must report the failure
        LOG.exception("系统服务配置加载失败")
        _set_status(SERVICE_STOPPED, exit_code=1)
        return
    _boot_log("config loaded")
    _stop_event.clear()
    agent = SystemAgent(account, settings, _stop_event)
    _set_status(
        SERVICE_RUNNING,
        SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN,
    )
    _boot_log("service running")
    _agent_thread = threading.Thread(target=agent.run, name="ruijie-agent")
    _agent_thread.start()
    while not _stop_event.wait(0.5):
        if not _agent_thread.is_alive():
            break
    _stop_event.set()
    if _agent_thread is not None:
        _agent_thread.join(timeout=15)
    _set_status(SERVICE_STOPPED)


_SERVICE_MAIN = SERVICE_MAIN_FN(_service_main)


def run_as_service() -> int:
    if os.name != "nt":
        print("Windows service mode is only available on Windows", file=sys.stderr)
        return 2
    _boot_log("run_as_service entry")
    table = (SERVICE_TABLE_ENTRY * 2)()
    table[0].lpServiceName = SERVICE_NAME
    table[0].lpServiceProc = _SERVICE_MAIN
    table[1].lpServiceName = None
    table[1].lpServiceProc = None
    if not advapi32.StartServiceCtrlDispatcherW(table):
        error = ctypes.get_last_error()
        _boot_log(f"dispatcher failed {error}")
        if error == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT:
            print(
                "--service 必须由 Windows 服务控制管理器启动",
                file=sys.stderr,
            )
        else:
            print(f"StartServiceCtrlDispatcher failed: {error}", file=sys.stderr)
        return 1
    _boot_log("dispatcher returned")
    return 0


# ------------------------------------------------------------- service control


def service_state() -> tuple[bool, str, int]:
    code, output = _run_sc(["query", SERVICE_NAME])
    if code != 0:
        return False, "not_installed", 0
    match = re.search(r"STATE\s*:\s*(\d+)", output)
    state = int(match.group(1)) if match else 0
    if state == SERVICE_RUNNING:
        return True, "running", state
    if state == SERVICE_STOPPED:
        return True, "stopped", state
    return True, f"state{state}", state


def service_running() -> bool:
    return service_state()[2] == SERVICE_RUNNING


def _open_scm() -> wt.HANDLE:
    handle = advapi32.OpenSCManagerW(
        None, None, SC_MANAGER_ALL_ACCESS
    )
    if not handle:
        raise OSError(ctypes.get_last_error(), "OpenSCManager failed")
    return handle


def install_service(executable: str) -> None:
    binary_path = f'"{executable}"'
    scm = _open_scm()
    try:
        service = advapi32.CreateServiceW(
            scm,
            SERVICE_NAME,
            SERVICE_DISPLAY,
            SERVICE_ALL_ACCESS,
            SERVICE_WIN32_OWN_PROCESS,
            SERVICE_AUTO_START,
            SERVICE_ERROR_NORMAL,
            binary_path,
            None,
            None,
            None,
            None,
            None,
        )
        if not service:
            error = ctypes.get_last_error()
            if error != 1073:  # ERROR_SERVICE_EXISTS
                raise OSError(error, "CreateService failed")
            service = advapi32.OpenServiceW(
                scm, SERVICE_NAME, SERVICE_ALL_ACCESS
            )
            if not service:
                raise OSError(
                    ctypes.get_last_error(), "OpenService failed"
                )
            if not advapi32.ChangeServiceConfigW(
                service,
                SERVICE_NO_CHANGE,
                SERVICE_AUTO_START,
                SERVICE_NO_CHANGE,
                binary_path,
                None,
                None,
                None,
                None,
                None,
                SERVICE_DISPLAY,
            ):
                raise OSError(
                    ctypes.get_last_error(), "ChangeServiceConfig failed"
                )
        advapi32.CloseServiceHandle(service)
    finally:
        advapi32.CloseServiceHandle(scm)
    _run_sc(["description", SERVICE_NAME, SERVICE_DESCRIPTION])
    _run_sc(
        [
            "failure",
            SERVICE_NAME,
            "reset=",
            "86400",
            "actions=",
            "restart/5000/restart/10000/restart/30000",
        ]
    )


def start_service() -> None:
    code, output = _run_sc(["start", SERVICE_NAME])
    if code != 0 and "1056" not in output:
        raise RuntimeError(output.strip() or f"sc start failed ({code})")


def stop_service() -> None:
    code, output = _run_sc(["stop", SERVICE_NAME])
    if code != 0 and "1062" not in output:
        raise RuntimeError(output.strip() or f"sc stop failed ({code})")


def uninstall_service() -> None:
    try:
        stop_service()
    except RuntimeError:
        pass
    code, output = _run_sc(["delete", SERVICE_NAME])
    if code != 0 and "1060" not in output and "1072" not in output:
        raise RuntimeError(output.strip() or f"sc delete failed ({code})")
