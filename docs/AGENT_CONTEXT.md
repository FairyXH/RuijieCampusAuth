# Agent Context

## Project

`ruijie-campus-auth-hxxy` is a cross-platform 802.1X supplicant for the Ruijie
campus network at Xiamen Huaxia University (HXXY). It authenticates with the
normal campus username/password using EAP-MD5 and reproduces the Ruijie vendor
private tail used by the official Windows client. It is a compatibility client,
not an authentication bypass.

Two implementations are maintained:

- **Root: C# / .NET 10** — `RuijieCampusAuth.Core`, `.Cli`, `.Service`,
  `.App` (Avalonia), `.Core.Tests`. Framework-dependent publish; Windows
  service + Linux systemd + Avalonia GUI.
- **`python/`** — the complete Python implementation, PySide6 GUI, single-file
  build, tests, and reference protocol code.

Older sections below that mention `src/ruijie_auth/...` and `tests/...` refer
to the Python tree under `python/`.

## Verified State

- 2026-11-17: real-account validation on Windows/Npcap.
- Python client received EAP-Success with the server message
  `欢迎使用厦门华厦学院网络`.
- CLI (`python -m ruijie_auth`), `ruijie_hxxy.login()`, and `RuijieClient`
  were all validated against the live network.
- Original client and Python client can authenticate on the same account;
  the server does not require the official process.
- 2026-11-17 23:49: deployed single-file exe (`--tray --auto`) authenticated
  with the takeover blocker enabled; 8021x.exe was terminated and both IFEO
  views received the marker.
- 23:50: deployed `restart_ruijie.vbs` logged `healthy takeover; skip original`
  and did not launch the official client.
- 23:50/23:54: after killing the GUI and after injecting a legacy markerless
  IFEO policy, the VBS logged `restoring stale policy` and
  `launching original supplicant`; the policy was removed and 8021x restarted.
- 2026-11-18 00:03: server returned `不在认证时段内!` (outside the campus
  authentication window). This is a server policy, not an account/client
  failure. The GUI kept retrying as configured.
- 0.5.0: GUI re-laid out to a ~1/4-screen iOS-style window with left tabs and a
  right log pane; discovery cache added and unit-tested. 12 tests pass.
- 0.5.1: fixed 880x620 non-resizable window, fixed 440px left tab column,
  single start/stop toggle, both-axis account-table scrolling, white tray menu,
  and the auth-channel/force-rediscover block moved to the top of Connection.
  All switches and connection parameters persist to settings.json.
- 2026-11-18 01:45: with the USB NIC pinned via Windows `IP_UNICAST_IF`
  (ifIndex 16), TLS to www.baidu.com succeeds on 172.18.28.6 while EAPOL-Start
  gets no server response. The switch appears to keep the earlier authorized
  session; "link egress" and "fresh 802.1X auth" must be reported separately.
  Pin the interface for probes: binding the source address alone still lets the
  WLAN default route win.
- 0.6.0: server list learned from live EAP responses and a lock-server switch.
  0.6.0 stored it at `%APPDATA%\RuijieCampusAuth\servers.json`; current C# and
  frozen Python builds use `<install_dir>\servers.json` with MAC/name/interface,
  first/last-seen, last-success, and success count.
- 2026-11-18 08:49: locked unicast authentication validated live on HXXY:
  group Logoff → unicast EAPOL-Start to `00:1a:a9:17:ff:ff` → EAP-MD5 →
  EAP-Success. A **unicast Logoff** to the authenticator does not clear the
  switch session and makes the next Start fail with EAP-Failure; Logoff must
  stay on the standard PAE group `01:80:c2:00:00:03`.
- 0.7.0: Network Guardian linkage. The app queries the loopback MCP
  `http://127.0.0.1:18765/mcp` (stateless HTTP, protocol `2025-11-25`) with
  `guardian_status`, matches the auth NIC by NPF GUID to `adapterGuid`, and
  treats `isOnline=false` or `captivePortalSuspected=true` as offline after N
  confirmations. MCP errors, paused Guardian state, and non-authoritative
  probes fall back to the local TCP check.
- 2026-11-18 09:17: live query returned `NG-TMP-0000 | 172.18.28.6 |
  online/stable | source=认证网卡` for the USB NIC.
- 0.8.0: aligned auth config with the official client: service name (0x39),
  static ID (0x54), Start retry interval, DHCP mode/timeout/Option 82, school
  code. Service name and static ID are applied to Identity/MD5/Start/Logoff
  private tails.
