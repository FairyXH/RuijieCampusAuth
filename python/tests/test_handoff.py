from __future__ import annotations

import time

from ruijie_auth.handoff import TakeoverState


def test_takeover_state_heartbeat(tmp_path) -> None:
    path = tmp_path / "takeover.ini"
    state = TakeoverState(path)
    state.set_enabled(True)
    try:
        text = path.read_text(encoding="ascii")
        assert "enabled=1" in text
        assert "pid=" in text
        assert "exe=" in text
        first = path.stat().st_mtime_ns
        time.sleep(1.2)
        assert path.stat().st_mtime_ns > first
    finally:
        state.stop()
    assert "enabled=0" in path.read_text(encoding="ascii")
