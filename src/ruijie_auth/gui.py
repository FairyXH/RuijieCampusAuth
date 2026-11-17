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
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Callable, Iterable
from urllib.parse import urlsplit

from PySide6.QtCore import QObject, Qt, QThread, QTimer, Signal
from PySide6.QtGui import QAction, QColor, QFont, QIcon, QPainter, QPen, QPixmap
from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import (
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
    QLabel,
    QLineEdit,
    QMainWindow,
    QMenu,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
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
from .private import TailOptions, build_tail, collect_network_info
from .storage import Account, AccountStore, AppSettings, program_data_dir

APP_NAME = "HXXY 校园网认证"
APP_ID = "RuijieCampusAuth"
APP_VERSION = __version__
UI_FONT = "Microsoft YaHei UI"

STYLE = """
QWidget {
    background: #0b1120;
    color: #e5e7eb;
    font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif;
    font-size: 13px;
}
QFrame#card, QGroupBox {
    background: #111827;
    border: 1px solid #1f2937;
    border-radius: 12px;
}
QGroupBox {
    margin-top: 14px;
    padding: 18px 12px 12px 12px;
    font-weight: 600;
}
QGroupBox::title {
    subcontrol-origin: margin;
    left: 14px;
    padding: 0 6px;
    color: #93c5fd;
}
QLabel#title {
    font-size: 24px;
    font-weight: 700;
    color: #f8fafc;
}
QLabel#state {
    font-size: 26px;
    font-weight: 700;
}
QLabel#muted { color: #94a3b8; }
QPushButton {
    background: #1f2937;
    border: 1px solid #334155;
    border-radius: 8px;
    padding: 8px 14px;
}
QPushButton:hover { background: #273449; border-color: #38bdf8; }
QPushButton:pressed { background: #0f172a; }
QPushButton:disabled { color: #64748b; border-color: #1e293b; }
QPushButton#primary {
    background: #2563eb;
    border: 1px solid #3b82f6;
    color: #ffffff;
    font-weight: 600;
}
QPushButton#primary:hover { background: #3b82f6; }
QPushButton#danger {
    background: #7f1d1d;
    border: 1px solid #b91c1c;
    color: #fecaca;
}
QPushButton#danger:hover { background: #991b1b; }
QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox {
    background: #0f172a;
    border: 1px solid #334155;
    border-radius: 8px;
    padding: 7px 10px;
    selection-background-color: #2563eb;
}
QComboBox::drop-down { border: none; width: 24px; }
QTableWidget {
    background: #0f172a;
    border: 1px solid #1f2937;
    border-radius: 10px;
    gridline-color: #1f2937;
}
QTableWidget::item { padding: 6px; }
QTableWidget::item:selected { background: #1d4ed8; }
QHeaderView::section {
    background: #111827;
    border: none;
    padding: 8px;
    color: #94a3b8;
    font-weight: 600;
}
QTabWidget::pane {
    border: 1px solid #1f2937;
    border-radius: 10px;
    background: #0f172a;
}
QTabBar::tab {
    background: transparent;
    padding: 8px 18px;
    color: #94a3b8;
}
QTabBar::tab:selected {
    color: #38bdf8;
    border-bottom: 2px solid #38bdf8;
}
QPlainTextEdit {
    background: #0f172a;
    border: 1px solid #1f2937;
    border-radius: 10px;
    font-family: "Cascadia Mono", "Consolas", monospace;
    font-size: 12px;
    selection-background-color: #1d4ed8;
}
QCheckBox { spacing: 8px; }
QStatusBar { color: #64748b; }
"""


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

    def __init__(self, account: Account, settings: AppSettings, parent=None):
        super().__init__(parent)
        self.account = replace(account)
        self.settings = replace(settings)
        self._stop = threading.Event()
        self._supp: Supplicant | None = None
        self._resolved: str | None = None
        self.ever_authenticated = False

    def stop(self) -> None:
        self._stop.set()
        supp = self._supp
        if supp is not None:
            supp.stop()

    def _wait_or_stop(self, seconds: float) -> bool:
        return self._stop.wait(seconds)

    def _logon(self) -> bool:
        requested = self.settings.interface or None
        if self._resolved is None:
            self.state.emit("selecting", "正在探测认证网卡...")
            self._resolved = choose_interface(requested, stop_event=self._stop)
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
        )
        self._supp = supp
        try:
            return supp.authenticate()
        finally:
            self._supp = None

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
        self.refresh_button = QPushButton("刷新网卡")
        interface_row.addWidget(QLabel("网卡"))
        interface_row.addWidget(self.interface_combo, 1)
        interface_row.addWidget(self.refresh_button)
        layout.addLayout(interface_row)

        option_row = QHBoxLayout()
        self.private_check = QCheckBox("附加锐捷私有尾")
        self.private_check.setChecked(True)
        self.mac_edit = QLineEdit()
        self.mac_edit.setPlaceholderText("源 MAC 覆盖（可选，如 00:e0:4c:68:00:c6）")
        option_row.addWidget(self.private_check)
        option_row.addWidget(self.mac_edit, 1)
        layout.addLayout(option_row)

        action_row = QHBoxLayout()
        self.probe_button = QPushButton("探测服务器")
        self.logoff_button = QPushButton("发 Logoff")
        self.start_button = QPushButton("发送 Start")
        self.manual_button = QPushButton("手动认证一次")
        for button in (
            self.probe_button,
            self.logoff_button,
            self.start_button,
            self.manual_button,
        ):
            action_row.addWidget(button)
        layout.addLayout(action_row)

        raw_group = QGroupBox("原始 EAPOL / 以太网帧")
        raw_layout = QVBoxLayout(raw_group)
        self.raw_edit = QPlainTextEdit()
        self.raw_edit.setPlaceholderText("粘贴完整以太网帧十六进制（可带空格/冒号）")
        self.raw_send_button = QPushButton("发送原始帧")
        raw_layout.addWidget(self.raw_edit)
        raw_layout.addWidget(self.raw_send_button)
        layout.addWidget(raw_group)

        tail_group = QGroupBox("私有尾预览")
        tail_layout = QVBoxLayout(tail_group)
        self.tail_button = QPushButton("生成当前账号的 Identity 私有尾")
        self.tail_output = QPlainTextEdit()
        self.tail_output.setReadOnly(True)
        self.tail_output.setMaximumBlockCount(2000)
        tail_layout.addWidget(self.tail_button)
        tail_layout.addWidget(self.tail_output, 1)
        layout.addWidget(tail_group, 1)

        self.output = QPlainTextEdit()
        self.output.setReadOnly(True)
        self.output.setMaximumBlockCount(2000)
        layout.addWidget(self.output, 1)

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
        self.resize(1160, 780)
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
        root = QVBoxLayout(central)
        root.setContentsMargins(18, 16, 18, 12)
        root.setSpacing(12)

        header = QHBoxLayout()
        title_box = QVBoxLayout()
        title = QLabel("HXXY 校园网认证")
        title.setObjectName("title")
        subtitle = QLabel("锐捷 EAP-MD5 · 569 字节私有尾 · 抢占保活")
        subtitle.setObjectName("muted")
        title_box.addWidget(title)
        title_box.addWidget(subtitle)
        header.addLayout(title_box)
        header.addStretch()
        self.admin_badge = QLabel()
        self.admin_badge.setObjectName("muted")
        self.admin_button = QPushButton("以管理员重启")
        self.admin_button.clicked.connect(self.relaunch_as_admin)
        header.addWidget(self.admin_badge)
        header.addWidget(self.admin_button)
        root.addLayout(header)

        body = QHBoxLayout()
        body.setSpacing(12)

        left_card = QFrame()
        left_card.setObjectName("card")
        left_card.setFixedWidth(400)
        left_layout = QVBoxLayout(left_card)
        left_layout.setContentsMargins(14, 14, 14, 14)
        left_layout.setSpacing(10)

        left_title = QLabel("账号管理")
        left_title.setStyleSheet("font-size: 16px; font-weight: 700;")
        left_layout.addWidget(left_title)
        self.account_table = QTableWidget(0, 4)
        self.account_table.setHorizontalHeaderLabels(["启用", "用户名", "备注", "状态"])
        self.account_table.verticalHeader().setVisible(False)
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
        header_view = self.account_table.horizontalHeader()
        header_view.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        header_view.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        header_view.setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        header_view.setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        left_layout.addWidget(self.account_table, 1)

        account_buttons = QGridLayout()
        self.add_button = QPushButton("添加")
        self.edit_button = QPushButton("编辑")
        self.delete_button = QPushButton("删除")
        self.test_button = QPushButton("测试登录")
        account_buttons.addWidget(self.add_button, 0, 0)
        account_buttons.addWidget(self.edit_button, 0, 1)
        account_buttons.addWidget(self.delete_button, 0, 2)
        account_buttons.addWidget(self.test_button, 1, 0, 1, 3)
        left_layout.addLayout(account_buttons)

        self.active_label = QLabel("当前账号: 未选择")
        self.active_label.setObjectName("muted")
        left_layout.addWidget(self.active_label)
        body.addWidget(left_card)

        right_card = QFrame()
        right_card.setObjectName("card")
        right_layout = QVBoxLayout(right_card)
        right_layout.setContentsMargins(16, 14, 16, 14)
        right_layout.setSpacing(10)

        status_row = QHBoxLayout()
        status_box = QVBoxLayout()
        self.state_label = QLabel("未启动")
        self.state_label.setObjectName("state")
        self.state_label.setStyleSheet("color: #94a3b8;")
        self.detail_label = QLabel("选择一个账号，然后开始认证。")
        self.detail_label.setObjectName("muted")
        self.detail_label.setWordWrap(True)
        self.interface_label = QLabel("网卡: 自动探测")
        self.interface_label.setObjectName("muted")
        status_box.addWidget(self.state_label)
        status_box.addWidget(self.detail_label)
        status_box.addWidget(self.interface_label)
        status_row.addLayout(status_box, 1)
        right_layout.addLayout(status_row)

        controls = QHBoxLayout()
        self.start_button = QPushButton("开始认证")
        self.start_button.setObjectName("primary")
        self.stop_button = QPushButton("停止")
        self.stop_button.setObjectName("danger")
        self.stop_button.setEnabled(False)
        controls.addWidget(self.start_button)
        controls.addWidget(self.stop_button)
        controls.addStretch()
        self.preempt_check = QCheckBox("抢占模式（掉线立即补入 + 无限重试）")
        controls.addWidget(self.preempt_check)
        right_layout.addLayout(controls)

        self.settings_group = QGroupBox("认证设置")
        settings_grid = QGridLayout(self.settings_group)
        settings_grid.setHorizontalSpacing(10)
        settings_grid.setVerticalSpacing(8)

        self.interface_combo = QComboBox()
        self.interface_refresh = QPushButton("刷新")
        settings_grid.addWidget(QLabel("网卡"), 0, 0)
        settings_grid.addWidget(self.interface_combo, 0, 1, 1, 4)
        settings_grid.addWidget(self.interface_refresh, 0, 5)

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
        settings_grid.addWidget(QLabel("掉线检测"), 1, 0)
        settings_grid.addWidget(self.check_spin, 1, 1)
        settings_grid.addWidget(QLabel("失败重试"), 1, 2)
        settings_grid.addWidget(self.retry_spin, 1, 3)
        settings_grid.addWidget(QLabel("认证超时"), 2, 0)
        settings_grid.addWidget(self.timeout_spin, 2, 1)
        settings_grid.addWidget(QLabel("Start 次数"), 2, 2)
        settings_grid.addWidget(self.retries_spin, 2, 3)
        settings_grid.addWidget(QLabel("应答次数"), 2, 4)
        settings_grid.addWidget(self.repeats_spin, 2, 5)

        self.url_edit = QLineEdit()
        self.url_edit.setPlaceholderText("http://www.baidu.com/")
        settings_grid.addWidget(QLabel("连通检测"), 3, 0)
        settings_grid.addWidget(self.url_edit, 3, 1, 1, 5)

        self.auto_start_check = QCheckBox("启动后自动开始认证")
        self.start_minimized_check = QCheckBox("启动时最小化到托盘")
        self.minimize_check = QCheckBox("关闭窗口最小化到托盘")
        self.verbose_check = QCheckBox("详细协议日志")
        self.logoff_on_stop_check = QCheckBox("停止时发送注销")
        checks = QHBoxLayout()
        checks.addWidget(self.auto_start_check)
        checks.addWidget(self.start_minimized_check)
        checks.addWidget(self.minimize_check)
        checks.addWidget(self.verbose_check)
        checks.addWidget(self.logoff_on_stop_check)
        checks.addStretch()
        right_layout.addWidget(self.settings_group)
        right_layout.addLayout(checks)

        block_row = QHBoxLayout()
        self.block_check = QCheckBox("托管模式：持续阻止原客户端 8021x.exe")
        self.block_check.setToolTip(
            "开启后每 0.5 秒结束 8021x.exe，并写入临时 IFEO 策略阻止其启动。\n"
            "关闭开关或退出本程序时自动恢复原策略。"
        )
        self.block_status = QLabel("")
        self.block_status.setObjectName("muted")
        block_row.addWidget(self.block_check)
        block_row.addWidget(self.block_status)
        block_row.addStretch()
        right_layout.addLayout(block_row)

        self.tabs = QTabWidget()
        self.log_view = QPlainTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setMaximumBlockCount(5000)
        self.debug_panel = DebugPanel(self)
        self.tabs.addTab(self.log_view, "运行日志")
        self.tabs.addTab(self.debug_panel, "调试工具")
        right_layout.addWidget(self.tabs, 1)

        body.addWidget(right_card, 1)
        root.addLayout(body, 1)
        self.setCentralWidget(central)

    def _setup_tray(self) -> None:
        if not QSystemTrayIcon.isSystemTrayAvailable():
            self.tray = None
            return
        self.tray = QSystemTrayIcon(make_icon(), self)
        menu = QMenu()
        show_action = QAction("显示主窗口", self)
        show_action.triggered.connect(self.show_window)
        start_action = QAction("开始认证", self)
        start_action.triggered.connect(self.start_auth)
        stop_action = QAction("停止认证", self)
        stop_action.triggered.connect(self.stop_auth)
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.quit_app)
        menu.addAction(show_action)
        menu.addAction(start_action)
        menu.addAction(stop_action)
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
        self.start_button.clicked.connect(self.start_auth)
        self.stop_button.clicked.connect(self.stop_auth)
        self.interface_refresh.clicked.connect(self.refresh_interfaces)
        for widget in (
            self.check_spin,
            self.retry_spin,
            self.timeout_spin,
            self.retries_spin,
            self.repeats_spin,
        ):
            widget.valueChanged.connect(self._save_settings_from_ui)
        self.interface_combo.currentIndexChanged.connect(self._save_settings_from_ui)
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
        self._select_interface(self.settings.interface)

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
        self.settings.interface = str(self.interface_combo.currentData() or "")
        self.log_handler.setLevel(
            logging.DEBUG if self.settings.verbose else logging.INFO
        )
        self.store.save_settings(self.settings)

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
        )
        return "认证成功" if supp.authenticate() else "认证失败（详见运行日志）"

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
        self.worker = AuthWorker(account, self.settings, self)
        self.worker.state.connect(self.on_worker_state)
        self.worker.details.connect(self.on_worker_details)
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
        ).logoff()
        return "已发送注销"

    def set_running(self, running: bool) -> None:
        self.start_button.setEnabled(not running)
        self.stop_button.setEnabled(running)
        self.settings_group.setEnabled(not running)
        self.account_table.setEnabled(not running)
        for widget in (
            self.add_button,
            self.edit_button,
            self.delete_button,
            self.test_button,
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