- 2026-11-18 18:19: C# release deployed to
  `D:\Storageredirect\Program\RuijieCampusAuth`. `HXXY-CampusAuth` runs as
  LocalSystem/auto and authenticated live (`00:1a:a9:17:ff:ff`); takeover
  blocked 7 official process names and `RJSuService`; GUI `--tray --auto`
  loaded the account pool and learned-server list; `baidu HTTP 200`. Service
  logs include `已成功阻止原版进程运行: <exe> (PID <pid>)` and
  `已成功阻止原版服务运行: <service>`.
- 2026-11-18 18:35: unified all runtime config under
  `D:\Storageredirect\Program\RuijieCampusAuth`. GUI `--tray --auto` loaded
  `accounts.json`/`settings.json` from the install dir, read the service's
  `discovery.json`/`servers.json` directly, and wrote `logs\csharp-gui.log`
  there; the old `%APPDATA%` log stopped updating. CLI `config path/show` reads
  the same install-dir `service.json`. Service stayed authenticated and
  `baidu HTTP 200`.
- 2026-11-18 18:43: Windows service account-pool alignment verified. After the
  GUI upgraded `accounts.json` to `machine-dpapi:`, a service restart logged
  `账号池加载: 1 个可用账号`, tried `S12016004`, and authenticated live
  (`00:1a:a9:17:ff:ff`). Service now reads `settings.json` for runtime settings
  instead of only `service.json`.
- 2026-11-18 18:51: launcher hardened for frozen C:. VBS now has a direct IFEO
  cleanup fallback across all seven official exe names when
  `--restore-client` is unavailable. The final frozen-baseline VBS SHA256 is
  `7E8258FA0CDAED2D0BF648D30F64745F0AFFE5E299DE92BBFB40C90FDBF3E7C6`;
  direct `cscript` and scheduled-task runs both returned 0 and logged
  `healthy takeover; skip original`.
- 2026-11-18 18:57: home-LAN pause verified with a no-authenticator
  `\Device\NPF_Loopback` interface. The service caught the interface exception,
  saw Guardian online, wrote `state=paused` and
  `未检测到认证服务器，当前网络在线，暂停认证`, then rechecked every 30 s.
  Restoring the USB NPF interface and restarting re-authenticated successfully
  (`00:1a:a9:17:ff:ff`); `baidu HTTP 200`. AuthWorker and GUI both wrap each
  auth attempt so a missing/unplugged NIC cannot terminate the loop.
- 2026-11-18 19:22: freeze-readiness audit. D: is fixed disk 1 (WD 1TB, NTFS,
  basic data), not the system volume; .NET 10.0.9/10.0.10 and Npcap
  `wpcap.dll` are installed on C:; `HXXY-CampusAuth` has `depend= npcap`,
  auto-start, LocalSystem, and SCM recovery `restart/5s,10s,30s` (verified by
  killing the service process; SCM restarted it and auth recovered). VBS hash
  `7E8258FA0CDAED2D0BF648D30F64745F0AFFE5E299DE92BBFB40C90FDBF3E7C6`;
  task/VBS runs return 0. IFEO baseline test: with `takeover_state.json`
  removed and markers pre-seeded, the service saved `Debugger64/32 = null`,
  re-applied markers, and cleared them on takeover stop. Service logs rotate at
  10 MB, GUI at 5 MB, VBS launcher at 1 MB.
- NetworkGuardian is `C:\Windows\Protect\NetworkGuardian.exe` with config under
  `%LOCALAPPDATA%\NetworkGuardian` (on D:); its `campusAuth` action is
  `schtasks /run /i /tn RunHXXYNet`. DiskGuard (`C:\Program Files (x86)\Laneye\
  DiskGuard`) is the freeze product; D: exclusion must be confirmed in its UI.
- 2026-11-18 21:04: post-freeze reboot validation passed. Boot
  `20:54:42`; service PID 8580, GUI PID 21820, `HXXY-CampusAuth` Running,
  `npcap` Running, `RJSuService` Stopped/Disabled. Frozen C: baseline retained
  VBS hash `7E8258...E7C6`, `RunHXXYNet` action/principal/rules, service
  `depend=npcap` and recovery 5s/10s/30s, and all seven IFEO markers. D:
  `accounts.json`/`settings.json`/`service.json`/`takeover_state.json`
  persisted; service started `20:55:07`, authenticated `20:57:09`
  (`00:1a:a9:17:ff:ff`), GUI loaded account/server, `launcher.log` logged
  `healthy takeover; skip original`, `baidu HTTP 200`. The detailed runbook is
  `docs/FROZEN_DEPLOYMENT.md`.

## Official Client Configuration Surface

Source: `C:\Program Files\Ruijie Networks\Ruijie Supplicant`.

- `SuConfig.txt` (plaintext summary): username, client title, Authentication
  Timeout=3, Start Timeout=3, start attempts=3, DHCP mode=before-auth, DHCP
  response timeout=30, self-service URL, AD timeout=120, adapter name.
