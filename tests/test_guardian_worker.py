from __future__ import annotations

import pytest

pytest.importorskip("PySide6")

from PySide6.QtCore import QCoreApplication

from ruijie_auth.guardian import GuardianError, GuardianProbe
from ruijie_auth.gui import AuthWorker
from ruijie_auth.storage import Account, AppSettings


class FakeGuardian:
    def __init__(self, probe=None, error: Exception | None = None) -> None:
        self.probe_result = probe
        self.error = error

    def probe(self, _hint=None):
        if self.error is not None:
            raise self.error
        return self.probe_result


def make_worker(failures: int = 2) -> AuthWorker:
    app = QCoreApplication.instance() or QCoreApplication([])
    app.setOrganizationName("HXXYTest")
    settings = AppSettings(
        guardian_enabled=True,
        guardian_url="http://127.0.0.1:18765/mcp",
        guardian_interval=1.0,
        guardian_failures=failures,
    )
    worker = AuthWorker(Account(username="u", password="p"), settings)
    worker._resolved = "test0"
    return worker


def test_guardian_offline_triggers_after_threshold() -> None:
    worker = make_worker(failures=2)
    worker.guardian_client = FakeGuardian(
        GuardianProbe(online=False, interface_name="NG-TMP-0000")
    )
    assert worker._guardian_online() is True
    worker._guardian_next = 0.0
    assert worker._guardian_online() is False


def test_guardian_online_does_not_trigger() -> None:
    worker = make_worker(failures=2)
    worker.guardian_client = FakeGuardian(
        GuardianProbe(online=True, stable_online=True)
    )
    assert worker._guardian_online() is True
    assert worker._guardian_online() is True


def test_guardian_failure_falls_back_to_local_probe() -> None:
    worker = make_worker()
    worker.guardian_client = FakeGuardian(error=GuardianError("offline"))
    assert worker._guardian_online() is None
