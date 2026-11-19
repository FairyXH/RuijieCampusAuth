"""宝塔式数字菜单 CLI（Python 版入口；GUI 已移除）。

Run with no arguments to enter the interactive menu; keep argparse
subcommands available for scripts/automation.
"""

from __future__ import annotations

import getpass
import sys
from pathlib import Path

from .storage import Account, AppSettings, AccountStore
from .system_service import (
    load_service_config,
    read_service_status,
    write_service_config,
)

MENU = """
========================================
  锐捷校园网认证器（Python 版）
  学校档案: {profile}
  当前状态: {status}
========================================
 1) 认证管理
 2) 账号池
 3) 认证服务器
 4) 网络与 DHCP
 5) 诊断工具
 6) 调试功能
 7) 系统服务
 8) 学校配置档案
 9) 查看日志
 0) 退出
"""


def _prompt(text: str, default: str = "") -> str:
    suffix = f" [{default}]" if default else ""
    value = input(f"{text}{suffix}: ").strip()
    return value or default


def _pause() -> None:
    input("\n回车返回...")


class MenuApp:
    def __init__(self, base_dir: Path | None = None) -> None:
        self.store = AccountStore(base_dir)
        self.settings = self.store.load_settings()

    # ------------------------------------------------------------- helpers

    def _active_account(self) -> Account | None:
        accounts = self.store.load_accounts()
        active_id = self.settings.active_account_id
        return next(
            (a for a in accounts if a.id == active_id),
            next((a for a in accounts if a.enabled), None),
        )

    def _header(self) -> dict:
        status = read_service_status()
        return {
            "profile": self.settings.profile_name or "未导入",
            "status": f"{status['state']}: {status['message']}" if status else "未检测",
        }

    def _select_interface(self) -> str:
        from .cli import choose_interface, list_interfaces

        interfaces = list_interfaces()
        print("\n可用网卡:")
        for index, item in enumerate(interfaces, 1):
            print(f"  {index}) {item.description} [{item.key}]")
        choice = _prompt("选择网卡编号（回车保持当前）",
                         self.settings.interface or "")
        if choice.isdigit() and 1 <= int(choice) <= len(interfaces):
            selected = interfaces[int(choice) - 1].key
            self.settings.interface = selected
            self.store.save_settings(self.settings)
            return selected
        return self.settings.interface

    def _supplicant(self, interface: str):
        from .cli import Supplicant

        account = self._active_account()
        if account is None:
            print("请先在「账号池」添加账号")
            return None
        return Supplicant(
            interface=interface,
            identity=account.username.encode("gb18030"),
            password=account.password.encode("gb18030"),
            timeout=self.settings.timeout,
            retries=self.settings.retries,
            repeats=self.settings.repeats,
            start_timeout=self.settings.start_timeout,
            locked_server=(
                self.settings.locked_server if self.settings.server_lock else None
            ),
            service_name=self.settings.service_name,
            static_id=self.settings.static_id,
            auth_mode=self.settings.auth_mode,
            log=print,
        )

    # ------------------------------------------------------------ 1 认证

    def menu_auth(self) -> None:
        interface = self._select_interface() or self._select_interface()
        while True:
            print("\n--- 认证管理 ---")
            print(" 1) 开始认证")
            print(" 2) 注销会话")
            print(" 3) 测试连通")
            print(" 0) 返回")
            choice = input("选择: ").strip()
            if choice == "1":
                supp = self._supplicant(interface)
                if supp is None:
                    continue
                try:
                    ok = supp.authenticate()
                    print("认证成功" if ok else "认证失败（校园网不可达属正常）")
                except KeyboardInterrupt:
                    supp.stop()
                except Exception as exc:
                    print(f"认证异常: {exc}")
                _pause()
            elif choice == "2":
                from .cli import send_logoff

                send_logoff(interface)
                _pause()
            elif choice == "3":
                from .diagnostics import probe_network

                result = probe_network(interface, self.settings.connectivity_url)
                print(f"网关 {result.gateway} 可达={result.gateway_reachable} "
                      f"DNS={result.dns_ok} HTTP={result.http_status} "
                      f"耗时={result.elapsed_ms}ms")
                _pause()
            elif choice == "0":
                return

    # ------------------------------------------------------------ 2 账号池

    def menu_accounts(self) -> None:
        while True:
            accounts = self.store.load_accounts()
            print("\n--- 账号池 ---")
            for index, account in enumerate(accounts, 1):
                mark = "●" if account.enabled else "○"
                active = " [当前]" if account.id == self.settings.active_account_id else ""
                print(f" {index}) {mark} {account.username} {account.note}{active}")
            print(" 1) 添加  2) 更新选中  3) 删除选中  4) 启用/停用  5) 设为当前  0) 返回")
            choice = input("选择: ").strip()
            if choice == "0":
                return
            if choice == "1":
                username = _prompt("用户名")
                password = getpass.getpass("密码: ")
                note = _prompt("备注")
                if not username:
                    print("用户名不能为空")
                    continue
                if any(a.username == username for a in accounts):
                    print(f"账号已存在: {username}")
                    continue
                accounts.append(Account(username=username, password=password, note=note))
                self.store.save_accounts(accounts)
                self.settings.active_account_id = accounts[-1].id
                self.store.save_settings(self.settings)
                print("账号已添加")
                continue
            index = input("账号编号: ").strip()
            if not index.isdigit() or not (1 <= int(index) <= len(accounts)):
                print("编号无效")
                continue
            account = accounts[int(index) - 1]
            if choice == "2":
                account.username = _prompt("用户名", account.username)
                account.password = getpass.getpass(
                    f"密码 [{'保持' if account.password else '无'}]: ") or account.password
                account.note = _prompt("备注", account.note)
                self.store.save_accounts(accounts)
                print("账号已更新")
            elif choice == "3":
                accounts.remove(account)
                self.store.save_accounts(accounts)
                print("账号已删除")
            elif choice == "4":
                account.enabled = not account.enabled
                self.store.save_accounts(accounts)
                print("已启用" if account.enabled else "已停用")
            elif choice == "5":
                self.settings.active_account_id = account.id
                self.store.save_settings(self.settings)
                print(f"当前账号: {account.username}")

    # ------------------------------------------------------------ 3 服务器

    def menu_servers(self) -> None:
        from .storage import ServerStore

        server_store = ServerStore(self.store.base_dir)
        while True:
            servers = server_store.load()
            print("\n--- 认证服务器 ---")
            for index, record in enumerate(servers, 1):
                print(f" {index}) {record.label}")
            print(" 1) 锁定选中  2) 取消锁定  3) 清空全部  0) 返回")
            choice = input("选择: ").strip()
            if choice == "0":
                return
            if choice == "2":
                self.settings.server_lock = False
                self.store.save_settings(self.settings)
                print("已取消锁定")
                continue
            index = input("服务器编号: ").strip()
            if not index.isdigit() or not (1 <= int(index) <= len(servers)):
                print("编号无效")
                continue
            server = servers[int(index) - 1]
            if choice == "1":
                self.settings.server_lock = True
                self.settings.locked_server = server.mac
                self.store.save_settings(self.settings)
                print(f"已锁定 {server.mac}")
            elif choice == "3":
                server_store.save([])
                print("已清空")

    # ------------------------------------------------------- 4 网络与 DHCP

    def menu_network(self) -> None:
        from .protocols.dhcp_control import DhcpOperation, run as dhcp_run
        from .protocols.auth_method import wants_dhcp_after, wants_dhcp_before

        while True:
            print("\n--- 网络与 DHCP ---")
            print(f" dhcp_mode={self.settings.dhcp_mode} "
                  f"timeout={self.settings.dhcp_timeout} "
                  f"option82={self.settings.dhcp_option82}")
            print(" 1) 续租  2) 释放  3) 释放并续租  4) 查看租约  5) 设置模式  0) 返回")
            choice = input("选择: ").strip()
            if choice == "0":
                return
            interface = self._select_interface() if not self.settings.interface \
                else self.settings.interface
            operations = {
                "1": DhcpOperation.RENEW,
                "2": DhcpOperation.RELEASE,
                "3": DhcpOperation.RELEASE_RENEW,
                "4": DhcpOperation.STATUS,
            }
            if choice in operations:
                try:
                    result = dhcp_run(
                        interface or self.settings.interface,
                        operations[choice],
                        self.settings.dhcp_timeout,
                        self.settings.dhcp_option82)
                    print(f"ok={result.ok} {result.message} "
                          f"租约={result.lease.ip if result.lease else '-'}")
                except Exception as exc:
                    print(f"DHCP 失败: {exc}")
                _pause()
            elif choice == "5":
                print(" 1) before（认证前续租） 2) after（认证后续租） 3) none（不使用）")
                mode_choice = input("选择: ").strip()
                mapping = {"1": "before", "2": "after", "3": "none"}
                if mode_choice in mapping:
                    self.settings.dhcp_mode = mapping[mode_choice]
                    self.store.save_settings(self.settings)
                    print(f"dhcp_mode={self.settings.dhcp_mode}")

    # ------------------------------------------------------------- 5 诊断

    def menu_diagnostics(self) -> None:
        from .diagnostics import (
            detect_dialup,
            doctor_available,
            list_nics,
            monitor_arp,
            probe_network,
            probe_proxy,
            vendor_of,
        )

        while True:
            print("\n--- 诊断工具 ---")
            print(" 1) 网络探测（网关/DNS/HTTP）")
            print(" 2) ARP 网关监控")
            print(" 3) 代理检测")
            print(" 4) 拨号检测")
            print(" 5) OUI 识别")
            print(" 6) 小锐医生管道")
            print(" 0) 返回")
            choice = input("选择: ").strip()
            interface = self.settings.interface
            try:
                if choice == "1":
                    result = probe_network(interface, self.settings.connectivity_url)
                    print(f"{result.detail} | {result.elapsed_ms}ms")
                elif choice == "2":
                    duration = float(_prompt("监控时长(秒)", "10"))
                    result = monitor_arp(interface, duration, 1.0)
                    print(f"网关 {result.gateway}: 初始 {result.initial_mac} "
                          f"→ 最终 {result.final_mac}，变更 {len(result.changes)} 次")
                elif choice == "3":
                    nic = next((n for n in list_nics() if n.name == interface), None)
                    from .diagnostics import gateway_of
                    gateway = gateway_of(nic) if nic else ""
                    result = probe_proxy(gateway, [1080, 3128, 8000, 8080, 8888], 500)
                    print(f"开放端口: {result.open_ports} 环境代理: {result.environment_proxy}")
                elif choice == "4":
                    reasons = detect_dialup()
                    print("拨号冲突: " + ("; ".join(reasons) if reasons else "无"))
                elif choice == "5":
                    from .cli import list_interfaces
                    for item in list_interfaces():
                        mac = getattr(item, "mac", "") or ""
                        print(f"  {item.description}: {mac} → {vendor_of(mac)}")
                elif choice == "6":
                    print(f"医生管道可用: {doctor_available()}")
                else:
                    continue
            except Exception as exc:
                print(f"诊断失败: {exc}")
            _pause()

    # ------------------------------------------------------------- 6 调试

    def menu_debug(self) -> None:
        from .debug.defaults import build as build_defaults
        from .protocols.clients import (
            ClientCenterOptions,
            PortalRequestOptions,
            build_center_url,
            client_center_query,
            des_transform,
            get_online_user_info,
            login as portal_login,
            logout as portal_logout,
            page_info as portal_page_info,
            sam_probe,
            self_service_get,
            upgrade_check,
        )

        account = self._active_account()
        while True:
            print("\n--- 调试功能（预填当前学校配置） ---")
            print(" 1) Portal 登录       2) Portal 注销")
            print(" 3) Portal 在线信息    4) Portal 页面信息")
            print(" 5) 自助服务          6) 客户端中心")
            print(" 7) 升级检查          8) SAM 探测")
            print(" 9) 私有属性解析(hex)  0) 返回")
            choice = input("选择: ").strip()
            if choice == "0":
                return
            try:
                if choice in ("1", "2", "3", "4"):
                    options = PortalRequestOptions(
                        portal_url=self.settings.portal_url,
                        user_id=account.username if account else "",
                        password=account.password if account else "",
                        service=self.settings.service_name,
                        user_ip=self._local_ip(),
                    )
                    actions = {"1": portal_login, "2": portal_logout,
                               "3": get_online_user_info, "4": portal_page_info}
                    result = actions[choice](options)
                    print(f"ok={result.ok} status={result.status}")
                    print((result.body or "")[:600])
                elif choice == "5":
                    args = json_loads_or(build_defaults("self-service",
                                                        self.settings, account))
                    result = self_service_get(args.get("url", ""))
                    print(f"ok={result.ok} status={result.status}")
                    print((result.body or "")[:600])
                elif choice == "6":
                    options = ClientCenterOptions(
                        url=self.settings.portal_url or "",
                        mac="", ipv4=self._local_ip(),
                        product="33554432", main_version="100663296",
                        sub_version="1442840576",
                        school_code=self.settings.school_code,
                        os_bits="64")
                    result = client_center_query(options)
                    print(f"ok={result.ok} status={result.status}")
                    print((result.body or "")[:600])
                elif choice == "7":
                    args = json_loads_or(build_defaults("upgrade-check",
                                                        self.settings, account))
                    result = upgrade_check(args.get("url", ""))
                    print(f"ok={result.ok} status={result.status}")
                    print((result.body or "")[:600])
                elif choice == "8":
                    host = _prompt("SAM 主机", self.settings.sam_host)
                    port = int(_prompt("SAM 端口",
                                       str(self.settings.sam_port or 53)))
                    payload = bytes.fromhex(_prompt("载荷(hex)", "0d0e0d0e"))
                    result = sam_probe(host, port, payload, 2000)
                    print(f"ok={result.ok} 响应={result.response_hex} "
                          f"耗时={result.elapsed_ms}ms {result.error or ''}")
                elif choice == "9":
                    payload = bytes.fromhex(_prompt("私有属性 hex"))
                    from .protocols import parse_private_properties
                    parsed = parse_private_properties(payload)
                    for prop in parsed.properties:
                        print(f"  {prop.name}: {prop.display_value}")
                    for message in parsed.messages:
                        print(f"  消息: {message}")
                    for error in parsed.errors:
                        print(f"  告警: {error}")
            except Exception as exc:
                print(f"失败: {exc}")
            _pause()

    def _local_ip(self) -> str:
        try:
            import socket
            socket.setdefaulttimeout(1)
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.connect(("8.8.8.8", 80))
            address = sock.getsockname()[0]
            sock.close()
            return address
        except Exception:
            return ""

    # ------------------------------------------------------------- 7 服务

    def menu_service(self) -> None:
        while True:
            status = read_service_status()
            print("\n--- 系统服务 ---")
            if status:
                print(f" 状态: {status['state']}: {status['message']}")
            else:
                print(" 状态: 未检测")
            print(" 1) 安装并启动  2) 启动  3) 停止  4) 卸载  5) 应用服务配置  0) 返回")
            choice = input("选择: ").strip()
            from .system_service import (
                install_service,
                service_host_path,
                start_service,
                stop_service,
                uninstall_service,
            )
            try:
                if choice == "1":
                    account = self._active_account()
                    if account is None:
                        print("请先添加账号")
                        continue
                    write_service_config(account, self.settings)
                    install_service(str(service_host_path()))
                    start_service()
                    print("服务已安装并启动")
                elif choice == "2":
                    start_service()
                    print("服务已启动")
                elif choice == "3":
                    stop_service()
                    print("服务已停止")
                elif choice == "4":
                    uninstall_service()
                    print("服务已卸载")
                elif choice == "5":
                    account = self._active_account()
                    if account is None:
                        print("请先添加账号")
                        continue
                    write_service_config(account, self.settings)
                    print("服务配置已更新（运行中的服务下一轮生效）")
            except Exception as exc:
                print(f"服务操作失败: {exc}")
            if choice == "0":
                return

    # ------------------------------------------------------------- 8 档案

    def menu_profiles(self) -> None:
        from .school_profile import discover_profiles, import_profile, load_profile

        while True:
            profiles = discover_profiles()
            print("\n--- 学校配置档案 ---")
            for index, path in enumerate(profiles, 1):
                try:
                    profile = load_profile(path)
                    print(f" {index}) {profile.name} ({profile.auth_mode})")
                except Exception:
                    print(f" {index}) {path.name} (无法解析)")
            print(f" 当前档案: {self.settings.profile_name or '未导入'}")
            print(" 1) 导入/切换  2) 刷新  0) 返回")
            choice = input("选择: ").strip()
            if choice == "0":
                return
            if choice == "2":
                continue
            index = input("档案编号: ").strip()
            if not index.isdigit() or not (1 <= int(index) <= len(profiles)):
                print("编号无效")
                continue
            try:
                profile = load_profile(profiles[int(index) - 1])
                result = import_profile(profile, self.store, str(profiles[int(index) - 1]))
                self.settings = self.store.load_settings()
                print(f"已切换到 {result.profile_name}；应用字段: "
                      f"{', '.join(result.applied)}；备份: {result.backup_path or '无'}")
            except Exception as exc:
                print(f"导入失败: {exc}")

    # ------------------------------------------------------------- 9 日志

    def menu_logs(self) -> None:
        from .system_service import service_log_path

        log_path = service_log_path()
        if not Path(log_path).exists():
            print("服务日志尚未生成")
            return
        lines = Path(log_path).read_text(encoding="utf-8", errors="replace").splitlines()
        for line in lines[-60:]:
            print(line)
        _pause()

    # ------------------------------------------------------------- run

    def run(self) -> int:
        while True:
            header = self._header()
            print(MENU.format(**header))
            choice = input("选择: ").strip()
            actions = {
                "1": self.menu_auth,
                "2": self.menu_accounts,
                "3": self.menu_servers,
                "4": self.menu_network,
                "5": self.menu_diagnostics,
                "6": self.menu_debug,
                "7": self.menu_service,
                "8": self.menu_profiles,
                "9": self.menu_logs,
            }
            if choice == "0":
                return 0
            action = actions.get(choice)
            if action:
                try:
                    action()
                except KeyboardInterrupt:
                    print("\n已取消")
            else:
                print("无效选择")


def json_loads_or(text: str | None) -> dict:
    import json
    if not text or not text.strip():
        return {}
    try:
        parsed = json.loads(text)
        return parsed if isinstance(parsed, dict) else {}
    except json.JSONDecodeError:
        return {}


def run_menu(base_dir: Path | None = None) -> int:
    return MenuApp(base_dir).run()