- `newskin.zip` login layout: username, password, service combo, `CTpasswordEdit`,
  remember password, auto connect, wired/wireless, NIC name.
- Settings layout (`layout_softwareconfig.xml`): basic/nic/notice tabs; auto run,
  auto connect, auto XiaoRui WiFi, NAT/ICS sharing, wired/wireless/auto login,
  close-to-tray/quit, language, notification switches.
- Network info layout: IP acquisition mode, IP, mask, gateway, DNS, spare DNS,
  NIC MAC, OS MAC, HDD serial (display-only).
- Config keys observed: `AuthMode=EAPMD5`, `MediaType=Wired`,
  `Service=电信宽带服务`, `Adapter`, `StartNumber`, `StartTimeout`,
  `EnableBeforeAuth`, `IsSupportOption82`, `SchoolCode`, `CTPassword`,
  `SelfSvrURL`.
- Implemented in `AppSettings`: `service_name`, `static_id`, `start_timeout`,
  `dhcp_mode`, `dhcp_timeout`, `dhcp_option82`, `school_code`.
- `CTPassword`, client title, self-service URL, WiFi sharing and notification
  options are not part of the current EAP-MD5 auth path; they are documented but
  not added to the auth config.

## C# Version (root)

- `RuijieCampusAuth.Core/PrivateTail.cs` — CRC16/XMODEM, obfuscation,
  MD5-chained credential cipher, 0x39 service name, 0x54 static ID.
  The 569-byte output matches the Python reference byte-for-byte
  (`RuijieCampusAuth.Core.Tests`, SHA256 check).
- `RuijieCampusAuth.Core/Eapol.cs` — EAPOL/EAP framing, Identity/MD5,
  GB18030 server-message extraction.
- `RuijieCampusAuth.Core/Pcap.cs` — Npcap on Windows, libpcap on Linux/macOS
  via a DllImport resolver; shared `PcapDevice` send/receive.
- `RuijieCampusAuth.Core/Supplicant.cs` — EAP-MD5 state machine, private tail,
  locked-server unicast Start, group Logoff, retries/timeouts.
- `RuijieCampusAuth.Core/ServiceConfig.cs` — service.json read/write,
  LocalMachine DPAPI on Windows / 0600 plain on Unix; includes
  `takeover_enabled`, DHCP/school/Guardian fields.
- `RuijieCampusAuth.Core/UserStore.cs` — Python-compatible `accounts.json`,
  `settings.json`, `discovery.json`, and `servers.json`; machine DPAPI for
  account passwords so the LocalSystem service can read the pool; multi-account
  load/save and learned-server upsert.
- `RuijieCampusAuth.Core/GuardianClient.cs` — loopback-only MCP client;
  `initialize` + `guardian_status`, adapter-GUID match, fallback probe.
- `RuijieCampusAuth.Core/ServiceInstaller.cs` — Windows SCM P/Invoke service
  install/uninstall/start/stop; Linux writes an `hxxy-campus-auth.service`
  unit (no subprocess launch).
- `RuijieCampusAuth.Cli` — live auth, `config init|show|takeover`,
  `service install|uninstall|start|stop|status`.
- `RuijieCampusAuth.Service` — `AddWindowsService` / `AddSystemd` worker;
  AuthWorker with discovery cache + Guardian; TakeoverGuard/TakeoverService.
- `RuijieCampusAuth.App` — Avalonia Fluent light UI, `--tray --auto`, tray icon,
  account/server/auth/service/settings tabs; account pool with enable/disable,
  learned-server list with lock/unlock, and full config persistence.
- Verified: C# CLI and Windows service both authenticated live as SYSTEM;
  service install/start via CLI worked. Takeover wrote IFEO markers for
  enumerated official exe names, stopped/disabled `RJSuService`, and restored
  both when disabled.
- Build/publish: `build-csharp.ps1 -Runtime win-x64` (framework-dependent).
- Deploy path: `D:\Storageredirect\Program\RuijieCampusAuth`; GUI launch:
  `"D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" --tray --auto`.

## Architecture

- `src/ruijie_auth/cli.py`
  - Interface probing/selection.
  - EAPOL framing, EAP identity/MD5 responses, Supplicant state machine.
  - Ruijie private-tail integration.
  - Npcap send-queue burst sender for Windows (falls back to Scapy).
- `src/ruijie_auth/private.py`
  - 23-byte private header, CRC-16/XMODEM, bit-reverse/invert obfuscation.
  - 47-byte preamble and `0x1A` vendor attributes.
  - MD5-chained XOR credential encryption.
  - Network information collection.
