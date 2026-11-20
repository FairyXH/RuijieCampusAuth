from __future__ import annotations

import os

import pytest

pytest.importorskip("scapy")

from ruijie_auth.storage import Account, AppSettings
from ruijie_auth.system_service import (
    load_service_config,
    read_service_status,
    remove_service_config,
    service_root,
    write_service_config,
    write_service_status,
)


def test_service_root_honors_override(tmp_path, monkeypatch) -> None:
    monkeypatch.setenv("RUIJIE_SERVICE_DIR", str(tmp_path))
    assert service_root() == tmp_path


@pytest.mark.skipif(os.name != "nt", reason="machine DPAPI is Windows-only")
def test_service_config_roundtrip(tmp_path, monkeypatch) -> None:
    monkeypatch.setenv("RUIJIE_SERVICE_DIR", str(tmp_path))
    account = Account(username="student", password="secret")
    settings = AppSettings(
        interface="iface",
        service_name="测试服务",
        static_id="Static:DEADBEEF",
    )
    path = write_service_config(account, settings)
    assert path.exists()
    loaded_account, loaded_settings = load_service_config()
    assert loaded_account.username == "student"
    assert loaded_account.password == "secret"
    assert loaded_settings.interface == "iface"
    assert loaded_settings.service_name == "测试服务"
    assert loaded_settings.static_id == "Static:DEADBEEF"
    remove_service_config()
    assert not path.exists()


def test_service_status_roundtrip(tmp_path, monkeypatch) -> None:
    monkeypatch.setenv("RUIJIE_SERVICE_DIR", str(tmp_path))
    write_service_status("authenticated", "认证成功", interface="iface")
    status = read_service_status()
    assert status is not None
    assert status["state"] == "authenticated"
    assert status["message"] == "认证成功"
    assert status["interface"] == "iface"
