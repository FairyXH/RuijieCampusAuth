"""Temporarily block the official Ruijie client (8021x.exe).

The blocker does two things while enabled:

1. A watchdog thread repeatedly terminates any running ``8021x.exe``.
2. A temporary Image File Execution Options (IFEO) policy makes new launches
   fail. Both the 64-bit and 32-bit registry views are written because the
   official client is 32-bit. The previous values are saved in
   ``blocker_state.json`` and restored when the switch is turned off or the
   program exits. A stale state is cleaned on the next start.
"""

from __future__ import annotations

import ctypes
import json
import logging
import os
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Any

LOG = logging.getLogger("ruijie-auth")
TARGET_EXE = "8021x.exe"
MARKER_NAME = "HXXYTakeover"
MARKER_VALUE = "RuijieCampusAuth"
IFEO_PATH = (
    r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
    r"\8021x.exe"
)
STATE_VERSION = 1

if os.name == "nt":
    import winreg
    from ctypes import wintypes

    TH32CS_SNAPPROCESS = 0x00000002
    PROCESS_TERMINATE = 0x0001
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

    class _PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", wintypes.WCHAR * 260),
        ]

    class _LUID(ctypes.Structure):
        _fields_ = [("LowPart", wintypes.DWORD), ("HighPart", wintypes.LONG)]

    class _LUID_AND_ATTRIBUTES(ctypes.Structure):
        _fields_ = [("Luid", _LUID), ("Attributes", wintypes.DWORD)]

    class _TOKEN_PRIVILEGES(ctypes.Structure):
        _fields_ = [
            ("PrivilegeCount", wintypes.DWORD),
            ("Privileges", _LUID_AND_ATTRIBUTES * 1),
        ]


def _views() -> list[tuple[str, int]]:
    if os.name != "nt":
        return []
    return [
        ("64", winreg.KEY_WOW64_64KEY),
        ("32", winreg.KEY_WOW64_32KEY),
    ]


def _debugger_command() -> str:
    cmd = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "cmd.exe")
    return f'"{cmd}" /c exit'


def _enable_debug_privilege() -> None:
    if os.name != "nt":
        return
    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi32.OpenProcessToken.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.HANDLE),
    ]
    advapi32.OpenProcessToken.restype = wintypes.BOOL
    advapi32.LookupPrivilegeValueW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.LPCWSTR,
        ctypes.POINTER(_LUID),
    ]
    advapi32.LookupPrivilegeValueW.restype = wintypes.BOOL
    advapi32.AdjustTokenPrivileges.argtypes = [
        wintypes.HANDLE,
        wintypes.BOOL,
        ctypes.POINTER(_TOKEN_PRIVILEGES),
        wintypes.DWORD,
        ctypes.c_void_p,
        ctypes.c_void_p,
    ]
    advapi32.AdjustTokenPrivileges.restype = wintypes.BOOL
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    token = wintypes.HANDLE()
    if not advapi32.OpenProcessToken(
        kernel32.GetCurrentProcess(),
        0x0020 | 0x0008,
        ctypes.byref(token),
    ):
        raise ctypes.WinError()
    try:
        luid = _LUID()
        if not advapi32.LookupPrivilegeValueW(
            None, "SeDebugPrivilege", ctypes.byref(luid)
        ):
            raise ctypes.WinError()
        privileges = _TOKEN_PRIVILEGES(
            1, (_LUID_AND_ATTRIBUTES * 1)(_LUID_AND_ATTRIBUTES(luid, 0x0002))
        )
        if not advapi32.AdjustTokenPrivileges(
            token, False, ctypes.byref(privileges), 0, None, None
        ):
            raise ctypes.WinError()
        if ctypes.get_last_error() == 1300:
            raise OSError("SeDebugPrivilege 未分配")
    finally:
        kernel32.CloseHandle(token)