- `src/ruijie_auth/api.py`
  - `RuijieClient` and `ruijie_hxxy.login()`.
- `src/ruijie_auth/gui.py`
  - PySide6 main window, account manager, tray, debug tools.
  - Server tab: learned authenticators, manual add/rename/delete, lock switch.
  - Connection tab advanced group: service name, static ID, Start interval,
    DHCP mode/timeout/Option 82, school code.
  - Linkage tab: Guardian MCP URL, enable switch, probe interval, offline count.
  - Preempt auth worker and TCP connectivity monitor.
  - AuthWorker polls Guardian while online; N confirmed failures trigger re-auth.
  - Takeover switch wiring: blocker + auth worker + heartbeat.
- `src/ruijie_auth/guardian.py`
  - Zero-dependency loopback MCP client (`initialize` + `guardian_status`).
  - Restricts URLs to `http://127.0.0.1:<port>/mcp` (or localhost), ignores
    environment proxies.
  - `select_probe` matches the auth NPF GUID to Guardian `adapterGuid`, then
    falls back to expected outlet/default route/global probe.
- `src/ruijie_auth/storage.py`
  - Frozen builds store accounts/settings in the install directory; source-tree
    development uses `%APPDATA%\RuijieCampusAuth`. Windows DPAPI passwords.
  - `ServerStore` persists learned authenticators to `servers.json`; discovery
    cache seeds it on first launch.
  - `install_dir()` for machine-wide state; `program_data_dir()` is legacy only.
- `src/ruijie_auth/blocker.py`
  - Kills 8021x.exe in a watchdog loop.
  - Writes 64/32-bit IFEO `Debugger` + `HXXYTakeover` marker.
  - Saves/restores previous values; cleans legacy markerless policies.
- `src/ruijie_auth/handoff.py`
  - Writes `takeover.ini` heartbeat every second in the install directory.
- `src/ruijie_auth/storage.py` (DiscoveryCache)
  - `discovery.json` caches interface/description/MAC/server MAC after any
    server EAP response; clears when the channel does not respond. Frozen builds
    use the install directory; the GUI exposes a force-rediscover button.
- `packaging/restart_ruijie.vbs`
  - Original-client launcher with four-check takeover health and stale/legacy
    recovery.
- `packaging/RuijieCampusAuth.spec`, `packaging/entry_gui.py`, `build.ps1`
  - PyInstaller onefile build, icon/version info, `-Deploy`.
- `tools/campus_monitor.py`
  - Passive 15-minute sampler; never sends EAPOL frames. Records USB NIC
    link/IP, source-bound egress probes, GUI/8021x/Supplicant processes,
    takeover/IFEO state, and app.log auth events. Writes
    `campus_status.json` in `default_data_dir()` (install dir for frozen
    builds, `%APPDATA%` for source runs) plus `logs/campus_status.jsonl`.
    Supports `--once`, `--interval`, `--until`, `--reset`. Probes pin egress
    with `IP_UNICAST_IF`.
- `tests/test_api.py`, `test_private.py`, `test_storage.py`, `test_handoff.py`,
  `test_gui.py`
  - API status, private tail, storage/DPAPI, heartbeat, offscreen GUI smoke.

## Protocol Facts

Wired HXXY flow: EAP-MD5.

- Control frames: EAPOL-Logoff / Start are 587 bytes: 14-byte Ethernet header,
  4-byte EAPOL header with length 0, then a 569-byte private tail.
- EAPOL-Start may be unicast to a learned authenticator MAC and is accepted.
  EAPOL-Logoff must stay on the standard PAE group `01:80:c2:00:00:03`: a
  unicast Logoff does not clear the switch session, and the next Start then
  receives EAP-Failure without an Identity request.
- Identity response: 601 bytes: EAPOL length field is 14 (the EAP length);
  the 569-byte tail is appended outside the EAPOL length.
- MD5 response: 618 bytes: EAPOL length field is 31; the tail is outside it.
- EAP-Success: 299 bytes: EAP length 281, including a 277-byte server private
  property block with GB18030 messages.

Private tail layout:

- Header: `00001311` + flag + four IPv4 fields + CRC16/XMODEM; the whole
  23-byte header is bit-reversed and inverted.
- Preamble: `00001311` + `8021x.exe` (padded 32) + `06560000` + flag byte +
  `00001311` + 2-byte attribute length.
- Attributes: `1A total_len 00001311 type len payload`.
- 0x2F credentials: key = username; plaintext = password padded to 16 bytes;
  IV = zero for Start/Logoff/Identity, MD5 challenge for the MD5 response.
  Cipher block = plain block XOR MD5(key || previous ciphertext/IV).
