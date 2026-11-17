"""Machine-wide takeover heartbeat consumed by restart_ruijie.vbs.

The official-client launcher must only skip the original client when the
takeover app is demonstrably healthy. The app writes this file every second
while it is actively managing authentication and blocking 8021x.exe. The VBS
combines this file, its mtime, the GUI process and the IFEO marker.
"""

from __future__ import annotations

import logging
import os
import sys
import threading
from pathlib import Path

from .storage import program_data_dir

LOG = logging.getLogger("ruijie-auth")


class TakeoverState:
    def __init__(self, path: Path | None = None) -> None:
        self.path = Path(path) if path else program_data_dir() / "takeover.ini"
        self._lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._enabled = False

    @property
    def enabled(self) -> bool:
        return self._enabled

    def set_enabled(self, enabled: bool) -> None:
        with self._lock:
            if enabled:
                if not self._enabled:
                    self._enabled = True
                    self._stop.clear()
                    self._write(True)
                    self._thread = threading.Thread(
                        target=self._heartbeat,
                        name="ruijie-takeover-heartbeat",
                        daemon=True,
                    )
                    self._thread.start()
                else:
                    self._write(True)
            else:
                self._enabled = False
                self._stop.set()
                thread = self._thread
                self._thread = None
                if thread is not None:
                    thread.join(2)
                self._write(False)

    def stop(self) -> None:
        self.set_enabled(False)

    def _heartbeat(self) -> None:
        while not self._stop.wait(1.0):
            self._write(True)

    def _write(self, enabled: bool) -> None:
        content = (
            f"enabled={1 if enabled else 0}\n"
            f"pid={os.getpid()}\n"
            f"exe={sys.executable}\n"
        )
        with self._write_lock:
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                temp = self.path.with_suffix(self.path.suffix + ".tmp")
                temp.write_text(content, encoding="ascii")
                os.replace(temp, self.path)
            except OSError as exc:
                LOG.warning("写入托管状态失败: %s", exc)
