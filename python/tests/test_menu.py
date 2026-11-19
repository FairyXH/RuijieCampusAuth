"""Menu logic tests: fully mocked, no disk I/O."""

import sys
from pathlib import Path
from unittest.mock import MagicMock

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ruijie_auth.menu import MenuApp
from ruijie_auth.storage import Account, AppSettings


def _make_app():
    store = MagicMock()
    store.accounts = []
    store.settings = AppSettings()
    store.load_accounts = lambda: store.accounts
    store.save_accounts = lambda accounts: setattr(store, "accounts", accounts)
    store.load_settings = lambda: store.settings
    store.save_settings = lambda s: setattr(store, "settings", s)

    app = MenuApp.__new__(MenuApp)
    app.store = store
    app.settings = store.settings
    return app


def _feed(monkeypatch, values):
    """Patch builtins.input AND getpass.getpass: getpass's isatty check
    raises OSError under pytest's stdin capture."""
    seq = list(values)
    monkeypatch.setattr(
        "builtins.input", lambda prompt="": seq.pop(0) if seq else "0")
    import getpass
    monkeypatch.setattr(
        getpass, "getpass", lambda prompt="": seq.pop(0) if seq else "0")


def test_add_and_set_current_account(monkeypatch):
    app = _make_app()
    _feed(monkeypatch, ["1", "testuser", "testpass", "备注", "0"])
    app.menu_accounts()
    assert len(app.store.accounts) == 1
    assert app.store.accounts[0].username == "testuser"
    assert app.settings.active_account_id == app.store.accounts[0].id


def test_toggle_and_delete_account(monkeypatch):
    app = _make_app()
    _feed(monkeypatch, ["1", "user1", "pw", "", "0"])
    app.menu_accounts()
    _feed(monkeypatch, ["4", "1", "3", "1", "0"])
    app.menu_accounts()
    assert app.store.accounts == []


def test_duplicate_account_rejected(monkeypatch):
    app = _make_app()
    _feed(monkeypatch, ["1", "user", "pw", "", "0"])
    app.menu_accounts()
    _feed(monkeypatch, ["1", "user", "pw2", "", "0"])
    app.menu_accounts()
    assert len(app.store.accounts) == 1


def test_main_menu_dispatch_exits(monkeypatch):
    app = _make_app()
    _feed(monkeypatch, ["0"])
    assert app.run() == 0


def test_invalid_choice_is_ignored(monkeypatch):
    app = _make_app()
    _feed(monkeypatch, ["x", "99", "", "0"])
    app.run()

def test_update_selected_account(monkeypatch):
    app = _make_app()
    _feed(monkeypatch, ["1", "user1", "pw", "old note", "0"])
    app.menu_accounts()
    _feed(monkeypatch, ["2", "1", "user2", "", "new note", "0"])
    app.menu_accounts()
    assert len(app.store.accounts) == 1
    account = app.store.accounts[0]
    assert account.username == "user2"
    assert account.password == "pw"
    assert account.note == "new note"