- 0x39 service name: server-provided type 0x66 (`电信宽带服务` on HXXY).
- 0x54 static ID: `Static:AB45A862` observed on the validation machine.
  0x39 service name and 0x54 static ID are configurable in 0.8.0; the service
  name lives in `TailOptions`, the static ID in `NetworkInfo`.
- Identity fingerprint 0x4D is stable on the validation machine
  (`5dbed5...`); the server accepted random values in the production-path
  test, so it is not strictly validated in the verified environment.

Server private properties (types 92/93) are RC4-obfuscated with key
`com.ruijie.www`; the client parses them but the outgoing credential tail uses
the username/password scheme above.

## Build / Test

```bash
python -m pip install -e .[gui,build]
python -m pytest -q
python -m ruijie_auth.gui --smoke-test
.\build.ps1 -SkipInstall -Deploy
```

Windows requires Npcap with WinPcap API compatibility and an elevated shell.
Linux requires libpcap and root (or equivalent raw-socket permission).

## GUI / Takeover / Launcher Handoff

- Takeover is enabled only when the blocker is running, the auth worker is
  running, preempt mode is on, and the block switch is checked.
- The app writes `takeover.ini` (`enabled=1`, PID, exe) every second.
- The blocker writes `Debugger` and `HXXYTakeover=RuijieCampusAuth` to both
  IFEO registry views and saves previous values in
  `<install_dir>\takeover_state.json`
  (`D:\Storageredirect\Program\RuijieCampusAuth`).
- `restart_ruijie.vbs` skips the official client only when:
  1. `enabled=1`;
  2. the heartbeat file mtime is <= 12 seconds old;
  3. the recorded PID is still running as `RuijieCampusAuth.exe` or
     `RuijieCampusAuth.Service.exe`;
  4. the IFEO marker exists in the 64- or 32-bit view.
- Otherwise the VBS kills a stale/hung GUI/service, runs
  `RuijieCampusAuth.exe --restore-client`, then kills 8021x and launches
  `RuijieSupplicant.exe` as before.
- C# `--restore-client` runs before any Avalonia/mutex initialization:
  it restores IFEO values and service `Start` types from `takeover_state.json`,
  removes the state/heartbeat files, and exits. If the state file is missing it
  removes markerless `cmd.exe /c exit` policies from the official client
  directory.
- VBS post-restore fallback: if `--restore-client` is missing, fails, or D: is
  offline, `AnyIfeoPolicyPresent` + `ClearIfeoPolicy` directly remove the
  `HXXYTakeover`/`Debugger` values for the seven official exe names
  (`8021x`, `RuijieSupplicant`, `NetworkDetect`, `snetcfg`, `SoftwareManager`,
  `SuInstaller`, `suservice`). Legitimate third-party Debugger values are not
  removed.
- `RunHXXYNet` scheduled task verified: action
  `wscript.exe "C:\Program Files\restart_ruijie.vbs"`, logon trigger for
  `ZHANGWINDOWS\Zhang`, `InteractiveToken`, `HighestAvailable`, no execution
  time limit, `IgnoreNew`, `StartWhenAvailable`. Last run returned 0 and the
  VBS logged `healthy takeover; skip original`.
- Deploy path: `D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe`;
  the launcher and shortcuts point there. `build-csharp.ps1 -Deploy` copies the
  published GUI/CLI/service, backs up and updates `C:\Program Files\restart_ruijie.vbs`,
  and creates `HXXY-CampusAuth.lnk` in the deploy dir and on the desktop.
- GUI / tray launch command (do not change):
  `"D:\Storageredirect\Program\RuijieCampusAuth\RuijieCampusAuth.exe" --tray --auto`.

### Deep Freeze / Long-Term Frozen C:

- If only C: is frozen and D: is thawed, normal binary/config updates under
  `D:\Storageredirect\Program\RuijieCampusAuth` persist without unfreezing.
  Stop the GUI and service, replace files, restart service and GUI.
- C: owns the service registration, `RunHXXYNet` task,
  `C:\Program Files\restart_ruijie.vbs`, shortcuts, .NET runtime, and Npcap.
  Changes there require thawing or must be in the frozen baseline.
- Do not run `build-csharp.ps1 -Deploy` while C: is frozen: it rewrites the VBS
  and desktop shortcut on C:, and those changes revert at reboot. Copy only the
  published files to D:.
- If takeover should always be on, set `RJSuService` to Disabled in the frozen
  baseline to avoid it starting before `HXXY-CampusAuth` at boot. If takeover
  must remain toggleable, freeze with takeover off (so IFEO is not in the
  baseline) and let the service reapply it at boot. The service now also
  recognizes its own IFEO values when `takeover_state.json` is missing, so a
  frozen marker baseline is cleared on a takeover-off startup.

