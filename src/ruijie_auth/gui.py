"""Qt GUI for the Ruijie campus authenticator.

Run from source:
    python -m ruijie_auth.gui
    python -m ruijie_auth.gui --tray --auto

The Windows build is a single elevated exe (see build.ps1). Data is stored in
%APPDATA%\\RuijieCampusAuth; account passwords are DPAPI-encrypted.
"""

from __future__ import annotations

import argparse
import ctypes
import getpass
import ipaddress
import logging
import os
import re
import socket
import subprocess
import sys
import threading
from dataclasses import replace
from datetime import datetime
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Callable, Iterable
from urllib.parse import urlsplit

from PySide6.QtCore import (
    QEasingCurve,
    QObject,
    QPropertyAnimation,
    QRectF,
    QSize,
    Qt,
    QThread,
    QTimer,
    QUrl,
    Property,
    Signal,
)
from PySide6.QtGui import (
    QAction,
    QColor,
    QDesktopServices,
    QFont,
    QIcon,
    QPainter,
    QPalette,
    QPen,
    QPixmap,
)
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import (
    QAbstractButton,
    QAbstractItemView,
    QApplication,
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFormLayout,
    QFrame,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QInputDialog,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMenu,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QSpinBox,
    QSystemTrayIcon,
    QTableWidget,
    QTableWidgetItem,
    QTabWidget,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from . import __version__
from .blocker import ProcessBlocker
from .cli import (
    LOG,
    Supplicant,
    choose_interface,
    list_interfaces,
    probe_authenticator,
)
from .handoff import TakeoverState
from .private import (
    TailOptions,
    build_tail,
    collect_network_info,
    normalize_mac,
)
from .storage import (
    Account,
    AccountStore,
    AppSettings,
    DiscoveryCache,
    ServerRecord,
    ServerStore,
    default_data_dir,
    program_data_dir,
)

APP_NAME = "HXXY 校园网认证"
APP_ID = "RuijieCampusAuth"
APP_VERSION = __version__
UI_FONT = "Microsoft YaHei UI"

STYLE = """
QWidget {
    background: transparent;
    color: #1c1c1e;
    font-family: "SF Pro Display", "Segoe UI Variable", "Segoe UI",
                 "Microsoft YaHei UI", sans-serif;
    font-size: 13px;
}
QMainWindow, QWidget#root { background: #f2f2f7; }
QFrame#card {
    background: #ffffff;
    border: none;
    border-radius: 16px;
}
QTabWidget#sidebar::pane {
    background: #ffffff;
    border: none;
    border-radius: 16px;
    top: -1px;
}
QTabWidget#sidebar QTabBar {
    background: transparent;
}
QTabWidget#sidebar QTabBar::tab {
    background: transparent;
    padding: 9px 14px;
    color: #8e8e93;
    border: none;
    font-weight: 500;
}
QTabWidget#sidebar QTabBar::tab:selected {
    color: #007aff;
    font-weight: 600;
    border-bottom: 2px solid #007aff;
}
QTabWidget#sidebar QTabBar::tab:hover { color: #1c1c1e; }
QGroupBox {
    background: #f2f2f7;
    border: none;
    border-radius: 12px;
    margin-top: 12px;
    padding: 26px 12px 10px 12px;
    font-weight: 500;
}
QGroupBox::title {
    subcontrol-origin: margin;
    left: 12px;
    top: 4px;
    padding: 0;
    color: #8e8e93;
    font-size: 11px;
    font-weight: 600;
}
QLabel { background: transparent; }
QLabel#title {
    font-size: 20px;
    font-weight: 700;
    color: #1c1c1e;
}
QLabel#state {
    font-size: 22px;
    font-weight: 700;
}
QLabel#muted { color: #8e8e93; font-size: 12px; }
QLabel#value { color: #1c1c1e; }
QPushButton {
    background: #ffffff;
    border: 1px solid #d1d1d6;
    border-radius: 10px;
    padding: 8px 12px;
    color: #007aff;
    font-weight: 500;
}
QPushButton:hover { background: #f2f7ff; border-color: #007aff; }
QPushButton:pressed { background: #e5efff; }
QPushButton:disabled {
    color: #c7c7cc;
    border-color: #e5e5ea;
    background: #fafafa;
}
QPushButton#primary {
    background: #007aff;
    border: none;
    color: #ffffff;
    font-weight: 600;
    padding: 10px 14px;
}
QPushButton#primary:hover { background: #0a84ff; }
QPushButton#danger {
    background: #ff3b30;
    border: none;
    color: #ffffff;
    font-weight: 600;
    padding: 10px 14px;
}
QPushButton#danger:hover { background: #ff453a; }
QPushButton#link {
    background: transparent;
    border: none;
    color: #007aff;
    padding: 4px;
}
QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox {
    background: #f2f2f7;
    border: 1px solid #e5e5ea;
    border-radius: 9px;
    padding: 7px 9px;
    color: #1c1c1e;
    selection-background-color: #007aff;
    selection-color: #ffffff;
}
QComboBox::drop-down { border: none; width: 22px; }
QComboBox QAbstractItemView {
    background: #ffffff;
    color: #1c1c1e;
    border: 1px solid #e5e5ea;
    outline: 0;
    selection-background-color: #e5efff;
    selection-color: #1c1c1e;
}
QComboBox QAbstractItemView::item { min-height: 28px; padding: 4px 8px; }
QTableWidget {
    background: #ffffff;
    border: none;
    gridline-color: transparent;
    selection-background-color: #e5efff;
    selection-color: #1c1c1e;
}
QTableWidget::item {
    padding: 6px 4px;
    border-bottom: 1px solid #f2f2f7;
}
QHeaderView::section {
    background: #ffffff;
    border: none;
    padding: 6px 4px;
    color: #8e8e93;
    font-size: 11px;
    font-weight: 600;
    border-bottom: 1px solid #e5e5ea;
}
QPlainTextEdit {
    background: #ffffff;
    border: none;
    border-radius: 12px;
    font-family: "Cascadia Mono", "SF Mono", "Consolas", monospace;
    font-size: 12px;
    color: #1c1c1e;
    selection-background-color: #007aff;
    selection-color: #ffffff;
}
QScrollArea { background: transparent; border: none; }
QScrollArea QWidget { background: transparent; }
QScrollBar:vertical {
    background: transparent;
    width: 8px;
    margin: 2px;
}
QScrollBar::handle:vertical {
    background: #c7c7cc;
    border-radius: 4px;
    min-height: 24px;
}
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical { background: transparent; }
QScrollBar:horizontal {
    background: transparent;
    height: 8px;
    margin: 2px;
}
QScrollBar::handle:horizontal {
    background: #c7c7cc;
    border-radius: 4px;
    min-width: 24px;
}
QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal { width: 0; }
QScrollBar::add-page:horizontal, QScrollBar::sub-page:horizontal { background: transparent; }
QTableCornerButton::section { background: #ffffff; border: none; }
QCheckBox { spacing: 8px; color: #1c1c1e; }
QCheckBox::indicator {
    width: 20px;
    height: 20px;
    border-radius: 10px;
    border: 1px solid #c7c7cc;
    background: #ffffff;
}
QCheckBox::indicator:checked {
    background: #007aff;
    border-color: #007aff;
}
QStatusBar { color: #8e8e93; background: transparent; }
QSplitter::handle { background: transparent; width: 10px; }
QDialog, QMessageBox, QInputDialog { background: #ffffff; }
QDialog QLabel, QMessageBox QLabel, QInputDialog QLabel { color: #1c1c1e; }
QMenu {
    background: #ffffff;
    border: 1px solid #e5e5ea;
    border-radius: 10px;
    padding: 6px;
    color: #1c1c1e;
}
QMenu::item {
    background: transparent;
    padding: 7px 24px 7px 12px;
    border-radius: 6px;
}
QMenu::item:selected { background: #e5efff; color: #1c1c1e; }
QMenu::item:disabled { color: #c7c7cc; }
QMenu::separator { height: 1px; background: #e5e5ea; margin: 5px 8px; }
QToolTip {
    background: #1c1c1e;
    color: #ffffff;
    border: none;
    padding: 6px 8px;
    border-radius: 8px;
}
"""


def apply_light_palette(app: QApplication) -> None:
    """Force light colors for native popups/dialogs under Windows dark mode."""
    palette = QPalette()
    role = QPalette.ColorRole
    group = QPalette.ColorGroup
    palette.setColor(role.Window, QColor("#ffffff"))
    palette.setColor(role.WindowText, QColor("#1c1c1e"))
    palette.setColor(role.Base, QColor("#ffffff"))
    palette.setColor(role.AlternateBase, QColor("#f7f7fa"))
    palette.setColor(role.ToolTipBase, QColor("#ffffff"))
    palette.setColor(role.ToolTipText, QColor("#1c1c1e"))
    palette.setColor(role.Text, QColor("#1c1c1e"))
    palette.setColor(role.Button, QColor("#ffffff"))
    palette.setColor(role.ButtonText, QColor("#1c1c1e"))
    palette.setColor(role.BrightText, QColor("#ff3b30"))
    palette.setColor(role.Highlight, QColor("#007aff"))
    palette.setColor(role.HighlightedText, QColor("#ffffff"))
    palette.setColor(role.PlaceholderText, QColor("#8e8e93"))
    palette.setColor(role.Light, QColor("#ffffff"))
    palette.setColor(role.Midlight, QColor("#f2f2f7"))
    palette.setColor(role.Mid, QColor("#e5e5ea"))
    palette.setColor(role.Dark, QColor("#d1d1d6"))
    palette.setColor(role.Shadow, QColor("#c7c7cc"))
    for disabled_role in (role.WindowText, role.Text, role.ButtonText):
        palette.setColor(group.Disabled, disabled_role, QColor("#c7c7cc"))
    palette.setColor(group.Disabled, role.Base, QColor("#fafafa"))
    app.setPalette(palette)


def is_admin() -> bool:
    if os.name == "nt":
        try:
            return bool(ctypes.windll.shell32.IsUserAnAdmin())
        except OSError:
            return False
    return hasattr(os, "geteuid") and os.geteuid() == 0


def make_icon(color: str = "#38bdf8", size: int = 64) -> QIcon:
    pixmap = QPixmap(size, size)
    pixmap.fill(Qt.GlobalColor.transparent)
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(QColor("#0f172a"))
    painter.drawRoundedRect(2, 2, size - 4, size - 4, 14, 14)
    pen = QPen(
        QColor(color),
        max(3, size // 14),
        Qt.PenStyle.SolidLine,
        Qt.PenCapStyle.RoundCap,
        Qt.PenJoinStyle.RoundJoin,
    )
    painter.setPen(pen)
    painter.setBrush(Qt.BrushStyle.NoBrush)
    center_x = size // 2
    base_y = int(size * 0.72)
    for radius in (int(size * 0.18), int(size * 0.32), int(size * 0.46)):
        painter.drawArc(
            center_x - radius,
            base_y - radius,
            radius * 2,
            radius * 2,
            45 * 16,
            90 * 16,
        )
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(QColor(color))
    painter.drawEllipse(
        center_x - size // 16, base_y - size // 16, size // 8, size // 8
    )
    painter.end()
    return QIcon(pixmap)


class ToggleSwitch(QAbstractButton):
    """Small iOS-style switch used for the settings rows."""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setCheckable(True)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self._offset = 0.0
        self._animation = QPropertyAnimation(self, b"offset", self)
        self._animation.setDuration(160)
        self._animation.setEasingCurve(QEasingCurve.Type.OutCubic)
        self.toggled.connect(self._animate)
        self.setFixedSize(46, 28)

    def _animate(self, checked: bool) -> None:
        self._animation.stop()
        self._animation.setStartValue(self._offset)
        self._animation.setEndValue(1.0 if checked else 0.0)
        self._animation.start()

    def _get_offset(self) -> float:
        return self._offset

    def _set_offset(self, value: float) -> None:
        self._offset = float(value)
        self.update()

    offset = Property(float, _get_offset, _set_offset)

    def sizeHint(self) -> QSize:  # noqa: N802 - Qt API
        return QSize(46, 28)

    def paintEvent(self, event) -> None:  # noqa: N802 - Qt API
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = QRectF(1, 1, self.width() - 2, self.height() - 2)
        track = QColor("#34c759") if self.isChecked() else QColor("#e5e5ea")
        if not self.isEnabled():
            track = QColor("#e5e5ea")
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(track)
        painter.drawRoundedRect(rect, rect.height() / 2, rect.height() / 2)
        knob = rect.height() - 4
        x = rect.left() + 2 + (rect.width() - knob - 4) * self._offset
        painter.setBrush(QColor("#ffffff"))
        painter.drawEllipse(QRectF(x, rect.top() + 2, knob, knob))
        painter.end()

    def mouseReleaseEvent(self, event) -> None:  # noqa: N802 - Qt API
        if (
            self.isEnabled()
            and event.button() == Qt.MouseButton.LeftButton
            and self.rect().contains(event.position().toPoint())
        ):
            self.setChecked(not self.isChecked())
            event.accept()
            return
        super().mouseReleaseEvent(event)


def connectivity_target(url: str) -> tuple[str, int] | None:
    """Validate the user-configured health-check target.

    Only plain TCP is used; no HTTP request or redirect is performed. Loopback,
    link-local, multicast and cloud-metadata addresses are rejected so a
    misconfigured debug URL cannot probe local services.
    """
    try:
        parts = urlsplit(url)
    except ValueError:
        return None
    if parts.scheme not in ("http", "https") or not parts.hostname:
        return None
    port = parts.port or (443 if parts.scheme == "https" else 80)
    try:
        infos = socket.getaddrinfo(parts.hostname, port, 0, socket.SOCK_STREAM)
    except OSError:
        return None
    for info in infos:
        try:
            address = ipaddress.ip_address(info[4][0])
        except ValueError:
            return None
        if (
            address.is_loopback
            or address.is_link_local
            or address.is_multicast
            or address.is_unspecified
            or str(address) == "169.254.169.254"
        ):
            return None
    return parts.hostname, port


class _LogBridge(QObject):
    message = Signal(str)


class QtLogHandler(logging.Handler):
    def __init__(self, bridge: _LogBridge) -> None:
        super().__init__(logging.DEBUG)
        self.bridge = bridge
        self.setFormatter(
            logging.Formatter("%(asctime)s [%(levelname)s] %(message)s", "%H:%M:%S")
        )

    def emit(self, record: logging.LogRecord) -> None:
        try:
            self.bridge.message.emit(self.format(record))
        except Exception:  # noqa: BLE001 - logging must never crash the app
            pass


def setup_file_logging(log_dir: Path, verbose: bool) -> None:
    LOG.setLevel(logging.DEBUG)
    if any(getattr(handler, "_ruijie_gui_file", False) for handler in LOG.handlers):
        return
    log_dir.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(
        log_dir / "app.log", maxBytes=2 * 1024 * 1024, backupCount=3, encoding="utf-8"
    )
    handler.setLevel(logging.DEBUG if verbose else logging.INFO)
    handler.setFormatter(
        logging.Formatter("%(asctime)s [%(levelname)s] %(message)s", "%Y-%m-%d %H:%M:%S")
    )
    handler._ruijie_gui_file = True  # type: ignore[attr-defined]
    LOG.addHandler(handler)


class AccountDialog(QDialog):
    def __init__(self, parent: QWidget | None = None, account: Account | None = None):
        super().__init__(parent)
        self._account = account
        self.setWindowTitle("编辑账号" if account else "添加账号")
        self.setMinimumWidth(420)

        self.username_edit = QLineEdit(account.username if account else "")
        self.username_edit.setPlaceholderText("校园网用户名")
        self.password_edit = QLineEdit(account.password if account else "")
        self.password_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.password_edit.setPlaceholderText("校园网密码")
        self.note_edit = QLineEdit(account.note if account else "")
        self.note_edit.setPlaceholderText("备注（可选）")
        self.enabled_check = QCheckBox("启用此账号")
        self.enabled_check.setChecked(account.enabled if account else True)
        self.show_password = QToolButton()
        self.show_password.setText("显示")
        self.show_password.setCheckable(True)
        self.show_password.toggled.connect(self._toggle_password)

        password_row = QHBoxLayout()
        password_row.addWidget(self.password_edit, 1)
        password_row.addWidget(self.show_password)

        form = QFormLayout()
        form.addRow("用户名", self.username_edit)
        form.addRow("密码", password_row)
        form.addRow("备注", self.note_edit)
        form.addRow("", self.enabled_check)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok
            | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self._accept)
        buttons.rejected.connect(self.reject)

        layout = QVBoxLayout(self)
        layout.addLayout(form)
        layout.addWidget(buttons)

    def _toggle_password(self, shown: bool) -> None:
        self.password_edit.setEchoMode(
            QLineEdit.EchoMode.Normal if shown else QLineEdit.EchoMode.Password
        )

    def _accept(self) -> None:
        if not self.username_edit.text().strip():
            QMessageBox.warning(self, "账号", "用户名不能为空")
            return
        if not self.password_edit.text():
            QMessageBox.warning(self, "账号", "密码不能为空")
            return
        self.accept()

    def result_account(self) -> Account:
        account = replace(self._account) if self._account else Account()
        account.username = self.username_edit.text().strip()
        account.password = self.password_edit.text()
        account.note = self.note_edit.text().strip()
        account.enabled = self.enabled_check.isChecked()
        return account


class OneShotWorker(QThread):
    done = Signal(str)
    failed = Signal(str)

    def __init__(self, func: Callable[[], str], parent: QObject | None = None):
        super().__init__(parent)
        self._func = func

    def run(self) -> None:
        try:
            self.done.emit(str(self._func()))
        except BaseException as exc:  # noqa: BLE001 - show the real error
            LOG.exception("调试操作失败")
            self.failed.emit(str(exc))


class AuthWorker(QThread):
    state = Signal(str, str)
    details = Signal(str)
    channel = Signal(str)

    def __init__(
        self,
        account: Account,
        settings: AppSettings,
        discovery: DiscoveryCache | None = None,
        servers: ServerStore | None = None,
        parent=None,
    ):
        super().__init__(parent)
        self.account = replace(account)
        self.settings = replace(settings)
        self.discovery = discovery or DiscoveryCache(
            default_data_dir() / "discovery.json"
        )
        self.servers = servers or ServerStore(default_data_dir() / "servers.json")
        self._stop = threading.Event()
        self._force_rediscover = threading.Event()
        self._supp: Supplicant | None = None
        self._resolved: str | None = None
        self.ever_authenticated = False

    def stop(self) -> None:
        self._stop.set()
        supp = self._supp
        if supp is not None:
            supp.stop()

    def force_rediscover(self) -> None:
        self._force_rediscover.set()
        self.discovery.clear()
        supp = self._supp
        if supp is not None:
            supp.stop()

    def _wait_or_stop(self, seconds: float) -> bool:
        return self._stop.wait(seconds)

    def _cached_key(self, cached) -> str | None:
        if cached is None:
            return None
        for item in list_interfaces():
            if item.key == cached.interface:
                return item.key
        return None

    def _logon(self) -> bool:
        requested = self.settings.interface or None
        if self._force_rediscover.is_set():
            self._force_rediscover.clear()
            self._resolved = None
            self.discovery.clear()
            self.channel.emit("正在强制重新探测认证通道...")
            LOG.info("强制重新探测认证通道")

        if self._resolved is None:
            if requested:
                self.state.emit("selecting", "正在解析指定网卡...")
                self._resolved = choose_interface(requested, stop_event=self._stop)
            else:
                cached = self.discovery.load()
                cached_key = self._cached_key(cached)
                if cached and cached_key:
                    self._resolved = cached_key
                    label = cached.description or cached_key
                    suffix = f" → {cached.server_mac}" if cached.server_mac else ""
                    self.channel.emit(f"使用缓存通道: {label}{suffix}")
                    LOG.info("使用缓存认证通道: %s%s", label, suffix)
                else:
                    self.state.emit("selecting", "正在探测认证网卡...")
                    self._resolved = choose_interface(None, stop_event=self._stop)
            try:
                from scapy.interfaces import resolve_iface

                info = resolve_iface(self._resolved)
                self.details.emit(
                    f"{getattr(info, 'description', info.name)} | "
                    f"{getattr(info, 'ip', '-') or '-'} | "
                    f"{getattr(info, 'mac', '-') or '-'}"
                )
            except Exception:  # noqa: BLE001 - details are best effort
                pass

        locked_server = (
            self.settings.locked_server if self.settings.server_lock else None
        )
        if locked_server:
            self.channel.emit(f"锁定认证服务器: {locked_server}")
        self.state.emit("authenticating", f"正在认证 {self.account.username} ...")
        supp = Supplicant(
            interface=self._resolved,
            identity=self.account.username.encode("gb18030"),
            password=self.account.password.encode("gb18030"),
            timeout=self.settings.timeout,
            retries=self.settings.retries,
            repeats=self.settings.repeats,
            private=True,
            logoff_first=True,
            locked_server=locked_server,
        )
        self._supp = supp
        try:
            ok = supp.authenticate()
        finally:
            self._supp = None

        if supp.connected:
            try:
                from scapy.interfaces import resolve_iface

                info = resolve_iface(self._resolved)
                description = str(getattr(info, "description", info.name))
                mac = str(getattr(info, "mac", "") or "")
            except Exception:  # noqa: BLE001
                description = self._resolved
                mac = ""
            self.discovery.save(
                self._resolved,
                description=description,
                mac=mac,
                server_mac=supp.server_mac or "",
            )
            record = self.servers.upsert(
                supp.server_mac or "",
                interface=self._resolved,
                description=description,
                success=ok,
            )
            if record is not None and ok:
                LOG.info(
                    "已记录认证服务器: %s | %s | 成功 %d 次",
                    record.mac,
                    record.label,
                    record.success_count,
                )
            suffix = f" → {supp.server_mac}" if supp.server_mac else ""
            self.channel.emit(f"已缓存通道: {description}{suffix}")
            LOG.info(
                "已记录认证通道: %s | %s", description, supp.server_mac or "-"
            )
        else:
            self.discovery.clear()
            if not requested:
                self._resolved = None
            self.channel.emit("认证通道无响应，将重新探测")
            LOG.warning("认证服务器无响应，已清除认证通道缓存")
        return ok

    def _online(self) -> bool:
        url = self.settings.connectivity_url.strip()
        if not url:
            return True
        target = connectivity_target(url)
        if target is None:
            LOG.warning("连通检测地址无效或指向本机/链路本地地址，跳过检测")
            return True
        try:
            with socket.create_connection(target, timeout=3):
                return True
        except OSError:
            return False

    def run(self) -> None:
        if not self.account.username or not self.account.password:
            self.state.emit("error", "账号或密码为空")
            return
        while not self._stop.is_set():
            try:
                ok = self._logon()
            except SystemExit as exc:
                if self._stop.is_set():
                    break
                self.state.emit("error", str(exc))
                self._resolved = None
                self.discovery.clear()
                if not self.settings.preempt:
                    break
                if self._wait_or_stop(self.settings.retry_interval):
                    break
                continue
            except Exception as exc:  # noqa: BLE001 - retry forever in preempt mode
                LOG.exception("认证线程异常")
                if self._stop.is_set():
                    break
                self.state.emit("error", f"认证异常: {exc}")
                self._resolved = None
                self.discovery.clear()
                if not self.settings.preempt:
                    break
                if self._wait_or_stop(self.settings.retry_interval):
                    break
                continue

            if self._stop.is_set():
                break
            if ok:
                self.ever_authenticated = True
                self.state.emit("authenticated", "认证成功")
                if not self.settings.preempt:
                    break
                while not self._stop.is_set():
                    if self._online():
                        self.state.emit("authenticated", "在线")
                        if self._wait_or_stop(self.settings.check_interval):
                            break
                    else:
                        self.state.emit("offline", "检测到掉线，立即抢占...")
                        break
            else:
                if not self.settings.preempt:
                    self.state.emit("failed", "认证失败")
                    break
                self.state.emit(
                    "retrying",
                    f"认证失败，{self.settings.retry_interval:g}s 后重试",
                )
                if self._wait_or_stop(self.settings.retry_interval):
                    break
        if self._stop.is_set():
            self.state.emit("stopped", "已停止")


class DebugPanel(QWidget):
    def __init__(self, controller: "MainWindow") -> None:
        super().__init__()
        self.controller = controller
        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(10)

        interface_row = QHBoxLayout()
        self.interface_combo = QComboBox()
        self.interface_combo.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        self.interface_combo.setMinimumContentsLength(8)
        self.interface_combo.setSizePolicy(
            QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed
        )
        self.refresh_button = QPushButton("刷新网卡")
        interface_row.addWidget(QLabel("网卡"))
        interface_row.addWidget(self.interface_combo, 1)
        interface_row.addWidget(self.refresh_button)
        layout.addLayout(interface_row)

        option_row = QHBoxLayout()
        option_row.addWidget(QLabel("附加锐捷私有尾"))
        self.private_check = ToggleSwitch()
        self.private_check.setChecked(True)
        option_row.addWidget(self.private_check)
        option_row.addStretch()
        layout.addLayout(option_row)

        self.mac_edit = QLineEdit()
        self.mac_edit.setPlaceholderText("源 MAC 覆盖（可选，如 00:e0:4c:68:00:c6）")
        layout.addWidget(self.mac_edit)

        action_grid = QGridLayout()
        action_grid.setHorizontalSpacing(6)
        action_grid.setVerticalSpacing(6)
        self.probe_button = QPushButton("探测服务器")
        self.logoff_button = QPushButton("发 Logoff")
        self.start_button = QPushButton("发送 Start")
        self.manual_button = QPushButton("手动认证一次")
        action_grid.addWidget(self.probe_button, 0, 0)
        action_grid.addWidget(self.logoff_button, 0, 1)
        action_grid.addWidget(self.start_button, 1, 0)
        action_grid.addWidget(self.manual_button, 1, 1)
        layout.addLayout(action_grid)

        raw_group = QGroupBox("原始 EAPOL / 以太帧")
        raw_layout = QVBoxLayout(raw_group)
        raw_layout.setContentsMargins(4, 0, 4, 4)
        self.raw_edit = QPlainTextEdit()
        self.raw_edit.setPlaceholderText("粘贴完整以太网帧十六进制（可带空格/冒号）")
        self.raw_edit.setMinimumHeight(64)
        self.raw_send_button = QPushButton("发送原始帧")
        raw_layout.addWidget(self.raw_edit)
        raw_layout.addWidget(self.raw_send_button)
        layout.addWidget(raw_group)

        tail_group = QGroupBox("私有尾预览")
        tail_layout = QVBoxLayout(tail_group)
        tail_layout.setContentsMargins(4, 0, 4, 4)
        self.tail_button = QPushButton("生成当前账号的 Identity 私有尾")
        self.tail_output = QPlainTextEdit()
        self.tail_output.setReadOnly(True)
        self.tail_output.setMinimumHeight(72)
        self.tail_output.setMaximumBlockCount(2000)
        tail_layout.addWidget(self.tail_button)
        tail_layout.addWidget(self.tail_output)
        layout.addWidget(tail_group)

        self.output = QPlainTextEdit()
        self.output.setReadOnly(True)
        self.output.setMinimumHeight(72)
        self.output.setMaximumBlockCount(2000)
        layout.addWidget(self.output)

        self.refresh_button.clicked.connect(self.refresh_interfaces)
        self.probe_button.clicked.connect(controller.debug_probe)
        self.logoff_button.clicked.connect(controller.debug_logoff)
        self.start_button.clicked.connect(controller.debug_start)
        self.manual_button.clicked.connect(controller.debug_manual_auth)
        self.raw_send_button.clicked.connect(controller.debug_raw_send)
        self.tail_button.clicked.connect(controller.debug_tail_preview)
        self.refresh_interfaces()

    def refresh_interfaces(self) -> None:
        current = self.interface_combo.currentData()
        self.interface_combo.blockSignals(True)
        self.interface_combo.clear()
        self.interface_combo.addItem("自动探测", "")
        for item in list_interfaces():
            self.interface_combo.addItem(
                f"{item.description} | {item.ip or '-'} | {item.mac}", item.key
            )
        index = self.interface_combo.findData(current or "")
        if index >= 0:
            self.interface_combo.setCurrentIndex(index)
        self.interface_combo.blockSignals(False)

    def interface_key(self) -> str:
        return str(self.interface_combo.currentData() or "")

    def append_output(self, text: str) -> None:
        self.output.appendPlainText(text)

    def set_running(self, running: bool) -> None:
        for widget in (
            self.logoff_button,
            self.start_button,
            self.manual_button,
        ):
            widget.setEnabled(not running)


class MainWindow(QMainWindow):
    def __init__(
        self,
        store: AccountStore,
        settings: AppSettings,
        args: argparse.Namespace,
    ) -> None:
        super().__init__()
        self.store = store
        self.settings = settings
        self.args = args
        self.accounts = store.load_accounts()
        self.discovery = DiscoveryCache(store.base_dir / "discovery.json")
        self.servers = ServerStore(store.base_dir / "servers.json")
        self.servers.import_discovery(self.discovery.path)
        self._server_records: list[ServerRecord] = []
        self._updating_server_table = False
        self.worker: AuthWorker | None = None
        self.one_shot: OneShotWorker | None = None
        self.tray: QSystemTrayIcon | None = None
        self.blocker = ProcessBlocker(program_data_dir() / "blocker_state.json")
        self.takeover_state = TakeoverState()
        self._last_state = "idle"
        self._last_account: Account | None = None
        self._logoff_after_stop = False
        self._shutting_down = False
        self._loading = True
        self._updating_table = False
        self._notified_online = False
        self._notified_offline = False

        self.log_bridge = _LogBridge(self)
        self.log_handler = QtLogHandler(self.log_bridge)
        self.log_handler.setLevel(
            logging.DEBUG if settings.verbose else logging.INFO
        )
        LOG.addHandler(self.log_handler)
        self.log_bridge.message.connect(self.append_log)

        self.setWindowTitle(f"{APP_NAME} {APP_VERSION}")
        self.setWindowIcon(make_icon())
        self.setWindowFlag(Qt.WindowType.WindowMaximizeButtonHint, False)
        self.setFixedSize(880, 620)
        screen = QApplication.primaryScreen()
        if screen is not None:
            self.move(
                screen.availableGeometry().center() - self.rect().center()
            )
        self._build_ui()
        self._refresh_interfaces()
        self._apply_settings()
        self._refresh_accounts()
        self._setup_tray()
        self._update_admin_badge()
        self._connect_signals()
        self._loading = False
        self.statusBar().showMessage(f"数据目录: {self.store.base_dir}")
        QTimer.singleShot(0, self._start_blocker_if_enabled)

    def _build_ui(self) -> None:
        central = QWidget()
        central.setObjectName("root")
        root = QVBoxLayout(central)
        root.setContentsMargins(16, 14, 16, 10)
        root.setSpacing(10)

        header = QHBoxLayout()
        title_box = QVBoxLayout()
        title_box.setSpacing(1)
        title = QLabel("校园网认证")
        title.setObjectName("title")
        subtitle = QLabel("HXXY · 锐捷 EAP-MD5 · 自动托管")
        subtitle.setObjectName("muted")
        title_box.addWidget(title)
        title_box.addWidget(subtitle)
        header.addLayout(title_box)
        header.addStretch()
        self.admin_badge = QLabel()
        self.admin_badge.setObjectName("muted")
        self.admin_button = QPushButton("以管理员重启")
        self.admin_button.setObjectName("link")
        self.admin_button.clicked.connect(self.relaunch_as_admin)
        header.addWidget(self.admin_badge)
        header.addWidget(self.admin_button)
        root.addLayout(header)

        body = QHBoxLayout()
        body.setSpacing(10)
        self.tabs = QTabWidget()
        self.tabs.setObjectName("sidebar")
        self.tabs.setDocumentMode(True)
        self.tabs.setFixedWidth(440)
        self.tabs.addTab(self._build_accounts_page(), "账号")
        self.tabs.addTab(self._build_auth_page(), "认证")
        self.tabs.addTab(self._build_connection_page(), "连接")
        self.tabs.addTab(self._build_server_page(), "服务器")
        self.tabs.addTab(self._build_debug_page(), "调试")
        body.addWidget(self.tabs, 0)
        body.addWidget(self._build_log_page(), 1)
        root.addLayout(body, 1)
        self.setCentralWidget(central)

    def _switch_row(
        self, title: str, description: str, switch: ToggleSwitch
    ) -> QWidget:
        row = QWidget()
        layout = QHBoxLayout(row)
        layout.setContentsMargins(4, 4, 4, 4)
        layout.setSpacing(8)
        texts = QVBoxLayout()
        texts.setSpacing(0)
        label = QLabel(title)
        label.setObjectName("value")
        texts.addWidget(label)
        if description:
            detail = QLabel(description)
            detail.setObjectName("muted")
            detail.setWordWrap(True)
            detail.setSizePolicy(
                QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred
            )
            detail.setMinimumWidth(0)
            texts.addWidget(detail)
        layout.addLayout(texts, 1)
        layout.addWidget(switch, 0, Qt.AlignmentFlag.AlignVCenter)
        return row

    def _compact_switch_cell(
        self, title: str, switch: ToggleSwitch
    ) -> QWidget:
        cell = QWidget()
        layout = QHBoxLayout(cell)
        layout.setContentsMargins(4, 4, 4, 4)
        layout.setSpacing(6)
        label = QLabel(title)
        label.setObjectName("value")
        layout.addWidget(label, 1)
        layout.addWidget(switch, 0, Qt.AlignmentFlag.AlignVCenter)
        return cell

    def _build_accounts_page(self) -> QWidget:
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(8)
        hint = QLabel("多账号管理 · 密码由 Windows DPAPI 加密")
        hint.setObjectName("muted")
        layout.addWidget(hint)

        self.account_table = QTableWidget(0, 4)
        self.account_table.setHorizontalHeaderLabels(["启用", "用户名", "备注", "状态"])
        self.account_table.verticalHeader().setVisible(False)
        self.account_table.setShowGrid(False)
        self.account_table.setMinimumHeight(160)
        self.account_table.setSelectionBehavior(
            QAbstractItemView.SelectionBehavior.SelectRows
        )
        self.account_table.setSelectionMode(
            QAbstractItemView.SelectionMode.SingleSelection
        )
        self.account_table.setEditTriggers(
            QAbstractItemView.EditTrigger.NoEditTriggers
        )
        self.account_table.setAlternatingRowColors(False)
        self.account_table.setWordWrap(False)
        self.account_table.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )
        self.account_table.setVerticalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )
        self.account_table.setHorizontalScrollMode(
            QAbstractItemView.ScrollMode.ScrollPerPixel
        )
        self.account_table.setVerticalScrollMode(
            QAbstractItemView.ScrollMode.ScrollPerPixel
        )
        header_view = self.account_table.horizontalHeader()
        header_view.setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        header_view.setStretchLastSection(False)
        self.account_table.setColumnWidth(0, 52)
        self.account_table.setColumnWidth(1, 130)
        self.account_table.setColumnWidth(2, 180)
        self.account_table.setColumnWidth(3, 70)
        layout.addWidget(self.account_table, 1)

        self.add_button = QPushButton("添加")
        self.edit_button = QPushButton("编辑")
        self.delete_button = QPushButton("删除")
        self.test_button = QPushButton("测试登录")
        buttons = QGridLayout()
        buttons.setHorizontalSpacing(6)
        buttons.setVerticalSpacing(6)
        buttons.addWidget(self.add_button, 0, 0)
        buttons.addWidget(self.edit_button, 0, 1)
        buttons.addWidget(self.delete_button, 0, 2)
        buttons.addWidget(self.test_button, 1, 0, 1, 3)
        layout.addLayout(buttons)

        self.active_label = QLabel("当前账号: 未选择")
        self.active_label.setObjectName("muted")
        layout.addWidget(self.active_label)
        return page

    def _build_auth_page(self) -> QWidget:
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(10)

        self.toggle_button = QPushButton("开始认证")
        self.toggle_button.setObjectName("primary")
        self.toggle_button.setMinimumHeight(44)
        layout.addWidget(self.toggle_button)

        policy = QGroupBox("认证策略")
        policy_layout = QVBoxLayout(policy)
        policy_layout.setContentsMargins(4, 0, 4, 4)
        policy_layout.setSpacing(0)
        self.preempt_check = ToggleSwitch()
        policy_layout.addWidget(
            self._switch_row("抢占模式", "掉线后立即补入并无限重试", self.preempt_check)
        )
        self.block_check = ToggleSwitch()
        self.block_check.setToolTip(
            "开启后每 0.5 秒结束 8021x.exe，并写入临时 IFEO 策略阻止其启动。\n"
            "关闭开关、停止认证或退出程序时自动恢复。"
        )
        policy_layout.addWidget(
            self._switch_row("托管模式", "持续阻止原客户端 8021x.exe", self.block_check)
        )
        self.block_status = QLabel("")
        self.block_status.setObjectName("muted")
        self.block_status.setContentsMargins(50, 0, 4, 4)
        policy_layout.addWidget(self.block_status)

        self.auto_start_check = ToggleSwitch()
        self.start_minimized_check = ToggleSwitch()
        self.minimize_check = ToggleSwitch()
        self.verbose_check = ToggleSwitch()
        self.logoff_on_stop_check = ToggleSwitch()
        secondary = QGridLayout()
        secondary.setHorizontalSpacing(4)
        secondary.setVerticalSpacing(2)
        compact_items = [
            ("启动后自动认证", self.auto_start_check),
            ("启动时最小化", self.start_minimized_check),
            ("关闭窗口最小化", self.minimize_check),
            ("详细协议日志", self.verbose_check),
            ("停止时发送注销", self.logoff_on_stop_check),
        ]
        for index, (caption, switch) in enumerate(compact_items):
            secondary.addWidget(
                self._compact_switch_cell(caption, switch),
                index // 2,
                index % 2,
            )
        policy_layout.addLayout(secondary)
        layout.addWidget(policy)
        layout.addStretch(1)
        scroll.setWidget(page)
        return scroll

    def _build_connection_page(self) -> QWidget:
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(10)

        settings = QGroupBox("连接设置")
        self.settings_group = settings
        form = QFormLayout(settings)
        form.setContentsMargins(10, 4, 10, 8)
        form.setHorizontalSpacing(10)
        form.setVerticalSpacing(8)
        form.setFieldGrowthPolicy(
            QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow
        )

        self.interface_combo = QComboBox()
        self.interface_combo.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        self.interface_combo.setMinimumContentsLength(8)
        self.interface_combo.setSizePolicy(
            QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed
        )
        self.interface_refresh = QPushButton("刷新")
        interface_row = QHBoxLayout()
        interface_row.setSpacing(6)
        interface_row.addWidget(self.interface_combo, 1)
        interface_row.addWidget(self.interface_refresh)
        interface_widget = QWidget()
        interface_widget.setLayout(interface_row)
        form.addRow("网卡", interface_widget)

        self.check_spin = QDoubleSpinBox()
        self.check_spin.setRange(0.5, 120.0)
        self.check_spin.setDecimals(1)
        self.check_spin.setSuffix(" s")
        self.retry_spin = QDoubleSpinBox()
        self.retry_spin.setRange(0.5, 120.0)
        self.retry_spin.setDecimals(1)
        self.retry_spin.setSuffix(" s")
        self.timeout_spin = QDoubleSpinBox()
        self.timeout_spin.setRange(5.0, 120.0)
        self.timeout_spin.setDecimals(0)
        self.timeout_spin.setSuffix(" s")
        self.retries_spin = QSpinBox()
        self.retries_spin.setRange(1, 10)
        self.repeats_spin = QSpinBox()
        self.repeats_spin.setRange(1, 10)
        form.addRow("掉线检测", self.check_spin)
        form.addRow("失败重试", self.retry_spin)
        form.addRow("认证超时", self.timeout_spin)
        form.addRow("Start 次数", self.retries_spin)
        form.addRow("应答次数", self.repeats_spin)

        self.url_edit = QLineEdit()
        self.url_edit.setPlaceholderText("http://www.baidu.com/")
        form.addRow("连通检测", self.url_edit)
        layout.addWidget(settings)

        channel = QGroupBox("认证通道")
        channel_layout = QVBoxLayout(channel)
        channel_layout.setContentsMargins(8, 0, 8, 6)
        channel_layout.setSpacing(4)
        self.cache_label = QLabel("认证通道: 自动探测")
        self.cache_label.setObjectName("muted")
        self.cache_label.setWordWrap(True)
        self.rediscover_button = QPushButton("强制重新探测")
        channel_layout.addWidget(self.cache_label)
        channel_layout.addWidget(self.rediscover_button)
        layout.insertWidget(0, channel)
        layout.addStretch(1)
        scroll.setWidget(page)
        return scroll

    def _build_server_page(self) -> QWidget:
        page = QWidget()
        self.server_page = page
        layout = QVBoxLayout(page)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(8)

        hint = QLabel("认证服务器列表 · 应答过的服务器会自动记录")
        hint.setObjectName("muted")
        hint.setWordWrap(True)
        layout.addWidget(hint)

        self.server_table = QTableWidget(0, 4)
        self.server_table.setHorizontalHeaderLabels(
            ["锁定", "服务器 MAC", "名称 / 描述", "成功"]
        )
        self.server_table.verticalHeader().setVisible(False)
        self.server_table.setShowGrid(False)
        self.server_table.setMinimumHeight(190)
        self.server_table.setSelectionBehavior(
            QAbstractItemView.SelectionBehavior.SelectRows
        )
        self.server_table.setSelectionMode(
            QAbstractItemView.SelectionMode.SingleSelection
        )
        self.server_table.setEditTriggers(
            QAbstractItemView.EditTrigger.NoEditTriggers
        )
        self.server_table.setWordWrap(False)
        self.server_table.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )
        self.server_table.setVerticalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )
        self.server_table.setHorizontalScrollMode(
            QAbstractItemView.ScrollMode.ScrollPerPixel
        )
        self.server_table.setVerticalScrollMode(
            QAbstractItemView.ScrollMode.ScrollPerPixel
        )
        header_view = self.server_table.horizontalHeader()
        header_view.setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        header_view.setStretchLastSection(False)
        self.server_table.setColumnWidth(0, 40)
        self.server_table.setColumnWidth(1, 150)
        self.server_table.setColumnWidth(2, 130)
        self.server_table.setColumnWidth(3, 80)
        layout.addWidget(self.server_table, 1)

        self.server_add_button = QPushButton("添加")
        self.server_rename_button = QPushButton("重命名")
        self.server_delete_button = QPushButton("删除")
        self.server_refresh_button = QPushButton("刷新")
        buttons = QGridLayout()
        buttons.setHorizontalSpacing(6)
        buttons.setVerticalSpacing(6)
        buttons.addWidget(self.server_add_button, 0, 0)
        buttons.addWidget(self.server_rename_button, 0, 1)
        buttons.addWidget(self.server_delete_button, 0, 2)
        buttons.addWidget(self.server_refresh_button, 0, 3)
        layout.addLayout(buttons)

        lock_group = QGroupBox("服务器锁定")
        lock_layout = QVBoxLayout(lock_group)
        lock_layout.setContentsMargins(4, 0, 4, 4)
        lock_layout.setSpacing(2)
        self.server_lock_check = ToggleSwitch()
        lock_layout.addWidget(
            self._switch_row(
                "锁定服务器发包",
                "EAPOL-Start 单播到选中服务器，忽略其他服务器；Logoff 仍走标准组播",
                self.server_lock_check,
            )
        )
        self.server_status = QLabel("未锁定: 自动发现，EAPOL 发往组播地址")
        self.server_status.setObjectName("muted")
        self.server_status.setWordWrap(True)
        self.server_status.setContentsMargins(50, 0, 4, 2)
        lock_layout.addWidget(self.server_status)
        layout.addWidget(lock_group)
        return page

    def _build_debug_page(self) -> QWidget:
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.debug_panel = DebugPanel(self)
        scroll.setWidget(self.debug_panel)
        return scroll

    def _build_log_page(self) -> QWidget:
        card = QFrame()
        card.setObjectName("card")
        layout = QVBoxLayout(card)
        layout.setContentsMargins(14, 12, 14, 12)
        layout.setSpacing(8)

        header = QHBoxLayout()
        title = QLabel("运行日志")
        title.setStyleSheet("font-size: 16px; font-weight: 700;")
        self.clear_log_button = QPushButton("清空")
        self.clear_log_button.setObjectName("link")
        self.open_log_button = QPushButton("日志目录")
        self.open_log_button.setObjectName("link")
        header.addWidget(title)
        header.addStretch()
        header.addWidget(self.clear_log_button)
        header.addWidget(self.open_log_button)
        layout.addLayout(header)

        self.state_label = QLabel("未启动")
        self.state_label.setObjectName("state")
        self.state_label.setStyleSheet("color: #8e8e93;")
        self.detail_label = QLabel("选择一个账号，然后开始认证。")
        self.detail_label.setObjectName("muted")
        self.detail_label.setWordWrap(True)
        self.interface_label = QLabel("网卡: 自动探测")
        self.interface_label.setObjectName("muted")
        layout.addWidget(self.state_label)
        layout.addWidget(self.detail_label)
        layout.addWidget(self.interface_label)

        self.log_view = QPlainTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setMaximumBlockCount(5000)
        layout.addWidget(self.log_view, 1)
        return card

    def _setup_tray(self) -> None:
        if not QSystemTrayIcon.isSystemTrayAvailable():
            self.tray = None
            return
        self.tray = QSystemTrayIcon(make_icon(), self)
        menu = QMenu()
        menu.setStyleSheet(
            "QMenu { background: #ffffff; border: 1px solid #e5e5ea; padding: 4px; }"
            "QMenu::item { padding: 7px 26px 7px 24px; border-radius: 6px; }"
            "QMenu::item:selected { background: #e5efff; color: #1c1c1e; }"
            "QMenu::separator { height: 1px; background: #e5e5ea; margin: 4px 8px; }"
        )
        show_action = QAction("显示主窗口", self)
        show_action.triggered.connect(self.show_window)
        self.tray_toggle_action = QAction("开始认证", self)
        self.tray_toggle_action.triggered.connect(self.toggle_auth)
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.quit_app)
        menu.addAction(show_action)
        menu.addAction(self.tray_toggle_action)
        menu.addSeparator()
        menu.addAction(quit_action)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(self._on_tray_activated)
        self.tray.setToolTip(f"{APP_NAME} - 未启动")
        self.tray.show()

    def _connect_signals(self) -> None:
        self.add_button.clicked.connect(self.add_account)
        self.edit_button.clicked.connect(self.edit_account)
        self.delete_button.clicked.connect(self.delete_account)
        self.test_button.clicked.connect(self.test_account)
        self.account_table.itemSelectionChanged.connect(self._on_account_selected)
        self.account_table.itemChanged.connect(self._on_account_item_changed)
        self.account_table.doubleClicked.connect(lambda _index: self.edit_account())
        self.toggle_button.clicked.connect(self.toggle_auth)
        self.interface_refresh.clicked.connect(self.refresh_interfaces)
        self.rediscover_button.clicked.connect(self.force_rediscover)
        self.server_table.itemSelectionChanged.connect(self._on_server_selected)
        self.server_table.doubleClicked.connect(lambda _index: self.rename_server())
        self.server_add_button.clicked.connect(self.add_server)
        self.server_rename_button.clicked.connect(self.rename_server)
        self.server_delete_button.clicked.connect(self.delete_server)
        self.server_refresh_button.clicked.connect(self._refresh_servers)
        self.server_lock_check.toggled.connect(self._on_server_lock_toggled)
        self.tabs.currentChanged.connect(self._on_tab_changed)
        self.clear_log_button.clicked.connect(self.log_view.clear)
        self.open_log_button.clicked.connect(self.open_log_dir)
        for widget in (
            self.check_spin,
            self.retry_spin,
            self.timeout_spin,
            self.retries_spin,
            self.repeats_spin,
        ):
            widget.valueChanged.connect(self._save_settings_from_ui)
        self.interface_combo.currentIndexChanged.connect(self._interface_changed)
        self.url_edit.textChanged.connect(self._save_settings_from_ui)
        for check in (
            self.preempt_check,
            self.auto_start_check,
            self.start_minimized_check,
            self.minimize_check,
            self.verbose_check,
            self.logoff_on_stop_check,
        ):
            check.toggled.connect(self._save_settings_from_ui)
        self.block_check.toggled.connect(self._on_block_toggled)

    def _apply_settings(self) -> None:
        self.check_spin.setValue(self.settings.check_interval)
        self.retry_spin.setValue(self.settings.retry_interval)
        self.timeout_spin.setValue(self.settings.timeout)
        self.retries_spin.setValue(self.settings.retries)
        self.repeats_spin.setValue(self.settings.repeats)
        self.url_edit.setText(self.settings.connectivity_url)
        self.preempt_check.setChecked(self.settings.preempt)
        self.auto_start_check.setChecked(self.settings.auto_start)
        self.start_minimized_check.setChecked(self.settings.start_minimized)
        self.minimize_check.setChecked(self.settings.minimize_to_tray)
        self.verbose_check.setChecked(self.settings.verbose)
        self.logoff_on_stop_check.setChecked(self.settings.logoff_on_stop)
        self.block_check.setChecked(self.settings.block_official_client)
        self.server_lock_check.setChecked(self.settings.server_lock)
        self._select_interface(self.settings.interface)
        self._refresh_servers()
        self._update_cache_label()

    def _save_settings_from_ui(self) -> None:
        if self._loading:
            return
        self.settings.check_interval = self.check_spin.value()
        self.settings.retry_interval = self.retry_spin.value()
        self.settings.timeout = self.timeout_spin.value()
        self.settings.retries = self.retries_spin.value()
        self.settings.repeats = self.repeats_spin.value()
        self.settings.connectivity_url = self.url_edit.text().strip()
        self.settings.preempt = self.preempt_check.isChecked()
        self.settings.auto_start = self.auto_start_check.isChecked()
        self.settings.start_minimized = self.start_minimized_check.isChecked()
        self.settings.minimize_to_tray = self.minimize_check.isChecked()
        self.settings.verbose = self.verbose_check.isChecked()
        self.settings.logoff_on_stop = self.logoff_on_stop_check.isChecked()
        self.settings.block_official_client = self.block_check.isChecked()
        self.settings.server_lock = self.server_lock_check.isChecked()
        self.settings.interface = str(self.interface_combo.currentData() or "")
        self.log_handler.setLevel(
            logging.DEBUG if self.settings.verbose else logging.INFO
        )
        self.store.save_settings(self.settings)

    def _interface_changed(self) -> None:
        self._save_settings_from_ui()
        self._update_cache_label()

    def _update_cache_label(self) -> None:
        lock_suffix = (
            f" | 锁定服务器 {self.settings.locked_server}"
            if self.settings.server_lock and self.settings.locked_server
            else ""
        )
        if self.settings.interface:
            self.cache_label.setText(
                "认证通道: 指定网卡（不使用自动探测缓存）" + lock_suffix
            )
            return
        info = self.discovery.load()
        if info:
            description = info.description or info.interface
            suffix = f" → {info.server_mac}" if info.server_mac else ""
            self.cache_label.setText(
                f"已缓存通道: {description}{suffix}{lock_suffix}"
            )
        else:
            self.cache_label.setText("认证通道: 自动探测" + lock_suffix)

    def force_rediscover(self) -> None:
        if self.worker and self.worker.isRunning():
            self.worker.force_rediscover()
            self.cache_label.setText("正在强制重新探测认证通道...")
            LOG.info("已请求强制重新探测认证通道")
        else:
            self.discovery.clear()
            self._update_cache_label()
            LOG.info("已清除认证通道缓存")

    @staticmethod
    def _short_time(value: str) -> str:
        if not value:
            return ""
        try:
            return datetime.fromisoformat(value).strftime("%m-%d %H:%M")
        except ValueError:
            return value[:16]

    def _refresh_servers(self) -> None:
        try:
            records = self.servers.load()
        except OSError as exc:
            LOG.warning("读取服务器列表失败: %s", exc)
            records = []
        self._server_records = records
        self._updating_server_table = True
        self.server_table.blockSignals(True)
        self.server_table.setRowCount(len(records))
        locked_mac = self.settings.locked_server if self.settings.server_lock else ""
        for row, record in enumerate(records):
            lock_item = QTableWidgetItem("✓" if record.mac == locked_mac else "")
            lock_item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            lock_item.setFlags(
                Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsSelectable
            )
            self.server_table.setItem(row, 0, lock_item)
            self.server_table.setItem(row, 1, QTableWidgetItem(record.mac))
            self.server_table.setItem(
                row,
                2,
                QTableWidgetItem(record.name or record.description or "—"),
            )
            success = f"{record.success_count} 次" if record.success_count else "发现"
            success_item = QTableWidgetItem(success)
            if record.last_success:
                success_item.setToolTip(
                    f"最近成功: {self._short_time(record.last_success)}"
                )
            elif record.last_seen:
                success_item.setToolTip(
                    f"最近发现: {self._short_time(record.last_seen)}"
                )
            self.server_table.setItem(row, 3, success_item)
        if records:
            target = 0
            for row, record in enumerate(records):
                if record.mac == locked_mac:
                    target = row
                    break
            self.server_table.setCurrentCell(target, 0)
        self.server_table.blockSignals(False)
        self._updating_server_table = False
        self._update_server_status()

    def _selected_server(self) -> ServerRecord | None:
        row = self.server_table.currentRow()
        if 0 <= row < len(self._server_records):
            return self._server_records[row]
        return None

    def _update_server_status(self) -> None:
        record = next(
            (
                item
                for item in self._server_records
                if item.mac == self.settings.locked_server
            ),
            None,
        )
        if self.settings.server_lock and record is not None:
            self.server_status.setText(
                f"已锁定: {record.mac} | {record.label} | 成功 {record.success_count} 次"
            )
        elif self.settings.server_lock:
            self.server_status.setText(f"已锁定: {self.settings.locked_server}")
        else:
            self.server_status.setText("未锁定: 自动发现，EAPOL 发往组播地址")

    def _on_server_selected(self) -> None:
        if self._updating_server_table:
            return
        record = self._selected_server()
        if record is not None and self.settings.server_lock:
            if record.mac != self.settings.locked_server:
                self.settings.locked_server = record.mac
                self.store.save_settings(self.settings)
                self._refresh_servers()
                self._update_cache_label()
                return
        self._update_server_status()

    def _on_server_lock_toggled(self, checked: bool) -> None:
        if self._loading or self._updating_server_table:
            return
        if checked:
            record = self._selected_server()
            if record is None:
                record = next(
                    (
                        item
                        for item in self._server_records
                        if item.mac == self.settings.locked_server
                    ),
                    None,
                )
            if record is None:
                self.server_lock_check.blockSignals(True)
                self.server_lock_check.setChecked(False)
                self.server_lock_check.blockSignals(False)
                QMessageBox.information(self, "服务器锁定", "请先在列表中选择一个服务器")
                return
            self.settings.server_lock = True
            self.settings.locked_server = record.mac
        else:
            self.settings.server_lock = False
        self.store.save_settings(self.settings)
        self._refresh_servers()
        self._update_cache_label()

    def _on_tab_changed(self, index: int) -> None:
        if self.tabs.widget(index) is getattr(self, "server_page", None):
            self._refresh_servers()

    def add_server(self) -> None:
        mac_text, ok = QInputDialog.getText(
            self, "添加服务器", "服务器 MAC（如 00:1a:a9:17:ff:ff）"
        )
        if not ok:
            return
        mac = normalize_mac(mac_text)
        if not mac:
            QMessageBox.warning(self, "添加服务器", "MAC 地址格式无效")
            return
        name, ok = QInputDialog.getText(self, "服务器名称", "名称（可留空）")
        self.servers.upsert(mac, name=name.strip() if ok else None)
        self._refresh_servers()
        LOG.info("已添加认证服务器: %s", mac)

    def rename_server(self) -> None:
        record = self._selected_server()
        if record is None:
            QMessageBox.information(self, "重命名", "请先选择一个服务器")
            return
        name, ok = QInputDialog.getText(
            self, "重命名服务器", "名称", text=record.name
        )
        if not ok:
            return
        self.servers.rename(record.mac, name)
        self._refresh_servers()

    def delete_server(self) -> None:
        record = self._selected_server()
        if record is None:
            QMessageBox.information(self, "删除服务器", "请先选择一个服务器")
            return
        answer = QMessageBox.question(
            self, "删除服务器", f"从列表中删除 {record.mac}？"
        )
        if answer != QMessageBox.StandardButton.Yes:
            return
        self.servers.remove(record.mac)
        if self.settings.locked_server == record.mac:
            self.settings.server_lock = False
            self.settings.locked_server = ""
            self.store.save_settings(self.settings)
            self.server_lock_check.blockSignals(True)
            self.server_lock_check.setChecked(False)
            self.server_lock_check.blockSignals(False)
        self._refresh_servers()
        self._update_cache_label()

    def open_log_dir(self) -> None:
        QDesktopServices.openUrl(QUrl.fromLocalFile(str(self.store.logs_dir)))

    def _refresh_interfaces(self) -> None:
        current = str(self.interface_combo.currentData() or self.settings.interface)
        self.interface_combo.blockSignals(True)
        self.interface_combo.clear()
        self.interface_combo.addItem("自动探测", "")
        for item in list_interfaces():
            self.interface_combo.addItem(
                f"{item.description} | {item.ip or '-'} | {item.mac}", item.key
            )
        index = self.interface_combo.findData(current)
        if index >= 0:
            self.interface_combo.setCurrentIndex(index)
        self.interface_combo.blockSignals(False)
        self.debug_panel.refresh_interfaces()

    def refresh_interfaces(self) -> None:
        self._refresh_interfaces()
        self._save_settings_from_ui()

    def _select_interface(self, key: str | None) -> None:
        index = self.interface_combo.findData(key or "")
        if index < 0 and key:
            index = 0
        self.interface_combo.setCurrentIndex(max(0, index))

    def _refresh_accounts(self) -> None:
        self._updating_table = True
        self.account_table.setRowCount(len(self.accounts))
        active = self._active_account()
        active_id = active.id if active else None
        for row, account in enumerate(self.accounts):
            enabled_item = QTableWidgetItem()
            enabled_item.setFlags(
                Qt.ItemFlag.ItemIsUserCheckable
                | Qt.ItemFlag.ItemIsEnabled
                | Qt.ItemFlag.ItemIsSelectable
            )
            enabled_item.setCheckState(
                Qt.CheckState.Checked if account.enabled else Qt.CheckState.Unchecked
            )
            self.account_table.setItem(row, 0, enabled_item)

            user_item = QTableWidgetItem(account.username)
            user_item.setData(Qt.ItemDataRole.UserRole, account.id)
            self.account_table.setItem(row, 1, user_item)
            self.account_table.setItem(row, 2, QTableWidgetItem(account.note))

            status_item = QTableWidgetItem("● 当前" if account.id == active_id else "")
            if account.id == active_id:
                status_item.setForeground(QColor("#34d399"))
            self.account_table.setItem(row, 3, status_item)
        self.account_table.resizeRowsToContents()
        if active_id:
            for row in range(self.account_table.rowCount()):
                item = self.account_table.item(row, 1)
                if item and item.data(Qt.ItemDataRole.UserRole) == active_id:
                    self.account_table.selectRow(row)
                    break
        elif self.accounts:
            self.account_table.selectRow(0)
        self._updating_table = False
        self._update_active_label()

    def _active_account(self) -> Account | None:
        if self.settings.active_account_id:
            for account in self.accounts:
                if account.id == self.settings.active_account_id:
                    return account
        if self.accounts:
            self.settings.active_account_id = self.accounts[0].id
            return self.accounts[0]
        return None

    def _selected_account(self) -> Account | None:
        rows = self.account_table.selectionModel().selectedRows()
        if rows:
            item = self.account_table.item(rows[0].row(), 1)
            if item:
                account_id = item.data(Qt.ItemDataRole.UserRole)
                for account in self.accounts:
                    if account.id == account_id:
                        return account
        return self._active_account()

    def _update_active_label(self) -> None:
        account = self._active_account()
        if account:
            self.active_label.setText(
                f"当前账号: {account.username}"
                + ("" if account.enabled else "（已停用）")
            )
            self.detail_label.setText(f"将使用账号 {account.username}")
        else:
            self.active_label.setText("当前账号: 未选择")
            self.detail_label.setText("请先添加一个账号。")

    def _on_account_selected(self) -> None:
        if self._updating_table or self._loading:
            return
        account = self._selected_account()
        if not account:
            return
        if self.settings.active_account_id != account.id:
            self.settings.active_account_id = account.id
            self.store.save_settings(self.settings)
        self._refresh_accounts()

    def _on_account_item_changed(self, item: QTableWidgetItem) -> None:
        if self._updating_table or self._loading or item.column() != 0:
            return
        user_item = self.account_table.item(item.row(), 1)
        if not user_item:
            return
        account_id = user_item.data(Qt.ItemDataRole.UserRole)
        for account in self.accounts:
            if account.id == account_id:
                account.enabled = item.checkState() == Qt.CheckState.Checked
                self.store.save_accounts(self.accounts)
                self._update_active_label()
                break

    def add_account(self) -> None:
        if self.worker and self.worker.isRunning():
            return
        dialog = AccountDialog(self)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            account = dialog.result_account()
            self.accounts.append(account)
            self.settings.active_account_id = account.id
            self.store.save_accounts(self.accounts)
            self.store.save_settings(self.settings)
            self._refresh_accounts()

    def edit_account(self) -> None:
        if self.worker and self.worker.isRunning():
            return
        account = self._selected_account()
        if not account:
            QMessageBox.information(self, "编辑账号", "请先选择一个账号")
            return
        dialog = AccountDialog(self, account)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            updated = dialog.result_account()
            for index, existing in enumerate(self.accounts):
                if existing.id == account.id:
                    self.accounts[index] = updated
                    break
            self.store.save_accounts(self.accounts)
            self._refresh_accounts()

    def delete_account(self) -> None:
        if self.worker and self.worker.isRunning():
            return
        account = self._selected_account()
        if not account:
            return
        answer = QMessageBox.question(
            self,
            "删除账号",
            f"确定删除账号 {account.username}？",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
        )
        if answer != QMessageBox.StandardButton.Yes:
            return
        self.accounts = [item for item in self.accounts if item.id != account.id]
        if self.settings.active_account_id == account.id:
            self.settings.active_account_id = (
                self.accounts[0].id if self.accounts else None
            )
        self.store.save_accounts(self.accounts)
        self.store.save_settings(self.settings)
        self._refresh_accounts()

    def test_account(self) -> None:
        if self.worker and self.worker.isRunning():
            QMessageBox.information(self, "测试登录", "认证任务正在运行，请先停止")
            return
        account = self._selected_account()
        if not account:
            QMessageBox.information(self, "测试登录", "请先选择一个账号")
            return
        self._save_settings_from_ui()
        settings = replace(self.settings, preempt=False)
        self._run_one_shot(
            lambda: self._authenticate_once(account, settings),
            "测试登录",
        )

    def _authenticate_once(self, account: Account, settings: AppSettings) -> str:
        interface = choose_interface(settings.interface or None)
        supp = Supplicant(
            interface=interface,
            identity=account.username.encode("gb18030"),
            password=account.password.encode("gb18030"),
            timeout=settings.timeout,
            retries=settings.retries,
            repeats=settings.repeats,
            private=True,
            logoff_first=True,
            locked_server=(
                settings.locked_server if settings.server_lock else None
            ),
        )
        ok = supp.authenticate()
        if supp.server_mac:
            self.servers.upsert(
                supp.server_mac, interface=interface, success=ok
            )
        return "认证成功" if ok else "认证失败（详见运行日志）"

    def toggle_auth(self) -> None:
        if self.worker and self.worker.isRunning():
            self.stop_auth()
        else:
            self.start_auth()

    def start_auth(self) -> None:
        if self.worker and self.worker.isRunning():
            return
        account = self._selected_account()
        if not account:
            QMessageBox.information(self, "开始认证", "请先添加或选择一个账号")
            self.show_window()
            return
        if not account.enabled:
            QMessageBox.warning(self, "开始认证", "当前账号已停用")
            return
        if not account.password:
            QMessageBox.warning(self, "开始认证", "当前账号没有密码")
            return
        self._save_settings_from_ui()
        self._last_account = account
        self._last_state = "starting"
        self.worker = AuthWorker(
            account,
            self.settings,
            discovery=self.discovery,
            servers=self.servers,
            parent=self,
        )
        self.worker.state.connect(self.on_worker_state)
        self.worker.details.connect(self.on_worker_details)
        self.worker.channel.connect(self.on_worker_channel)
        self.worker.finished.connect(self.on_worker_finished)
        self.worker.start()
        self._sync_takeover()
        self.set_running(True)
        self.state_label.setText("启动中...")
        self.state_label.setStyleSheet("color: #38bdf8;")
        self.detail_label.setText(
            "抢占模式已开启" if self.settings.preempt else "单次认证模式"
        )

    def stop_auth(self) -> None:
        if self.block_check.isChecked():
            self._set_block_check(False)
            self.takeover_state.set_enabled(False)
            try:
                self.blocker.stop()
            except Exception:  # noqa: BLE001
                LOG.exception("停止托管模式失败")
            self._update_block_status(False)
            self.preempt_check.setEnabled(True)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
        if not self.worker:
            return
        self._logoff_after_stop = (
            self.settings.logoff_on_stop and self.worker.ever_authenticated
        )
        self.worker.stop()
        self.state_label.setText("正在停止...")
        self.state_label.setStyleSheet("color: #fbbf24;")
        self.detail_label.setText("正在等待认证线程退出")

    def on_worker_state(self, code: str, text: str) -> None:
        self._last_state = code
        if code == "authenticated" and text == "认证成功":
            self._refresh_servers()
        colors = {
            "authenticated": "#34d399",
            "authenticating": "#38bdf8",
            "selecting": "#38bdf8",
            "offline": "#fbbf24",
            "retrying": "#fbbf24",
            "error": "#f87171",
            "failed": "#f87171",
            "stopped": "#94a3b8",
        }
        self.state_label.setText(text)
        self.state_label.setStyleSheet(f"color: {colors.get(code, '#e5e7eb')};")
        details = {
            "authenticated": "认证成功，保持在线",
            "authenticating": "正在认证",
            "selecting": "正在建立认证通道",
            "offline": "检测到掉线，正在抢占补入",
            "retrying": "正在等待下一次重试",
            "error": "认证异常，正在按策略处理",
            "failed": "认证失败；点击开始重试",
            "stopped": "认证任务已停止；点击开始重新认证",
        }
        if code in details:
            self.detail_label.setText(details[code])
        if self.tray:
            self.tray.setToolTip(f"{APP_NAME} - {text}")
            self.tray.setIcon(make_icon(colors.get(code, "#38bdf8")))
        if code == "authenticated" and not self._notified_online:
            self._notified_online = True
            self._notified_offline = False
            if self.tray:
                name = self._last_account.username if self._last_account else ""
                self.tray.showMessage(
                    APP_NAME,
                    f"{name} 认证成功",
                    QSystemTrayIcon.MessageIcon.Information,
                    2500,
                )
        elif code == "offline" and not self._notified_offline:
            self._notified_offline = True
            self._notified_online = False
            if self.tray:
                self.tray.showMessage(
                    APP_NAME,
                    "检测到掉线，正在抢占补入",
                    QSystemTrayIcon.MessageIcon.Warning,
                    2500,
                )

    def on_worker_details(self, text: str) -> None:
        self.interface_label.setText(f"网卡: {text}")

    def on_worker_channel(self, text: str) -> None:
        self.cache_label.setText(text)

    def on_worker_finished(self) -> None:
        worker = self.worker
        self.worker = None
        self.set_running(False)
        self.takeover_state.set_enabled(False)
        if self._shutting_down:
            return
        if worker and self._logoff_after_stop and self._last_account:
            account = self._last_account
            settings = replace(self.settings, preempt=False)
            self._run_one_shot(
                lambda: self._logoff_once(account, settings),
                "注销",
                quiet=True,
            )
        if self.block_check.isChecked():
            self._set_block_check(False)
            try:
                self.blocker.stop()
            except Exception:  # noqa: BLE001
                LOG.exception("停止托管模式失败")
            self._update_block_status(False)
            self.preempt_check.setEnabled(True)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
            LOG.info("认证任务结束，已关闭托管模式并恢复原客户端策略")
        self._logoff_after_stop = False

    def _logoff_once(self, account: Account, settings: AppSettings) -> str:
        interface = choose_interface(settings.interface or None)
        Supplicant(
            interface=interface,
            identity=account.username.encode("gb18030"),
            password=account.password.encode("gb18030"),
            timeout=settings.timeout,
            retries=settings.retries,
            repeats=settings.repeats,
            private=True,
            locked_server=(
                settings.locked_server if settings.server_lock else None
            ),
        ).logoff()
        return "已发送注销"

    def set_running(self, running: bool) -> None:
        self.toggle_button.setText("停止" if running else "开始认证")
        self.toggle_button.setObjectName("danger" if running else "primary")
        self.toggle_button.style().unpolish(self.toggle_button)
        self.toggle_button.style().polish(self.toggle_button)
        action = getattr(self, "tray_toggle_action", None)
        if action is not None:
            action.setText("停止认证" if running else "开始认证")
        self.settings_group.setEnabled(not running)
        self.account_table.setEnabled(not running)
        self.server_table.setEnabled(not running)
        self.server_lock_check.setEnabled(not running)
        for widget in (
            self.add_button,
            self.edit_button,
            self.delete_button,
            self.test_button,
            self.server_add_button,
            self.server_rename_button,
            self.server_delete_button,
            self.server_refresh_button,
        ):
            widget.setEnabled(not running)
        self.debug_panel.set_running(running)

    def _debug_interface(self) -> str:
        key = self.debug_panel.interface_key()
        if key:
            return key
        return choose_interface(self.settings.interface or None)

    def debug_probe(self) -> None:
        def action() -> str:
            key = self._debug_interface()
            info = next(
                (item for item in list_interfaces() if item.key == key), None
            )
            if info is None:
                raise RuntimeError("网卡不存在或已被移除")
            return (
                "找到认证服务器"
                if probe_authenticator(info)
                else "未找到认证服务器"
            )

        self._run_one_shot(action, "探测服务器")

    def _debug_supplicant(self) -> Supplicant:
        account = self._selected_account()
        if not account:
            raise RuntimeError("请先选择一个账号")
        if not account.password:
            raise RuntimeError("当前账号没有密码")
        self._save_settings_from_ui()
        source_mac = self.debug_panel.mac_edit.text().strip() or None
        return Supplicant(
            interface=self._debug_interface(),
            identity=account.username.encode("gb18030"),
            password=account.password.encode("gb18030"),
            timeout=self.settings.timeout,
            retries=self.settings.retries,
            repeats=self.settings.repeats,
            private=self.debug_panel.private_check.isChecked(),
            source_mac=source_mac,
            locked_server=(
                self.settings.locked_server if self.settings.server_lock else None
            ),
        )

    def debug_logoff(self) -> None:
        if self.worker and self.worker.isRunning():
            QMessageBox.information(self, "调试", "认证任务正在运行，请先停止")
            return
        self._run_one_shot(
            lambda: (self._debug_supplicant().logoff() or "已发送 Logoff"),
            "发送 Logoff",
        )

    def debug_start(self) -> None:
        if self.worker and self.worker.isRunning():
            QMessageBox.information(self, "调试", "认证任务正在运行，请先停止")
            return
        self._run_one_shot(
            lambda: (self._debug_supplicant().send_start() or "已发送 Start"),
            "发送 Start",
        )

    def debug_manual_auth(self) -> None:
        if self.worker and self.worker.isRunning():
            QMessageBox.information(self, "调试", "认证任务正在运行，请先停止")
            return
        self._run_one_shot(
            lambda: (
                "认证成功"
                if self._debug_supplicant().authenticate()
                else "认证失败（详见运行日志）"
            ),
            "手动认证",
        )

    def debug_tail_preview(self) -> None:
        account = self._selected_account()
        if not account or not account.password:
            QMessageBox.information(self, "私有尾", "请先选择一个带密码的账号")
            return
        try:
            interface = self._debug_interface()
            source_mac = self.debug_panel.mac_edit.text().strip() or None
            if source_mac is None:
                from scapy.all import get_if_hwaddr

                source_mac = get_if_hwaddr(interface)
            network = collect_network_info(interface)
            tail = build_tail(
                account.username.encode("gb18030"),
                account.password.encode("gb18030"),
                source_mac,
                network,
                bytes(16),
                TailOptions(),
            )
            hex_text = tail.hex()
            lines = [hex_text[i : i + 64] for i in range(0, len(hex_text), 64)]
            self.debug_panel.tail_output.setPlainText(
                f"长度 {len(tail)} 字节（Identity / 零 IV）\n" + "\n".join(lines)
            )
        except Exception as exc:  # noqa: BLE001
            self.debug_panel.tail_output.setPlainText(f"生成失败: {exc}")

    def debug_raw_send(self) -> None:
        text = self.debug_panel.raw_edit.toPlainText()
        data = re.sub(r"[^0-9A-Fa-f]", "", text)
        if len(data) < 28 or len(data) % 2:
            QMessageBox.warning(self, "原始帧", "请输入完整以太网帧十六进制")
            return
        payload = bytes.fromhex(data)

        def action() -> str:
            from scapy.all import Ether, sendp as scapy_sendp

            key = self._debug_interface()
            scapy_sendp(Ether(payload), iface=key, verbose=False)
            return f"已发送 {len(payload)} 字节"

        self._run_one_shot(action, "原始帧")

    def _run_one_shot(
        self,
        func: Callable[[], str],
        title: str,
        quiet: bool = False,
    ) -> None:
        if self.one_shot and self.one_shot.isRunning():
            QMessageBox.information(self, title, "已有调试任务在运行")
            return
        worker = OneShotWorker(func, self)
        self.one_shot = worker

        def done(result: str) -> None:
            self.one_shot = None
            self.debug_panel.append_output(f"[{title}] {result}")
            if not quiet:
                QMessageBox.information(self, title, result)

        def failed(error: str) -> None:
            self.one_shot = None
            self.debug_panel.append_output(f"[{title}] 失败: {error}")
            if not quiet:
                QMessageBox.warning(self, title, error)

        worker.done.connect(done)
        worker.failed.connect(failed)
        worker.start()

    def append_log(self, text: str) -> None:
        self.log_view.appendPlainText(text)

    def _on_tray_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        if reason in (
            QSystemTrayIcon.ActivationReason.DoubleClick,
            QSystemTrayIcon.ActivationReason.Trigger,
        ):
            self.show_window()

    def show_window(self) -> None:
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def _update_admin_badge(self) -> None:
        if is_admin():
            self.admin_badge.setText("管理员权限")
            self.admin_badge.setStyleSheet("color: #34d399;")
            self.admin_button.setVisible(False)
        else:
            self.admin_badge.setText("未以管理员运行：可能无法访问网卡")
            self.admin_badge.setStyleSheet("color: #fbbf24;")
            self.admin_button.setVisible(os.name == "nt")
        if os.name != "nt":
            self.block_check.setEnabled(False)
            self.block_check.setToolTip("托管模式仅支持 Windows")
        elif not is_admin():
            self.block_check.setEnabled(False)
            self.block_check.setToolTip("托管模式需要管理员权限")

    def _set_block_check(self, checked: bool) -> None:
        self.block_check.blockSignals(True)
        self.block_check.setChecked(checked)
        self.block_check.blockSignals(False)

    def _update_block_status(self, active: bool) -> None:
        if active:
            self.block_status.setText("已阻止 8021x.exe")
            self.block_status.setStyleSheet("color: #f87171;")
        else:
            self.block_status.setText("")
            self.block_status.setStyleSheet("")

    def _sync_takeover(self) -> None:
        active = bool(
            self.block_check.isChecked()
            and self.blocker.running
            and self.worker is not None
            and self.worker.isRunning()
            and self.settings.preempt
        )
        self.takeover_state.set_enabled(active)

    def _on_block_toggled(self, checked: bool) -> None:
        if self._loading:
            return
        if checked:
            account = self._selected_account()
            if not account or not account.password:
                self._set_block_check(False)
                QMessageBox.warning(self, "托管模式", "请先选择一个带密码的账号")
                return
            if not account.enabled:
                self._set_block_check(False)
                QMessageBox.warning(self, "托管模式", "当前账号已停用")
                return
            if os.name != "nt" or not is_admin():
                self._set_block_check(False)
                QMessageBox.warning(
                    self, "托管模式", "托管模式需要 Windows 管理员权限"
                )
                return
            answer = QMessageBox.warning(
                self,
                "托管模式",
                "将写入临时 IFEO 策略并持续结束 8021x.exe，同时自动开始认证任务。\n"
                "关闭开关、停止认证或退出本程序时会恢复原策略。\n"
                "如果程序被强制结束，下次启动或网络管家调用时会自动恢复。\n\n"
                "是否继续？",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.No,
            )
            if answer != QMessageBox.StandardButton.Yes:
                self._set_block_check(False)
                return
            self.preempt_check.setChecked(True)
            self.preempt_check.setEnabled(False)
            try:
                self.blocker.start()
            except Exception as exc:  # noqa: BLE001
                self._set_block_check(False)
                self.preempt_check.setEnabled(True)
                QMessageBox.critical(self, "托管模式", f"开启失败: {exc}")
                return
            self._update_block_status(True)
            self.settings.block_official_client = True
            self.store.save_settings(self.settings)
            LOG.warning("用户开启托管模式：持续阻止 8021x.exe")
            if not (self.worker and self.worker.isRunning()):
                self.start_auth()
            if not (self.worker and self.worker.isRunning()):
                self.takeover_state.set_enabled(False)
                try:
                    self.blocker.stop()
                except Exception:  # noqa: BLE001
                    LOG.exception("回滚托管模式失败")
                self._set_block_check(False)
                self._update_block_status(False)
                self.preempt_check.setEnabled(True)
                self.settings.block_official_client = False
                self.store.save_settings(self.settings)
                QMessageBox.warning(
                    self, "托管模式", "认证任务未能启动，已关闭托管模式"
                )
                return
            self._sync_takeover()
        else:
            self.takeover_state.set_enabled(False)
            try:
                self.blocker.stop()
            except Exception as exc:  # noqa: BLE001
                LOG.exception("关闭托管模式失败")
                QMessageBox.warning(self, "托管模式", f"关闭失败: {exc}")
            if self.worker and self.worker.isRunning():
                self.worker.stop()
            self._update_block_status(False)
            self.preempt_check.setEnabled(True)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
            LOG.info("用户关闭托管模式")

    def _start_blocker_if_enabled(self) -> None:
        if not self.settings.block_official_client:
            return
        account = self._selected_account()
        if not account or not account.password or not account.enabled:
            self._set_block_check(False)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
            QMessageBox.warning(
                self, "托管模式", "没有可用账号，托管模式已关闭"
            )
            return
        if os.name != "nt" or not is_admin():
            self._set_block_check(False)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
            QMessageBox.warning(
                self, "托管模式", "托管模式需要 Windows 管理员权限，已关闭"
            )
            return
        self._set_block_check(True)
        self.preempt_check.setChecked(True)
        self.preempt_check.setEnabled(False)
        try:
            self.blocker.start()
        except Exception as exc:  # noqa: BLE001
            self._set_block_check(False)
            self.preempt_check.setEnabled(True)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
            QMessageBox.critical(self, "托管模式", f"启动失败: {exc}")
            return
        self._update_block_status(True)
        LOG.warning("启动时自动开启托管模式：持续阻止 8021x.exe")
        if not (self.worker and self.worker.isRunning()):
            self.start_auth()
        if not (self.worker and self.worker.isRunning()):
            self.takeover_state.set_enabled(False)
            try:
                self.blocker.stop()
            except Exception:  # noqa: BLE001
                LOG.exception("回滚托管模式失败")
            self._set_block_check(False)
            self._update_block_status(False)
            self.preempt_check.setEnabled(True)
            self.settings.block_official_client = False
            self.store.save_settings(self.settings)
            QMessageBox.warning(
                self, "托管模式", "认证任务未能启动，托管模式已关闭"
            )
            return
        self._sync_takeover()

    def relaunch_as_admin(self) -> None:
        if os.name != "nt":
            QMessageBox.information(self, "管理员权限", "请使用 sudo 重新运行")
            return
        if getattr(sys, "frozen", False):
            executable = sys.executable
            params = subprocess.list2cmdline(sys.argv[1:])
        else:
            executable = sys.executable
            params = subprocess.list2cmdline(
                ["-m", "ruijie_auth.gui", *sys.argv[1:]]
            )
        result = ctypes.windll.shell32.ShellExecuteW(
            None, "runas", executable, params, str(Path.cwd()), 1
        )
        if result <= 32:
            QMessageBox.warning(self, "管理员权限", "用户取消了提权或启动失败")
            return
        self.quit_app()

    def closeEvent(self, event) -> None:  # noqa: N802 - Qt API
        if self.tray is not None and self.settings.minimize_to_tray:
            event.ignore()
            self.hide()
            self.tray.showMessage(
                APP_NAME,
                "已最小化到托盘，认证任务继续运行",
                QSystemTrayIcon.MessageIcon.Information,
                1800,
            )
            return
        self.shutdown()
        event.accept()

    def shutdown(self) -> None:
        self._shutting_down = True
        if not getattr(self.args, "smoke_test", False):
            try:
                self.takeover_state.stop()
            except Exception:  # noqa: BLE001
                LOG.exception("停止托管心跳失败")
        if self.worker:
            self.worker.stop()
            self.worker.wait(8000)
            self.worker = None
        if self.one_shot and self.one_shot.isRunning():
            self.one_shot.wait(4000)
        if not getattr(self.args, "smoke_test", False) and self.blocker:
            try:
                self.blocker.stop()
            except Exception:  # noqa: BLE001
                LOG.exception("停止托管模式失败")
        self._save_settings_from_ui()
        try:
            LOG.removeHandler(self.log_handler)
        except Exception:  # noqa: BLE001
            pass
        if self.tray:
            self.tray.hide()

    def quit_app(self) -> None:
        self.shutdown()
        QApplication.quit()