def _kill_target() -> list[int]:
    if os.name != "nt":
        return []
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.Process32FirstW.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(_PROCESSENTRY32W),
    ]
    kernel32.Process32FirstW.restype = wintypes.BOOL
    kernel32.Process32NextW.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(_PROCESSENTRY32W),
    ]
    kernel32.Process32NextW.restype = wintypes.BOOL
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel32.TerminateProcess.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot is None or snapshot == INVALID_HANDLE_VALUE:
        return []
    killed: list[int] = []
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
        if not kernel32.Process32FirstW(snapshot, ctypes.byref(entry)):
            return killed
        while True:
            if entry.szExeFile.casefold() == TARGET_EXE.casefold():
                handle = kernel32.OpenProcess(
                    PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION,
                    False,
                    entry.th32ProcessID,
                )
                if handle:
                    try:
                        if kernel32.TerminateProcess(handle, 1):
                            killed.append(int(entry.th32ProcessID))
                    finally:
                        kernel32.CloseHandle(handle)
            if not kernel32.Process32NextW(snapshot, ctypes.byref(entry)):
                break
    finally:
        kernel32.CloseHandle(snapshot)
    return killed


def _open_key(view: int, access: int):
    return winreg.CreateKeyEx(winreg.HKEY_LOCAL_MACHINE, IFEO_PATH, 0, access | view)


def _read_debugger(view: int) -> tuple[bool, str | None]:
    try:
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            IFEO_PATH,
            0,
            winreg.KEY_QUERY_VALUE | view,
        )
    except FileNotFoundError:
        return False, None
    try:
        try:
            value, _kind = winreg.QueryValueEx(key, "Debugger")
            return True, str(value)
        except FileNotFoundError:
            return True, None
    finally:
        winreg.CloseKey(key)


def _set_debugger(view: int, value: str) -> None:
    key = _open_key(view, winreg.KEY_SET_VALUE | winreg.KEY_QUERY_VALUE)
    try:
        winreg.SetValueEx(key, "Debugger", 0, winreg.REG_SZ, value)
    finally:
        winreg.CloseKey(key)


def _read_marker(view: int) -> str | None:
    try:
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            IFEO_PATH,
            0,
            winreg.KEY_QUERY_VALUE | view,
        )
    except FileNotFoundError:
        return None
    try:
        try:
            value, _kind = winreg.QueryValueEx(key, MARKER_NAME)
            return str(value)
        except FileNotFoundError:
            return None
    finally:
        winreg.CloseKey(key)


def _set_marker(view: int, value: str = MARKER_VALUE) -> None:
    key = _open_key(view, winreg.KEY_SET_VALUE | winreg.KEY_QUERY_VALUE)
    try:
        winreg.SetValueEx(key, MARKER_NAME, 0, winreg.REG_SZ, value)
    finally:
        winreg.CloseKey(key)


def _delete_marker(view: int) -> None:
    try:
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            IFEO_PATH,
            0,
            winreg.KEY_SET_VALUE | view,
        )
    except FileNotFoundError:
        return
    try:
        try:
            winreg.DeleteValue(key, MARKER_NAME)
        except FileNotFoundError:
            pass
    finally:
        winreg.CloseKey(key)


def _delete_debugger(view: int) -> None:
    try:
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            IFEO_PATH,
            0,
            winreg.KEY_SET_VALUE | view,
        )
    except FileNotFoundError:
        return
    try:
        try:
            winreg.DeleteValue(key, "Debugger")
        except FileNotFoundError:
            pass
    finally:
        winreg.CloseKey(key)


def _looks_like_ours(value: str | None) -> bool:
    if not value:
        return False
    text = value.casefold().replace("\\", "/")
    return "cmd.exe" in text and "/c exit" in text


def _delete_key_if_empty(view: int) -> None:
    try:
        winreg.DeleteKey(winreg.HKEY_LOCAL_MACHINE, IFEO_PATH)
    except OSError:
        pass


def _load_state(path: Path) -> dict[str, Any] | None:
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _save_state(path: Path, state: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temp, path)


def _remove_state(path: Path) -> None:
    try:
        path.unlink()
    except OSError:
        pass