### Frozen-C Acceptance Checklist

1. Confirm DiskGuard/Laneye excludes D: (and any other volume holding the
   install directory); the current D: is fixed disk 1, not C:.
2. Confirm D: is not encrypted/locked before Windows auto-start services run.
3. Confirm `net start HXXY-CampusAuth` works and `sc qc` shows
   `DEPENDENCIES: npcap`, `AUTO_START`, LocalSystem.
4. Confirm `sc qfailure HXXY-CampusAuth` shows restart actions 5s/10s/30s.
5. Confirm `RunHXXYNet` still has the VBS action and `HighestAvailable`, and
   `cscript` returns 0 with `healthy takeover; skip original`.
6. Create a marker file on D: and one on C:, freeze/reboot, then verify the D:
   marker persists, the C: marker is gone, service/GUI are running, and the
   launcher skipped the original client.
7. After reboot, check `logs\service.log` for successful auth and
   `logs\launcher.log` for a healthy takeover decision. NetworkGuardian's own
   config is on D: (`%LOCALAPPDATA%`) and its action remains
   `schtasks /run /i /tn RunHXXYNet`.

## Windows System Service (C#)

- `RuijieCampusAuth.Service.exe` is the service host
  (`Microsoft.Extensions.Hosting.WindowsServices`); Linux uses `AddSystemd`
  only when `INVOCATION_ID` is present. It never creates an Avalonia/Qt
  application.
- Service name `HXXY-CampusAuth`, LocalSystem, auto start. It starts at boot
  before user login and keeps the 802.1X session alive.
- Data root is the exe directory
  `D:\Storageredirect\Program\RuijieCampusAuth` (not `%ProgramData%`):
  `service.json`, `service_status.json`, `accounts.json`, `settings.json`,
  `discovery.json`, `servers.json`, `logs\service.log`, `logs\csharp-gui.log`,
  `takeover_state.json`, `takeover.ini`.
- GUI, CLI, and service all read/write the same install-directory files; there
  is no `%APPDATA%` runtime split. `UserStore` defaults to
  `AppContext.BaseDirectory`; the CLI uses `ServiceConfig.ServiceRoot`; the
  service already used the install directory.
- `AuthWorker` prefers `settings.json` + `accounts.json`; `service.json` remains
  the fallback account/settings source and owns the takeover switch. Active
  account first, then other enabled accounts, with a new pool read each retry
  cycle.
- One-time migration: on first GUI/CLI start, missing `accounts.json`,
  `settings.json`, `discovery.json`, or `servers.json` are copied from the old
  `%APPDATA%\RuijieCampusAuth` location. Existing target files are never
  overwritten; after migration all reads are direct from the install dir.
- The frozen Python build follows the same rule: `default_data_dir()` returns
  `install_dir()` when `sys.frozen` is set. Source-tree Python development still
  uses `%APPDATA%` to avoid polluting the repository.
- `service.json` stores the password with **LocalMachine DPAPI** and is ACLed to
  SYSTEM / Administrators only. Machine-wide state must survive the frozen C:
  volume; keep it on D:.
- The GUI exposes “系统服务开机自启”; applying service config writes the
  current account and settings, creates/updates the service, and starts it.
  When the service is running the GUI skips `--auto` and refuses to start its
  own AuthWorker.
- `restart_ruijie.vbs` reads `takeover.ini` and `takeover_state.json` from the
  install directory.
- `service_status.json` is written on state changes and read by the GUI's
  service status label.

## Important Implementation Notes

- The EAPOL length field must cover only the standard EAP packet. Putting the
  private tail inside the EAPOL length causes the server to reject Identity
  responses even when the tail bytes are identical to the official client.
- Control frames need the private tail; bare 18-byte Logoff/Start frames are
  not equivalent to the official client.
- Start/Logoff/Identity share one session nonce; the MD5 response uses a fresh
  nonce.
- Windows uses `pcap_sendqueue_transmit` to reproduce the official client's
  tight 3-frame bursts (~20 us spacing). Scapy's per-frame loop is much slower.
- A ~40 ms delay before EAP responses matches the official client.
- Duplicate EAP requests are ignored to avoid flooding the server.
- GUI settings persistence: every strategy switch (preempt/block/auto-start/
  start-minimized/close-minimize/verbose/logoff), server lock, Guardian linkage
  (`guardian_enabled/url/interval/failures`), and every connection field
  (interface, intervals, timeout, start/repeat counts, connectivity URL) is
  saved on change and restored by `_apply_settings`.
- The takeover checkbox auto-saves on toggle: it writes both user
  `settings.json` and the service `service.json` (`takeover_enabled`), so the
  running service applies/restores within its 2-second config poll and the
  checkbox stays set after a GUI restart. On startup the checkbox prefers the
  service config when `service.json` is present.
