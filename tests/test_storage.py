from __future__ import annotations

import json
import os

from ruijie_auth.storage import (
    Account,
    AccountStore,
    AppSettings,
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
    )
    store.save_settings(settings)
    loaded = store.load_settings()
    assert loaded.active_account_id == "abc"
    assert loaded.preempt is False
    assert loaded.check_interval == 4.5
    assert loaded.timeout == 25.0
    assert loaded.connectivity_url == "http://example.test/"
    assert loaded.block_official_client is True


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
