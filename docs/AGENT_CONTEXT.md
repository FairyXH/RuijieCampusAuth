# Agent Context

## Project

`ruijie-campus-auth-hxxy` is a cross-platform (Windows/Linux) Python 802.1X
supplicant for the Ruijie campus network at Xiamen Huaxia University (HXXY).

The client authenticates with the normal campus username/password using
EAP-MD5 and reproduces the Ruijie vendor private tail used by the official
Windows client. It is a compatibility client, not an authentication bypass.

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
  - Preempt auth worker and TCP connectivity monitor.
  - Takeover switch wiring: blocker + auth worker + heartbeat.
- `src/ruijie_auth/storage.py`
  - `%APPDATA%\RuijieCampusAuth` accounts/settings, Windows DPAPI passwords.
  - `program_data_dir()` for machine-wide state.
- `src/ruijie_auth/blocker.py`
  - Kills 8021x.exe in a watchdog loop.
  - Writes 64/32-bit IFEO `Debugger` + `HXXYTakeover` marker.
  - Saves/restores previous values; cleans legacy markerless policies.
- `src/ruijie_auth/handoff.py`
  - `%ProgramData%\RuijieCampusAuth\takeover.ini` heartbeat every second.
- `packaging/restart_ruijie.vbs`
  - Original-client launcher with four-check takeover health and stale/legacy
    recovery.
- `packaging/RuijieCampusAuth.spec`, `packaging/entry_gui.py`, `build.ps1`
  - PyInstaller onefile build, icon/version info, `-Deploy`.
- `tests/test_api.py`, `test_private.py`, `test_storage.py`, `test_handoff.py`,
  `test_gui.py`
  - API status, private tail, storage/DPAPI, heartbeat, offscreen GUI smoke.

## Protocol Facts

Wired HXXY flow: EAP-MD5.

- Control frames: EAPOL-Logoff / Start are 587 bytes: 14-byte Ethernet header,
  4-byte EAPOL header with length 0, then a 569-byte private tail.
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
  `%ProgramData%\RuijieCampusAuth\blocker_state.json`.
- `restart_ruijie.vbs` skips the official client only when:
  1. `enabled=1`;
  2. the heartbeat file mtime is <= 12 seconds old;
  3. the recorded PID is still running as `RuijieCampusAuth.exe`;
  4. the IFEO marker exists in the 64- or 32-bit view.
- Otherwise the VBS kills a stale/hung GUI, runs
  `RuijieCampusAuth.exe --restore-client`, then kills 8021x and launches
  `RuijieSupplicant.exe` as before.
- `--restore-client` also removes markerless legacy IFEO policies from older
  builds and writes `enabled=0`.
- Deploy path: `D:\Storageredirect\Program\RuijieCampusAuth.exe`; the launcher
  and shortcuts point there. `build.ps1 -Deploy` copies the exe, backs up and
  updates `C:\Program Files\restart_ruijie.vbs`, and creates
  `HXXY-CampusAuth.lnk` in the deploy dir and on the desktop.

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

## Known Limitations / Next Steps

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
- The takeover handoff/blocker state is machine-wide under
  `%ProgramData%\RuijieCampusAuth`; the GUI must run elevated. Accounts and
  settings remain per-user under `%APPDATA%\RuijieCampusAuth`.
- The analysis artifacts (IDA database, pcaps, Frida logs) are local temporary
  files and are not part of the repository.

## Security

No test account or password is stored in the repository. Tests use dummy
credentials. The README does not contain captured credentials. The GUI account
store lives in `%APPDATA%\RuijieCampusAuth\accounts.json`; passwords are
DPAPI-encrypted and are not committed. The validation account was supplied by
the user and is stored only in that local per-user file.
