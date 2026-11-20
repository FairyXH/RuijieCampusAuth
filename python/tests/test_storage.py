from __future__ import annotations

import json
import os

from ruijie_auth.storage import (
    Account,
    AccountStore,
    AppSettings,
    ServerStore,
    protect_password,
    unprotect_password,
)


def test_password_roundtrip() -> None:
    encrypted = protect_password("s3cret-密码")
    assert "s3cret" not in encrypted
    assert unprotect_password(encrypted) == "s3cret-密码"


def test_account_store_roundtrip(tmp_path) -> None:
    store = AccountStore(base_dir=tmp_path)
    account = Account(username="student", password="secret", note="测试", enabled=False)
    store.save_accounts([account])

    raw = (tmp_path / "accounts.json").read_text(encoding="utf-8")
    if os.name == "nt":
        assert "secret" not in raw
    else:
        assert "plain:" in raw

    loaded = store.load_accounts()
    assert len(loaded) == 1
    assert loaded[0].id == account.id
    assert loaded[0].username == "student"
    assert loaded[0].password == "secret"
    assert loaded[0].note == "测试"
    assert loaded[0].enabled is False


def test_settings_roundtrip(tmp_path) -> None:
    store = AccountStore(base_dir=tmp_path)
    settings = AppSettings(
        active_account_id="abc",
        interface="iface",
        preempt=False,
        check_interval=4.5,
        retry_interval=6.0,
        timeout=25.0,
        retries=2,
        repeats=4,
        connectivity_url="http://example.test/",
        block_official_client=True,
        server_lock=True,
        locked_server="00:1A:A9:17:FF:FF",
        guardian_enabled=True,
        guardian_url="http://127.0.0.1:18765/mcp",
        guardian_interval=7.5,
        guardian_failures=3,
        service_name="测试服务",
        static_id="Static:DEADBEEF",
        start_timeout=4.5,
        dhcp_mode="after",
        dhcp_timeout=45.0,
        dhcp_option82=True,
        school_code="HPVTC",
    )
    store.save_settings(settings)
    loaded = store.load_settings()
    assert loaded.active_account_id == "abc"
    assert loaded.preempt is False
    assert loaded.check_interval == 4.5
    assert loaded.timeout == 25.0
    assert loaded.connectivity_url == "http://example.test/"
    assert loaded.block_official_client is True
    assert loaded.server_lock is True
    assert loaded.locked_server == "00:1a:a9:17:ff:ff"
    assert loaded.guardian_enabled is True
    assert loaded.guardian_url == "http://127.0.0.1:18765/mcp"
    assert loaded.guardian_interval == 7.5
    assert loaded.guardian_failures == 3
    assert loaded.service_name == "测试服务"
    assert loaded.static_id == "Static:DEADBEEF"
    assert loaded.start_timeout == 4.5
    assert loaded.dhcp_mode == "after"
    assert loaded.dhcp_timeout == 45.0
    assert loaded.dhcp_option82 is True
    assert loaded.school_code == "HPVTC"


def test_settings_clamped(tmp_path) -> None:
    store = AccountStore(base_dir=tmp_path)
    store.settings_path.write_text(
        json.dumps({"check_interval": 0.01, "retry_interval": 0.01, "timeout": 1}),
        encoding="utf-8",
    )
    loaded = store.load_settings()
    assert loaded.check_interval >= 0.5
    assert loaded.retry_interval >= 0.5
    assert loaded.timeout >= 5.0


def test_server_store_learns_and_updates(tmp_path) -> None:
    store = ServerStore(tmp_path / "servers.json")
    record = store.upsert(
        "00-1A-A9-17-FF-FF",
        interface="npf",
        description="Realtek USB GbE",
        success=True,
    )
    assert record is not None
    assert record.mac == "00:1a:a9:17:ff:ff"
    assert record.success_count == 1
    assert record.last_success

    store.upsert("00:1a:a9:17:ff:ff", success=True, name="DEMO")
    loaded = store.get("00:1a:a9:17:ff:ff")
    assert loaded is not None
    assert loaded.success_count == 2
    assert loaded.name == "DEMO"

    assert store.rename("00:1a:a9:17:ff:ff", "主认证服务器")
    assert store.get("00:1a:a9:17:ff:ff").name == "主认证服务器"
    assert store.remove("00:1a:a9:17:ff:ff")
    assert store.get("00:1a:a9:17:ff:ff") is None


def test_server_store_imports_discovery(tmp_path) -> None:
    discovery = tmp_path / "discovery.json"
    discovery.write_text(
        json.dumps(
            {
                "interface": "\\Device\\NPF_TEST",
                "description": "Realtek USB GbE Family Controller",
                "server_mac": "00:1A:A9:17:FF:FF",
                "updated_at": "2026-11-18T06:02:30+08:00",
            }
        ),
        encoding="utf-8",
    )
    store = ServerStore(tmp_path / "servers.json")
    record = store.import_discovery(discovery)
    assert record is not None
    assert record.mac == "00:1a:a9:17:ff:ff"
    assert record.last_seen == "2026-11-18T06:02:30+08:00"
    assert store.import_discovery(discovery) is not None
    assert len(store.load()) == 1