def parse_args(argv: Iterable[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="ruijie-auth-gui", description="HXXY 锐捷校园网认证 GUI"
    )
    parser.add_argument("--tray", action="store_true", help="启动后隐藏到托盘")
    parser.add_argument("--auto", action="store_true", help="启动后自动开始认证任务")
    parser.add_argument("--account", help="按用户名选择账号")
    parser.add_argument("--interface", help="覆盖网卡（名称/描述/NPF）")
    parser.add_argument("--verbose", action="store_true", help="详细协议日志")
    parser.add_argument("--version", action="store_true", help="显示版本后退出")
    parser.add_argument(
        "--restore-client",
        action="store_true",
        help="恢复被托管模式修改的 8021x.exe 运行策略后退出",
    )
    parser.add_argument("--smoke-test", action="store_true", help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def main(argv: Iterable[str] | None = None) -> int:
    args = parse_args(argv)
    if args.version:
        print(f"{APP_NAME} {APP_VERSION}")
        return 0

    if args.restore_client:
        store = AccountStore()
        setup_file_logging(store.logs_dir, args.verbose)
        state_path = program_data_dir() / "blocker_state.json"
        try:
            TakeoverState().stop()
            cleaned = ProcessBlocker.cleanup_stale(state_path)
        except OSError as exc:
            print(f"恢复失败（需要管理员权限）: {exc}", file=sys.stderr)
            return 1
        print("已恢复原客户端运行策略" if cleaned else "没有需要恢复的策略")
        return 0

    arguments = list(argv) if argv is not None else sys.argv[1:]
    app = QApplication([sys.argv[0], *arguments])
    app.setApplicationName(APP_NAME)
    app.setOrganizationName("HXXY")
    app.setApplicationVersion(APP_VERSION)
    app.setQuitOnLastWindowClosed(False)
    app.setStyle("Fusion")
    apply_light_palette(app)
    app.setFont(QFont(UI_FONT, 9))
    app.setStyleSheet(STYLE)

    if args.smoke_test:
        import tempfile

        store = AccountStore(
            base_dir=Path(tempfile.mkdtemp(prefix="ruijie-gui-smoke-"))
        )
        setup_file_logging(store.logs_dir, args.verbose)
        settings = AppSettings(
            start_minimized=True,
            auto_start=False,
            minimize_to_tray=False,
            block_official_client=False,
        )
        window = MainWindow(store, settings, args)
        app.aboutToQuit.connect(window.shutdown)
        QTimer.singleShot(500, app.quit)
        return app.exec()

    key = f"{APP_ID}-{getpass.getuser()}"
    probe = QLocalSocket()
    probe.connectToServer(key)
    if probe.waitForConnected(200):
        probe.write(b"show")
        probe.flush()
        probe.waitForBytesWritten(200)
        return 0

    store = AccountStore()
    setup_file_logging(store.logs_dir, args.verbose)
    state_path = program_data_dir() / "blocker_state.json"
    try:
        if ProcessBlocker.cleanup_stale(state_path):
            LOG.warning("检测到上次未清理的托管策略，已恢复原客户端")
    except OSError as exc:
        LOG.warning("清理旧托管策略失败（需要管理员权限）: %s", exc)
    try:
        TakeoverState().stop()
    except OSError as exc:
        LOG.warning("清理旧托管心跳失败: %s", exc)

    settings = store.load_settings()
    if args.verbose:
        settings.verbose = True
    if args.interface:
        settings.interface = args.interface

    window = MainWindow(store, settings, args)
    if args.account:
        for account in window.accounts:
            if account.username == args.account:
                window.settings.active_account_id = account.id
                store.save_settings(window.settings)
                window._refresh_accounts()
                break
    app.aboutToQuit.connect(window.shutdown)

    server = QLocalServer(app)
    QLocalServer.removeServer(key)
    server.listen(key)

    def accept_connection() -> None:
        while server.hasPendingConnections():
            connection = server.nextPendingConnection()
            try:
                data = bytes(connection.readAll())
                if b"show" in data:
                    window.show_window()
            finally:
                connection.disconnectFromServer()

    server.newConnection.connect(accept_connection)

    if not (args.tray or settings.start_minimized):
        window.show()
    if args.auto or settings.auto_start:
        QTimer.singleShot(300, window.start_auth)
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