class ProcessBlocker:
    def __init__(self, state_path: Path) -> None:
        self.state_path = Path(state_path)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()
        self._debugger = _debugger_command()

    @property
    def running(self) -> bool:
        thread = self._thread
        return thread is not None and thread.is_alive()

    def start(self) -> None:
        if os.name != "nt":
            raise RuntimeError("托管模式仅支持 Windows")
        with self._lock:
            if self.running:
                return
            self._stop.clear()
            try:
                _enable_debug_privilege()
            except OSError as exc:
                LOG.warning("启用 SeDebugPrivilege 失败，将仅尝试结束可见进程: %s", exc)
            self._apply()
            killed = _kill_target()
            if killed:
                LOG.warning("已结束 8021x.exe: %s", killed)
            self._thread = threading.Thread(
                target=self._run, name="ruijie-client-blocker", daemon=True
            )
            self._thread.start()
            LOG.warning("托管模式已开启：持续阻止原客户端 8021x.exe")

    def stop(self) -> None:
        self._stop.set()
        thread = self._thread
        if thread is not None:
            thread.join(3)
        self._thread = None
        self._restore()
        LOG.info("托管模式已关闭：原客户端运行策略已恢复")

    def _run(self) -> None:
        next_policy = time.monotonic() + 5.0
        while not self._stop.is_set():
            killed = _kill_target()
            if killed:
                LOG.warning("已结束 8021x.exe: %s", killed)
            if time.monotonic() >= next_policy:
                try:
                    self._enforce_policy()
                except OSError as exc:
                    LOG.error("重新应用阻止策略失败: %s", exc)
                next_policy = time.monotonic() + 5.0
            self._stop.wait(0.5)

    def _apply(self) -> None:
        if self.state_path.exists():
            self._restore()
        else:
            self.cleanup_legacy()
        state: dict[str, Any] = {
            "version": STATE_VERSION,
            "created_at": datetime.now().isoformat(timespec="seconds"),
            "debugger": self._debugger,
            "views": {},
        }
        for label, view in _views():
            existed, previous = _read_debugger(view)
            state["views"][label] = {
                "key_existed": existed,
                "previous_debugger": previous,
                "previous_marker": _read_marker(view),
            }
        _save_state(self.state_path, state)
        try:
            for _label, view in _views():
                _set_debugger(view, self._debugger)
                _set_marker(view)
        except OSError:
            self._restore()
            raise

    def _restore(self) -> None:
        state = _load_state(self.state_path)
        if not state:
            return
        failed = False
        for label, view in _views():
            info = state.get("views", {}).get(label, {})
            previous = info.get("previous_debugger")
            previous_marker = info.get("previous_marker")
            try:
                if previous is not None:
                    _set_debugger(view, str(previous))
                else:
                    _delete_debugger(view)
                if previous_marker is not None:
                    _set_marker(view, str(previous_marker))
                else:
                    _delete_marker(view)
                if previous is None and not info.get("key_existed", False):
                    _delete_key_if_empty(view)
            except OSError as exc:
                failed = True
                LOG.error("恢复 IFEO 策略失败 (%s): %s", label, exc)
        if not failed:
            _remove_state(self.state_path)

    def _enforce_policy(self) -> None:
        for label, view in _views():
            try:
                _existed, current = _read_debugger(view)
                marker = _read_marker(view)
                if current != self._debugger or marker != MARKER_VALUE:
                    _set_debugger(view, self._debugger)
                    _set_marker(view)
                    LOG.warning("检测到 IFEO 策略被修改，已重新应用 (%s)", label)
            except OSError as exc:
                LOG.error("检查 IFEO 策略失败 (%s): %s", label, exc)

    @classmethod
    def cleanup_legacy(cls) -> bool:
        if os.name != "nt":
            return False
        changed = False
        for label, view in _views():
            try:
                _existed, current = _read_debugger(view)
                marker = _read_marker(view)
                if _looks_like_ours(current) and marker != MARKER_VALUE:
                    _delete_debugger(view)
                    _delete_marker(view)
                    _delete_key_if_empty(view)
                    changed = True
                    LOG.warning("清理旧版未带标记的 IFEO 策略 (%s)", label)
            except OSError as exc:
                LOG.error("清理旧版 IFEO 策略失败 (%s): %s", label, exc)
        return changed

    @classmethod
    def cleanup_stale(cls, state_path: Path) -> bool:
        if os.name != "nt":
            return False
        changed = False
        path = Path(state_path)
        if path.exists():
            blocker = cls(path)
            blocker._restore()
            changed = not path.exists()
        if cls.cleanup_legacy():
            changed = True
        return changed