- Auto-detect uses `DiscoveryCache`: probe once, cache the interface/server MAC
  after **any** server EAP response (Success, Failure, Identity/MD5), then skip
  probing on later attempts. If no server EAP response arrives, the cache is
  cleared and the next attempt re-probes. Explicit `interface=` bypasses the
  cache. The GUI's force button clears the cache and aborts the current
  `Supplicant` so the next loop re-discovers.

## Known Limitations / Next Steps

- OpenWrt build/environment is deployed on the Ubuntu 26.04 VM and verified
  (2026-11-18); details and commands are in `docs/OPENWRT_BUILD_ENV.md`.
  Go 1.25.5 is at `/usr/local/go`; the 24.10.0 `ramips/mt7621` Image Builder is
  at `/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64`
  (SHA256 `37b9bff3...571b3e`). Ubuntu 26.04 maps `/usr/bin/install` to
  uutils, so Image Builder must run with
  `PATH=/opt/openwrt/gnu-bin:$PATH` where `install -> /usr/bin/gnuinstall`.
  A stock `xiaomi_mi-router-3g` image and a `FILES=files/` overlay image both
  built successfully; `sha256sum -c sha256sums` passed, and the overlay marker
  plus LuCI assets were found in `root.squashfs`. Go `linux/mipsle`
  `GOMIPS=softfloat` smoke tests passed under `qemu-mipsel`, including
  `x/text` GB18030 (`b5e7d0c5bfedb4f8b7fecef1`, matching Python). The R3G
  sysupgrade image cannot boot in QEMU (no MT7621 model); an x86_64 OpenWrt
  24.10 QEMU instance is running for LuCI testing (VM-local 18080/18443;
  host-accessible via socat 28080/28443), root/empty password, image
  `/opt/openwrt/qemu/openwrt-x86-64.img`, serial log
  `/opt/openwrt/qemu/serial.log`. QEMU 10.2.1 hostfwd hangs on non-loopback
  sources, hence the socat hop. It is stock OpenWrt, not the custom app yet.
  The clean R3G sysupgrade image SHA256 is
  `c4742ea371e7053933a308e1ef92761dad2dfd77aa4f13fc58992d10ec133e4a`.
  Credentials for the VM itself are not stored here.
- `openwrt/` — OpenWrt source tree (current validation target R3G). The
  `hxxy-auth/` Go module has the full protocol core (private tail,
  CRC16/XMODEM, bit obfuscation, MD5-chained credentials, EAPOL, EAP-MD5,
  GB18030 messages; golden test
  `779ea31765daef370dd54c243e0ac4bf0912bf183f889ac50ff7bb1bc3cfbfe5`), a
  resident AF_PACKET supplicant, and a daemon with UCI `config account`
  account pool (max 5, active-first failover), server learning,
  `servers.json`/`discovery.json`, auto guard (offline re-auth), continuous
  preemption (periodic re-auth), pause-on-home, Unix control socket, and
  1MB log rotation. CLI: `version`, `status`, `tail`, `selftest`, `daemon`,
  `auth/logoff/pause/resume/reload`, `accounts`, `servers`,
  `discovery`, `mac`. LuCI has six pages
  (overview/accounts/servers/mac/settings/logs) with UCI accounts,
  server/cache management, MAC quick change, and control buttons; procd
  init/hotplug/uci-defaults are included. Account passwords are plaintext in
  `/etc/config/hxxy-auth` (0600) per the user's deployment decision; no DPAPI.
  A full R3G firmware (14MB, SHA256
  `35f72b0c8504a0b4b79ccea4b3d3081f79e9bd383437c5ea8b22294ff5874476`)
  contains the daemon, UA3F 3.7.0 mipsel (NFQUEUE L3: TTL 128/IPID 0/
  TCP timestamp removed/SYN window 65535; UA rewriting OFF),
  https-dns-proxy + luci-app/i18n zh-cn (AliDNS/Tencent DoH),
  iptables/nft kmods, MAC page, disguise script, init/hotplug, UCI config,
  and six LuCI views. Default `disguise=full`; sysctl TTL 128/timestamps
  off/ECN off/IPv6 off; flow offload off.
  x86 QEMU verification: the daemon opens AF_PACKET on `br-lan`, enters
  `paused` when no authenticator exists and the localhost probe is online,
  the overview shows account/interface, Resume re-runs auth and pauses again,
  and the accounts/servers/settings pages render. Source lives in the Windows
  repo; compile/test/image work happens only on the Ubuntu VM (see
  `docs/OPENWRT_BUILD_ENV.md`). Source is generic OpenWrt: only the Image
  Builder `PROFILE`/device packages, `GOARCH`, UCI `device`, and UA3F kmods are
  target-specific. Next: live R3G validation (`wan` DSA, DHCP timing,
  100Mbps iperf3), UA3F NFQUEUE throughput, and MAC ifup/re-auth timing.
