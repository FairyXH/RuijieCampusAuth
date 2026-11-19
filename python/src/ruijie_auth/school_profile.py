"""School profile store (mirrors Core SchoolProfile/ProfileStore)."""

from __future__ import annotations

import json
import shutil
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from .storage import AccountStore


@dataclass
class SchoolProfile:
    name: str = ""
    school_code: str = ""
    auth_mode: str = "EAPMD5"
    service_name: str = ""
    static_id: str = ""
    self_service_url: str = ""
    update_url: str = ""
    portal_url: str = ""
    sam_host: str = ""
    sam_port: int = 0
    dhcp_mode: str = "none"
    dhcp_timeout: float = 30.0
    dhcp_option82: bool = False
    ad_domain: str = ""
    ad_timeout: float = 120.0
    engine_version: str = ""
    notes: str = ""
    extra: dict = field(default_factory=dict)


@dataclass
class ProfileImportResult:
    profile_name: str
    settings_path: str
    backup_path: str | None
    applied: list[str]


def load_profile(path: Path) -> SchoolProfile:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    known = {f for f in SchoolProfile.__dataclass_fields__ if f != "extra"}
    profile = SchoolProfile()
    for key, value in data.items():
        snake = key.replace("-", "_")
        if snake in known:
            setattr(profile, snake, value)
        else:
            profile.extra[key] = value
    if not profile.name.strip():
        profile.name = Path(path).stem
    return profile


def list_profiles(directory: Path) -> list[Path]:
    if not Path(directory).is_dir():
        return []
    return sorted(Path(directory).glob("*.json"), key=lambda p: p.name.lower())


def discover_profiles(extra_directory: str | None = None) -> list[Path]:
    directories = [
        Path(__file__).resolve().parents[3] / "profiles",
        Path.cwd() / "profiles",
        Path(extra_directory) if extra_directory else None,
    ]
    result: list[Path] = []
    seen: set[str] = set()
    for directory in directories:
        if directory is None:
            continue
        for path in list_profiles(directory):
            resolved = str(path.resolve())
            if resolved.lower() not in seen:
                seen.add(resolved.lower())
                result.append(path)
    return result


def import_profile(profile: SchoolProfile, store: DataStore,
                   profile_path: str | None = None) -> ProfileImportResult:
    settings_path = store.settings_path
    backup: str | None = None
    if Path(settings_path).exists():
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        backup = f"{settings_path}.bak-{stamp}"
        shutil.copyfile(settings_path, backup)

    settings = store.load_settings()
    applied: list[str] = []

    def apply(key: str, value, setter) -> None:
        if value:
            setter(value)
            applied.append(key)

    def apply_int(key: str, value, setter, minimum: int = 1) -> None:
        if value and value >= minimum:
            setter(value)
            applied.append(key)

    if profile.name:
        settings.profile_name = profile.name
        applied.append("profile_name")
    apply("school_code", profile.school_code,
          lambda v: setattr(settings, "school_code", v))
    apply("auth_mode", profile.auth_mode,
          lambda v: setattr(settings, "auth_mode", v))
    apply("service_name", profile.service_name,
          lambda v: setattr(settings, "service_name", v))
    apply("static_id", profile.static_id,
          lambda v: setattr(settings, "static_id", v))
    apply("self_service_url", profile.self_service_url,
          lambda v: setattr(settings, "self_service_url", v))
    apply("update_url", profile.update_url,
          lambda v: setattr(settings, "update_url", v))
    apply("portal_url", profile.portal_url,
          lambda v: setattr(settings, "portal_url", v))
    apply("sam_host", profile.sam_host,
          lambda v: setattr(settings, "sam_host", v))
    if profile.sam_port > 0:
        settings.sam_port = profile.sam_port
        applied.append("sam_port")
    if profile.dhcp_mode in ("before", "after", "none"):
        settings.dhcp_mode = profile.dhcp_mode
        applied.append("dhcp_mode")
    if profile.dhcp_timeout > 0:
        settings.dhcp_timeout = profile.dhcp_timeout
        applied.append("dhcp_timeout")
    settings.dhcp_option82 = profile.dhcp_option82
    applied.append("dhcp_option82")
    apply("ad_domain", profile.ad_domain,
          lambda v: setattr(settings, "ad_domain", v))
    if profile.ad_timeout > 0:
        settings.ad_timeout = profile.ad_timeout
        applied.append("ad_timeout")
    apply("engine_version", profile.engine_version,
          lambda v: setattr(settings, "engine_version", v))

    store.save_settings(settings)

    # Propagate into service config so a running service agent picks the
    # switch up on its next cycle (mirrors C# ProfileStore.Import).
    from .system_service import service_config_path, write_service_config
    applied.append("service_config") if Path(
        service_config_path()).exists() else None
    try:
        if Path(service_config_path()).exists():
            accounts = store.load_accounts()
            active_id = settings.active_account_id
            account = next((a for a in accounts if a.id == active_id),
                           next((a for a in accounts if a.enabled), None))
            if account is not None:
                write_service_config(account, settings)
    except Exception:
        # service.json may be protected; the menu's "应用服务配置" can refresh.
        pass

    return ProfileImportResult(profile.name, str(settings_path), backup, applied)
