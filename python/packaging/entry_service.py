"""Windows service entry point (onedir build, pywin32 frozen host)."""

from __future__ import annotations

import sys
import threading
from datetime import datetime
from pathlib import Path


def _early(message: str) -> None:
    try:
        path = Path(sys.executable).resolve().parent / "service_entry.log"
        with path.open("a", encoding="utf-8") as handle:
            handle.write(f"{datetime.now()} {message}\n")
    except Exception:
        pass


_early(f"entry argv={sys.argv!r} exe={sys.executable!r}")

import servicemanager
import win32service
import win32serviceutil

from ruijie_auth.system_service import (
    SERVICE_DESCRIPTION,
    SERVICE_DISPLAY,
    SERVICE_NAME,
    SystemAgent,
    _boot_log,
    _setup_service_logging,
    load_service_config,
    service_log_path,
)

_early("imports ok")


class HXXYService(win32serviceutil.ServiceFramework):
    _svc_name_ = SERVICE_NAME
    _svc_display_name_ = SERVICE_DISPLAY
    _svc_description_ = SERVICE_DESCRIPTION

    def __init__(self, args):
        super().__init__(args)
        self.stop_event = threading.Event()
        self.worker: threading.Thread | None = None

    def SvcStop(self) -> None:
        self.ReportServiceStatus(win32service.SERVICE_STOP_PENDING)
        self.stop_event.set()

    def SvcDoRun(self) -> None:
        self.ReportServiceStatus(win32service.SERVICE_RUNNING)
        _boot_log("SvcDoRun entry")
        try:
            account, settings = load_service_config()
        except Exception as exc:
            _boot_log(f"config failed: {exc}")
            servicemanager.LogErrorMsg(f"HXXY service config failed: {exc}")
            return
        _setup_service_logging(service_log_path())
        agent = SystemAgent(account, settings, self.stop_event)
        self.worker = threading.Thread(target=agent.run, name="hxxy-agent")
        self.worker.start()
        self.worker.join()
        _boot_log("SvcDoRun exit")


def main() -> int:
    _early(f"hosting service {SERVICE_NAME}")
    servicemanager.Initialize()
    servicemanager.PrepareToHostSingle(HXXYService)
    _early("dispatching")
    servicemanager.StartServiceCtrlDispatcher()
    _early("dispatcher returned")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