- `docs/OPENWRT_FEASIBILITY.md` — OpenWrt soft-router port assessment, updated
  for the Xiaomi R3G target. The auth algorithm is fully portable; account
  pool, failover, discovery, server lock, logging, status, and home-network
  pause are portable. Windows takeover, IFEO, DPAPI, Guardian loopback MCP,
  SCM/systemd installers, and the Avalonia GUI are not. R3G original
  (MT7621A/MIPS32, 256MB RAM, 128MB NAND) should use a Go `linux/mipsle`
  `GOMIPS=softfloat` daemon + procd + UCI. `.NET` has no MIPS32 runtime, so C#
  is excluded. LuCI is a hard requirement: five pages (overview/accounts/
  servers/settings/logs) use `L.uci` for non-secret settings and `rpcd` +
  `fs.exec` against the `hxxy-auth` CLI for status/control/accounts/servers/
  logs; accounts live in a 0600 `accounts.json`, not UCI. Anti-sharing disguise
  is tiered: `basic` (MAC clone, Windows DHCP fingerprint, TTL 128, MSS clamp,
  IPv6 off, WAN drop, UPnP off) is default; `full` adds UA3F
  (`mipsel_24kc`, 5.43MiB) L3 rewrite. MIPS32 cannot use UA3F eBPF, so `full`
  runs through NFQUEUE; the campus link is 100Mbps, so throughput is workable
  but still measured with `iperf3`. Pure DHCP and pre-auth DHCP are confirmed.
  No R3G hardware is on hand yet; build/unit/veth/QEMU first, then live test.
  The campus has not kicked a Windows mobile hotspot, so multi-device detection
  appears weak; remaining live unknowns are account/MAC binding, static-ID
  validation, server-initiated re-auth, and DHCP fingerprinting.
- `Static:AB45A862` and the default service name `电信宽带服务` are observed
  HXXY/test-machine constants. Other machines/schools may require deriving the
  static ID or parsing type 0x66 from the server.
- Linux burst sending falls back to Scapy `inter=0`; not yet validated against
  a live Linux deployment.
- `--logoff` sends the standard EAPOL-Logoff path; the official client detected
  `AUTH_ALREADY_LOGOFF`, but a Python-only logoff test was not isolated.
- PEAP code exists in the official client but the wired HXXY network uses
  EAP-MD5; PEAP is out of scope for this implementation.
- The VBS health check depends on WMI and `reg.exe`; if WMI is unavailable it
  falls back to launching the original client (safe but takeover is not
  skipped). The VBS/launcher path is fixed to the deploy directory; update it
  if the exe moves.
- The campus server rejects authentication outside its time window
  (`不在认证时段内!`). The GUI will keep retrying; a long backoff on that
  message is a possible next improvement.
- 0.5.0 discovery-cache behavior and the new layout were unit/offscreen tested;
  a fresh live cache regression was not possible after the window closed.
- Machine-wide takeover state and all runtime config live in the install
  directory on D: (`takeover_state.json`, `takeover.ini`, `service.json`,
  `accounts.json`, `settings.json`, `discovery.json`, `servers.json`); the GUI
  must run elevated.
- The Windows service reads `settings.json` + `accounts.json` from the install
  directory: it tries the active enabled account first, then the remaining
  enabled accounts, and falls back to `service.json` when the pool is empty.
  Account passwords are machine-DPAPI encrypted so LocalSystem can decrypt
  them; GUI startup upgrades old user-DPAPI `accounts.json` entries in place.
- Home/non-802.1X LAN behavior: if authentication fails with no server
  response (`Supplicant.Connected == false`) and Guardian/TCP reports the
  network online, the service enters `paused` instead of retrying 802.1X. It
  rechecks every 30 seconds and resumes authentication when the network goes
  offline (campus return). Takeover remains active while paused; disable
  takeover or stop the service to run the official client on a home LAN.
- Linux `ServiceInstaller.Install` only writes the systemd unit; start/stop are
  left to `systemctl`. macOS launchd is not implemented.
- The analysis artifacts (IDA database, pcaps, Frida logs) are local temporary
  files and are not part of the repository.

## Security

No test account or password is stored in the repository. Tests use dummy
credentials. The README does not contain captured credentials. The GUI account
store lives in the install directory (`accounts.json`); passwords use machine
DPAPI so the LocalSystem service can read the pool, and are not committed. The
validation account was supplied by the user and is stored only in that local
file.
