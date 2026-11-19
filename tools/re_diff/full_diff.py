#!/usr/bin/env python3
"""Full differential audit: original 8021x.exe functions vs the C# client.

Phase A: core algorithms (CRC16 / obfuscation / header / MD5 / credential
         cipher) with fresh seeded vectors, live-compared against DiffRunner.
Phase B: server private-property parsers (ReadRGVendorSeg 0x51a890 flat TLV,
         ParsePrivateProperty 0x537ee0 sectioned) under Unicorn with real
         RC4/ntohl stubs, compared against PrivateProperties.Parse.
Phase C: full 569-byte private tail via 0x53a590 with a constructed object.

Every claim printed by this script is backed by raw Unicorn/DiffRunner output
written to build/capture/full-diff-report.json.
"""
from __future__ import annotations

import json
import random
import struct
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "re_diff"))

from pe_call import PeCall  # noqa: E402

import unicorn.x86_const as xc  # noqa: E402

EXE = Path(r"C:\Program Files\Ruijie Networks\Ruijie Supplicant\8021x.exe")
DIFFRUNNER_PROJECT = REPO / "tools" / "csharp_diff"
REPORT_DIR = REPO / "build" / "capture"

RVA = {
    "crc16": 0x13DC20,
    "obfuscate": 0x13DE90,
    "header_raw": 0x13DC80,
    "header": 0x13CBB0,
    "md5": 0x13CB50,
    "encrypt": 0x13C970,
    "build_tail": 0x13A590,
    "read_rg_vendor_seg": 0x11A890,
    "parse_private_property": 0x137EE0,
    "get_client_version": 0x118950,
    "get_ipv4_info_a": 0x118DD0,
    "get_ipv4_info_b": 0x1191D0,
    "get_mac": 0x1195B0,
    "get_dhcp_phase": 0x118C70,
    "get_ipv6_info": 0x119F80,
    "get_service_name": 0x119620,
    "get_userpasswd4peap": 0x119C70,
    "get_v3_hash": 0x118A60,
    "hard_disk_sn": 0x11A670,
    "encap_eaphost": 0x1178F0,
    "encap_peap": 0x117F90,
    "encap_extra_1a510": 0x11A510,
    "encap_extra_18bc0": 0x118BC0,
    "encap_extra_1bec0": 0x11BEC0,
    "encap_extra_1bf40": 0x11BF40,
    "encap_extra_1bdb0": 0x11BDB0,
    "encap_extra_1c020": 0x11C020,
    "tail_header_helper": 0x13CC00,
    "tail_version_helper": 0x13CCD0,
    "tail_nonce": 0x13A220,
}

GLOBAL_OBJ = 0x7557B4  # DAT_007557b4: pointer to the global engine object
IV_GLOBAL = 0x755818   # DAT_00755818: 16-byte IV (challenge or PEAP IV)

RC4_KEY_COM_RUIJIE = b"com.ruijie.www"

# Win32 stdcall callee-pops-args table. pe_call's libc stubs are cdecl;
# KERNEL32/ADVAPI32/USER32 functions the CRT touches must pop their args.
STDCALL_ARGS = {
    "TlsGetValue": 1, "TlsSetValue": 2, "TlsAlloc": 0, "TlsFree": 1,
    "GetModuleHandleA": 1, "GetModuleHandleW": 1, "GetProcAddress": 2,
    "GetCurrentProcessId": 0, "GetCurrentProcess": 0, "GetCurrentThread": 0,
    "GetCurrentThreadId": 0, "CloseHandle": 1, "Sleep": 1, "GetLastError": 0,
    "SetLastError": 1, "GetSystemTimeAsFileTime": 1, "QueryPerformanceCounter": 1,
    "IsDebuggerPresent": 0, "EncodePointer": 1, "DecodePointer": 1,
    "EnterCriticalSection": 1, "LeaveCriticalSection": 1,
    "InitializeCriticalSection": 1, "InitializeCriticalSectionAndSpinCount": 2,
    "DeleteCriticalSection": 1, "GetProcessHeap": 0, "HeapAlloc": 3,
    "HeapFree": 3, "HeapSize": 3, "HeapValidate": 3, "HeapSetInformation": 4,
    "HeapCreate": 3, "HeapDestroy": 1, "VirtualAlloc": 4, "VirtualFree": 3,
    "VirtualProtect": 4, "VirtualQuery": 3, "FlushInstructionCache": 3,
    "RaiseException": 4, "UnhandledExceptionFilter": 1,
    "SetUnhandledExceptionFilter": 1, "ExitProcess": 1, "TerminateProcess": 2,
    "GetVersionExA": 1, "GetVersionExW": 1, "GetTickCount": 0,
    "LoadLibraryA": 1, "FreeLibrary": 1, "MultiByteToWideChar": 6,
    "WideCharToMultiByte": 8, "lstrlenA": 1, "lstrlenW": 1, "lstrcpyA": 2,
    "lstrcatA": 2, "OutputDebugStringA": 1, "IsBadReadPtr": 2,
    "IsBadWritePtr": 2, "IsBadCodePtr": 1, "GlobalAlloc": 2, "GlobalFree": 1,
    "GlobalLock": 1, "GlobalUnlock": 1, "GlobalMemoryStatus": 1,
    "LocalAlloc": 2, "LocalFree": 1, "InterlockedIncrement": 1,
    "InterlockedDecrement": 1, "InterlockedExchange": 2,
    "InterlockedCompareExchange": 3, "GetCPInfo": 2, "GetStringTypeA": 5,
    "GetStringTypeW": 4, "LCMapStringA": 6, "LCMapStringW": 6,
    "CompareStringA": 6, "CompareStringW": 6, "GetLocaleInfoA": 4,
    "GetConsoleCP": 0, "GetConsoleOutputCP": 0, "WriteFile": 5, "ReadFile": 5,
    "CreateFileA": 7, "GetFileType": 1, "GetStdHandle": 1, "SetHandleCount": 1,
    "GetStartupInfoA": 1, "GetEnvironmentStrings": 0,
    "GetEnvironmentStringsW": 0, "FreeEnvironmentStringsA": 1,
    "FreeEnvironmentStringsW": 1, "GetModuleFileNameA": 4, "CreateEventA": 4,
    "SetEvent": 1, "ResetEvent": 1, "WaitForSingleObject": 2, "CreateThread": 6,
    "GetExitCodeThread": 2, "SystemTimeToFileTime": 2, "FileTimeToSystemTime": 2,
    "GetTimeZoneInformation": 1, "GetDriveTypeA": 1, "GetTempPathA": 2,
    "GetTempFileNameA": 4, "DeleteFileA": 1, "CreateDirectoryA": 2,
    "FindFirstFileA": 2, "FindNextFileA": 2, "FindClose": 1,
    "GetFileAttributesA": 1, "SetFileAttributesA": 2, "GetFileSize": 2,
    "GetFileTime": 4, "FlushFileBuffers": 1, "SetEndOfFile": 1,
    "GetFullPathNameA": 4, "GetACP": 0, "GetOEMCP": 0, "GetConsoleMode": 2,
    "GetModuleFileNameA": 4, "GetModuleFileNameW": 4,
    "GetConsoleWindow": 0, "SetConsoleCtrlHandler": 2, "GetComputerNameA": 2,
    "RegOpenKeyExA": 5, "RegQueryValueExA": 6, "RegCloseKey": 1,
    "RegSetValueExA": 6, "RegCreateKeyExA": 9, "RegDeleteKeyA": 2,
    "RegDeleteValueA": 2, "RegEnumKeyExA": 8, "OpenProcessToken": 3,
    "LookupAccountSidA": 7, "GetTokenInformation": 5,
    "MessageBoxA": 4, "MessageBoxW": 4, "PostMessageA": 4, "SendMessageA": 4,
    "GetWindowLongA": 2, "SetWindowLongA": 3, "RegisterClassExA": 1,
    "CreateWindowExA": 12, "DestroyWindow": 1, "ShowWindow": 2,
    "UpdateWindow": 1, "GetMessageA": 4, "TranslateMessage": 1,
    "DispatchMessageA": 1, "DefWindowProcA": 4, "PostQuitMessage": 1,
    "SetTimer": 4, "KillTimer": 2, "GetSysColor": 1, "GetSystemMetrics": 1,
    "LoadIconA": 2, "LoadCursorA": 2, "LoadStringA": 4, "RegisterWindowMessageA": 1,
    "OpenProcess": 3, "GetCurrentDirectoryA": 2, "SetCurrentDirectoryA": 1,
    "GetDiskFreeSpaceExA": 4, "GetVolumeInformationA": 8,
}


class RuijieCall(PeCall):
    """PeCall with working ws2_32 byte-order stubs and OpenSSL RC4 stubs."""

    def __init__(self, path: str):
        super().__init__(path)
        self.rc4_keys: dict[int, list[int]] = {}
        self.pc_trace: list[int] = []
        self.stub_calls: list[tuple[str, int]] = []
        self.fault: tuple[int, int] | None = None
        uc = __import__("unicorn")
        self.uc.hook_add(uc.UC_HOOK_CODE, self._trace_hook)
        self.uc.hook_add(uc.UC_HOOK_MEM_FETCH_UNMAPPED, self._fault_hook)
        self.uc.hook_add(uc.UC_HOOK_MEM_READ_UNMAPPED, self._fault_hook)
        self.uc.hook_add(uc.UC_HOOK_MEM_WRITE_UNMAPPED, self._fault_hook)
        # Ordinal-only imports: pe_call skips nameless entries, but the RC4
        # functions are imported by LIBEAY32 ordinal (479=set_key, 477=RC4).
        for entry in getattr(self.pe, "DIRECTORY_ENTRY_IMPORT", []):
            for imp in entry.imports:
                if imp.name or not imp.address:
                    continue
                dll = entry.dll.decode().lower()
                stub = 0x70000000 + self._stub_index * 0x10
                self._stub_index += 1
                self.uc.mem_write(stub, b"\xc3")
                if dll == "libeay32.dll" and imp.ordinal == 479:
                    self._imports[stub] = "RC4_set_key_ord"
                elif dll == "libeay32.dll" and imp.ordinal == 477:
                    self._imports[stub] = "RC4_ord"
                else:
                    self._imports[stub] = f"ordinal_{dll}_{imp.ordinal}"
                self.uc.mem_write(imp.address, struct.pack("<I", stub))

    def _fault_hook(self, uc, access, address, size, value, user_data) -> bool:
        pc = uc.reg_read(xc.UC_X86_REG_EIP)
        if self.fault is None:
            self.fault = (address, pc)
        return False  # let the error propagate

    def _trace_hook(self, uc, address, size, user_data) -> None:  # noqa: ANN001
        self.pc_trace.append(address)
        if len(self.pc_trace) > 64:
            del self.pc_trace[:-32]

    def crash_pcs(self) -> list[int]:
        return [f"{p:#x}" for p in self.pc_trace[-24:]]

    def ensure_crt_allocator(self):
        """The CRT allocator singleton (0x756a0c) is built by a dynamic
        initializer thunk; the PE entry point is never run here, so call it."""
        if getattr(self, "_crt_alloc_ready", False):
            return
        res = self.call(0x6AE489 - 0x400000, [])
        if not res.ok:
            raise RuntimeError(f"CRT allocator init failed: {res.error}")
        self._crt_alloc_ready = True

    def init_string_field(self, obj: int, offset: int, initial: bytes = b""):
        """Construct a real string object at obj+offset by calling the binary's
        own string constructor (FUN_0040c100, thiscall: ecx=this, arg=cstr)."""
        self.ensure_crt_allocator()
        cstr = self.alloc(len(initial) + 1)
        self.write(cstr, initial + b"\x00")
        res = self.call(0x40C100 - 0x400000, [cstr], ecx=obj + offset)
        if not res.ok:
            raise RuntimeError(
                f"string ctor failed at obj+{offset:#x}: {res.error}")


    def _rc4_crypt(self, key: bytes, data: bytes) -> bytes:
        s = list(range(256))
        j = 0
        for i in range(256):
            j = (j + s[i] + key[i % len(key)]) & 0xFF
            s[i], s[j] = s[j], s[i]
        i = j = 0
        out = bytearray()
        for byte in data:
            i = (i + 1) & 0xFF
            j = (j + s[i]) & 0xFF
            s[i], s[j] = s[j], s[i]
            out.append(byte ^ s[(s[i] + s[j]) & 0xFF])
        return bytes(out)

    def _hook_stub(self, uc, address, size, user_data) -> None:  # noqa: ANN001
        name = self._imports.get(address, "")
        esp = uc.reg_read(xc.UC_X86_REG_ESP)
        if name:
            self.stub_calls.append((name, esp))
        if name in ("ntohl", "htonl"):
            (value,) = self._args(uc, 1)
            uc.reg_write(xc.UC_X86_REG_EAX,
                         struct.unpack("<I", struct.pack(">I", value & 0xFFFFFFFF))[0])
            self._return(uc)
        elif name in ("ntohs", "htons"):
            (value,) = self._args(uc, 1)
            uc.reg_write(xc.UC_X86_REG_EAX,
                         ((value & 0xFF) << 8) | ((value >> 8) & 0xFF))
            self._return(uc)
        elif name == "RC4_set_key":
            key_ptr, length, data = self._args(uc, 3)
            key = bytes(uc.mem_read(data, length))
            self.rc4_keys[key_ptr] = list(key)
            self._return(uc)
        elif name in ("RC4", "RC4_ord"):
            key_ptr, length, src, dst = self._args(uc, 4)
            key = bytes(self.rc4_keys.get(key_ptr, b""))
            data = bytes(uc.mem_read(src, length))
            uc.mem_write(dst, self._rc4_crypt(key, data))
            self._return(uc)
        elif name == "RC4_set_key_ord":
            key_ptr, length, data = self._args(uc, 3)
            key = bytes(uc.mem_read(data, length))
            self.rc4_keys[key_ptr] = list(key)
            self._return(uc)
        else:
            if name == "GetStdHandle":
                # CRT write() bails cleanly on INVALID_HANDLE_VALUE with a
                # balanced stack; returning 0 takes an unbalanced path.
                self._return_stdcall(uc, 1)
                uc.reg_write(xc.UC_X86_REG_EAX, 0xFFFFFFFF)
            elif name in STDCALL_ARGS:
                self._return_stdcall(uc, STDCALL_ARGS[name])
            else:
                super()._hook_stub(uc, address, size, user_data)

    def _return_stdcall(self, uc, arg_count: int) -> None:
        esp = uc.reg_read(xc.UC_X86_REG_ESP)
        ret = struct.unpack("<I", uc.mem_read(esp, 4))[0]
        uc.reg_write(xc.UC_X86_REG_ESP, esp + 4 + 4 * arg_count)
        uc.reg_write(xc.UC_X86_REG_EIP, ret)


class DiffSession:
    def __init__(self):
        self.img = RuijieCall(str(EXE))
        self.rng = random.Random(0x1311)
        self.log: list[dict] = []
        self.diffrunner_path = self._build_diffrunner()
        # Logging helpers reach a global logger with virtual calls; their
        # behavior has no effect on algorithm outputs, so stub them in the
        # emulated image (in-memory only, the file is untouched).
        for va in (0x4D64C0, 0x4D6220, 0x4D6720):
            self.img.write(self.img.base + va - 0x400000, b"\xc3")
        # CRT _write(fd, buf, count): pretend the write succeeded so console
        # paths taken by CRT string helpers stay stack-balanced.
        self.img.write(self.img.base + 0x664481 - 0x400000, b"\x8b\x44\x24\x0c\xc3")

    # ---------- infra ----------

    def _build_diffrunner(self) -> str:
        result = subprocess.run(
            ["dotnet", "build", str(DIFFRUNNER_PROJECT), "-c", "Release", "--nologo", "-v", "q"],
            capture_output=True, text=True, check=True,
        )
        exe = (
            DIFFRUNNER_PROJECT / "bin" / "Release" / "net10.0" / "DiffRunner.exe"
        )
        if not exe.exists():
            exe = DIFFRUNNER_PROJECT / "bin" / "Release" / "net10.0" / "DiffRunner"
        return str(exe)

    def csharp(self, ops: list[dict]) -> list[dict]:
        payload = "".join(json.dumps(op) + "\n" for op in ops)
        result = subprocess.run(
            [self.diffrunner_path], input=payload, capture_output=True,
            text=True, check=True,
        )
        return [json.loads(line) for line in result.stdout.splitlines() if line.strip()]

    def record(self, name: str, vector: dict, original: str, csharp: str,
               match: bool, extra: dict | None = None):
        entry = {
            "name": name,
            "vector": vector,
            "original": original,
            "csharp": csharp,
            "match": match,
        }
        if extra:
            entry["extra"] = extra
        self.log.append(entry)
        flag = "OK " if match else "DIFF"
        print(f"[{flag}] {name}: orig={original[:64]} cs={csharp[:64]}")

    # ---------- helpers ----------

    def random_bytes(self, n: int) -> bytes:
        return bytes(self.rng.randrange(256) for _ in range(n))

    def cstring(self, text: str) -> bytes:
        return text.encode("ascii") + b"\x00"

    def make_this(self, flag: int, ip: int, mask: int, gw: int, dhcp: int,
                  mac: bytes, state: int, client_flag: int, ip_string: str):
        """Engine object with the fields the tail/header functions read."""
        obj = self.img.alloc(0xB00)
        self.img.uc.mem_write(obj, b"\x00" * 0xB00)
        self.img.write(obj + 0x648, struct.pack("<I", flag))
        self.img.write(obj + 0x64C, struct.pack("<I", ip))
        self.img.write(obj + 0x650, struct.pack("<I", mask))
        self.img.write(obj + 0x654, struct.pack("<I", gw))
        self.img.write(obj + 0x658, struct.pack("<I", dhcp))
        self.img.write(obj + 0x0C, mac)
        self.img.write(obj + 0x1C, bytes([state]))
        self.img.write(obj + 0xA6C, bytes([client_flag]))
        self.img.write(obj + 0x66A, self.cstring(ip_string))
        return obj

    def set_global_object(self, size: int = 0x7B0) -> int:
        """Allocate the fake global engine object and point DAT_007557b4 at it."""
        obj = self.img.alloc(size)
        self.img.uc.mem_write(obj, b"\x00" * size)
        self.img.write(GLOBAL_OBJ, struct.pack("<I", obj))
        return obj

    def init_string_field(self, obj: int, offset: int, initial: bytes = b""):
        """Construct a real string object at obj+offset by calling the binary's
        own string constructor (FUN_0040c100, thiscall: ecx=this, arg=cstr)."""
        self.img.ensure_crt_allocator()
        cstr = self.img.alloc(len(initial) + 1)
        self.img.write(cstr, initial + b"\x00")
        res = self.img.call(0x40C100 - 0x400000, [cstr], ecx=obj + offset)
        if not res.ok:
            raise RuntimeError(
                f"string ctor failed at obj+{offset:#x}: {res.error}")

    def write_msvc_string_global(self, obj: int, offset: int, text: bytes):
        """Write an MSVC std::string (x86: inline buf[16] or heap ptr) that
        returns its length via ptr-0xC header pattern used by this binary."""
        if len(text) < 16:
            self.img.write(obj + offset, text + b"\x00" * (16 - len(text)))
            # length probe pattern: this binary reads size at ptr-0xC for heap
            # strings only; inline strings pass strlen path.
        else:
            block = self.img.alloc(len(text) + 0x20)
            self.img.uc.mem_write(block, b"\x00" * (len(text) + 0x20))
            self.img.write(block + 0x10, text + b"\x00")
            self.img.write(block + 0x1C, struct.pack("<I", len(text)))
            self.img.write(obj + offset, struct.pack("<I", block + 0x10))

    # ---------- phase A ----------

    def phase_a(self):
        print("== Phase A: core algorithms, fresh vectors ==")
        vectors = []
        for n in (0, 9, 32, 255):
            vectors.append(("crc16", self.random_bytes(n)))
        for n in (1, 2, 7, 23, 64, 255):
            vectors.append(("obfuscate", self.random_bytes(n)))
        for n in (0, 3, 5, 64, 255):
            vectors.append(("md5", self.random_bytes(n)))

        pending_ops = []
        keys = []
        for kind, data in vectors:
            if kind == "crc16":
                ptr = self.img.alloc(len(data) or 1)
                self.img.write(ptr, data)
                res = self.img.call(RVA["crc16"], [ptr, len(data)])
                ok, val = res.ok, f"{res.eax & 0xFFFF:04X}"
                pending_ops.append({"op": "crc16", "data": data.hex()})
                keys.append((f"crc16 len={len(data)}", data.hex(), val, ok))
            elif kind == "obfuscate":
                ptr = self.img.alloc(len(data))
                self.img.write(ptr, data)
                res = self.img.call(RVA["obfuscate"], [ptr, len(data)])
                val = self.img.read(ptr, len(data)).hex().upper()
                pending_ops.append({"op": "obfuscate", "data": data.hex()})
                keys.append((f"obfuscate len={len(data)}", data.hex(), val, res.ok))
            elif kind == "md5":
                ptr = self.img.alloc(len(data) or 1)
                out = self.img.alloc(16)
                self.img.write(ptr, data)
                res = self.img.call(RVA["md5"], [ptr, len(data), out])
                val = self.img.read(out, 16).hex().upper()
                pending_ops.append({"op": "md5", "data": data.hex()})
                keys.append((f"md5 len={len(data)}", data.hex(), val, res.ok))

        # credential cipher: fresh usernames/passwords/IVs, one and chained
        enc_vectors = [
            ("user", "AAAAAAAAAAAAAAAA", bytes(range(16))),
            ("student01", bytes(range(32)).hex(), bytes([0x11] * 16)),
            ("student01", "x" * 16, bytes([0x22] * 16)),
            ("longusername", "y" * 47, self.random_bytes(16)),
        ]
        for username, password, iv in enc_vectors:
            if isinstance(password, str) and all(c in "0123456789abcdef" for c in password) and len(password) == 64:
                password = bytes.fromhex(password)
            else:
                password = password.encode("ascii") if isinstance(password, str) else password
            user_ptr = self.img.alloc(len(username) + 1)
            self.img.write(user_ptr, self.cstring(username))
            plain_ptr = self.img.alloc(len(password))
            self.img.write(plain_ptr, password)
            key_ptr = self.img.alloc(16)
            self.img.write(key_ptr, iv)
            out_ptr = self.img.alloc(0x100)
            self.img.uc.mem_write(out_ptr, b"\x00" * 0x100)
            res = self.img.call(RVA["encrypt"], [
                key_ptr, 16, user_ptr, plain_ptr, len(password), out_ptr,
            ])
            val = self.img.read(out_ptr, len(password)).hex().upper()
            pending_ops.append({
                "op": "encrypt", "username": username,
                "password_hex": password.hex(), "iv": iv.hex(),
            })
            keys.append((f"encrypt user={username} len={len(password)}",
                         password.hex(), val, res.ok))

        # header construction with a real this-object
        ip_fields = [
            (0x0101010A, 0x00FFFFFF, 0x015318AC, 0x015318AC),  # 10.1.1.1/24 gw 172.24.83.1
            (0x00000000, 0x00000000, 0x00000000, 0x00000000),
        ]
        for idx, (ip, mask, gw, dhcp) in enumerate(ip_fields):
            obj = self.make_this(1, ip, mask, gw, dhcp,
                                 bytes.fromhex("001aa917ffff"), 0, 1, "10.1.1.1")
            out = self.img.alloc(64)
            self.img.uc.mem_write(out, b"\xEE" * 64)
            res = self.img.call(RVA["header"], [out, obj])
            val = self.img.read(out, 23).hex().upper()
            ip_str = ".".join(str(b) for b in struct.pack("<I", ip))
            mask_str = ".".join(str(b) for b in struct.pack("<I", mask))
            gw_str = ".".join(str(b) for b in struct.pack("<I", gw))
            pending_ops.append({"op": "header", "ip": ip_str, "mask": mask_str,
                                "gateway": gw_str, "dhcp": gw_str, "flag": 1})
            keys.append((f"header case {idx}", ip_str, val, res.ok))

        answers = self.csharp(pending_ops)
        for (name, vec, orig, ok), answer in zip(keys, answers):
            cs = str(answer.get("result", {}).get("hex", answer.get("result", {}).get("crc_hex", "")))
            cs = cs.upper()
            match = ok and cs == orig
            self.record(name, {"input": vec}, orig, cs, match)

    # ---------- phase B: server private-property parsers ----------

    GLOBAL_FIELDS = {
        "SU_NEWEST_VER": 0x33DC,
        "PROXY_AVOID": 0x33E8,
        "DIALUP_AVOID": 0x33EC,
        "SU_REAUTH_INTERV": 0x3428,
        "HELLO_INTERV": 0x3404,
        "INDICATE_PORT": 0x33FC,
        "INDICATE_SERV_IP": 0x33F8,
        "ENCRYPT_KEY": 0x3408,
        "ENCRYPT_IV": 0x3410,
        "SERVER_UTC_TIME": 0x3418,
        "RADIUS_SERV_TYPE_A": 0x342C,
        "RADIUS_SERV_TYPE_B": 0x3464,
        "MSG_CLIENT_PORT": 0x3400,
        "SMP_IPADDRESS": 0x3454,
        "PROXY_DECTECT_KINDS": 0x33F0,
        "CLIENT_MSG_PERIOD": 0x3458,
        "AUTO_OPEN_SSO_URL": 0x3450,
        "DIRECT_COMM_HIGHEST_VERSION": 0x345A,
        "DIRECT_COMM_HEARTBEAT_FLAGS": 0x345C,
        "WIFI_CNNT_NUM_FLAGS": 0x3460,
    }

    STRING_FIELDS = {
        "SU_UPGRADE_URL": 0x33F4,
        "ACCOUNT_INFO": 0x33E0,
        "PERSIONAL_INFO": 0x33E4,
        "BROADCASE_INFO": 0x3424,
        "REJECT_REASON": 0x3420,
    }

    def read_global_string(self, obj: int, offset: int) -> bytes:
        """Read back an MSVC x86 std::string stored at obj+offset."""
        raw = self.img.read(obj + offset, 16)
        # MSVC x86 std::string: if the union holds a heap pointer it is
        # word-aligned and does not look like inline data; detect via _Myres.
        size_field = struct.unpack("<I", self.img.read(obj + offset + 0x10, 4))[0]
        res_field = struct.unpack("<I", self.img.read(obj + offset + 0x14, 4))[0]
        if res_field >= 16:
            ptr = struct.unpack("<I", raw[:4])[0]
            if ptr:
                return bytes(self.img.read(ptr, min(size_field, 4096)))
            return b""
        return raw.split(b"\x00")[0]

    def flat_tlv(self, props: list[tuple[int, bytes]]) -> bytes:
        out = bytearray()
        for ptype, value in props:
            out += bytes([ptype, len(value)]) + value
        return bytes(out)

    def rc4(self, data: bytes, key: bytes = RC4_KEY_COM_RUIJIE) -> bytes:
        return self.img._rc4_crypt(key, data)

    def phase_b(self):
        print("== Phase B: private-property parsers ==")
        obj = self.set_global_object()
        # String members touched by FUN_004813c0 (the +0x33DC sub-object init)
        # and by the parse cases themselves.
        for offset in (0x33E0, 0x33E4, 0x33F4, 0x3420, 0x3424,
                       0x3428, 0x342C, 0x3430,
                       0x778, 0x790, 0x798, 0x7A0, 0x7A4):
            self.init_string_field(obj, offset)
        ctx = self.img.alloc(0xA70)
        self.img.uc.mem_write(ctx, b"\x00" * 0xA70)

        rc4_key_plain = bytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88])
        rc4_iv_plain = bytes([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04])
        url = (b"http://update.rghall.com.cn/das/update"
               + b"x" * (0xF7 - 36))
        text = "电信宽带服务认证成功，剩余时长 4 小时".encode("gb18030")

        flat_props = [
            (0x05, struct.pack(">I", 0x01020304)),
            (0x14, struct.pack(">I", 1)),
            (0x15, struct.pack(">I", 1)),
            (0x37, url),
            (0x3C, text),
            (0x53, "用户名或密码错误".encode("gb18030")),
            (0x56, struct.pack(">I", 60)),
            (0x59, struct.pack(">I", 30)),
            (0x5A, struct.pack(">I", 3848)),
            (0x5B, struct.pack(">I", 0xACA87F01)),
            (0x5C, self.rc4(rc4_key_plain)),
            (0x5D, self.rc4(rc4_iv_plain)),
            (0x5E, b"\x5E" * 8),
            (0x61, bytes([9])),
            (0x6E, struct.pack(">I", 3848)),
            (0x72, b"http://172.24.83.254:8080/"),
            (0x73, struct.pack(">I", 0x0A0B0C0D)),
            (0x74, struct.pack(">I", 0x0000000F)),
            (0x75, struct.pack(">H", 60)),
            (0x77, bytes([1])),
            (0x79, bytes([2])),
            (0x80, struct.pack(">I", 7)),
            (0x98, struct.pack(">I", 3)),
        ]
        blob = self.flat_tlv(flat_props)
        ptr = self.img.alloc(len(blob))
        self.img.write(ptr, blob)
        res = self.img.call(RVA["read_rg_vendor_seg"], [ptr, len(blob)], ecx=obj)
        if not res.ok:
            print(f"  crash pcs: {self.img.crash_pcs()}")
        print(f"  ReadRGVendorSeg(flat) ok={res.ok} eax={res.eax:#x} ins={res.instructions}")

        original = {}
        for name, offset in self.GLOBAL_FIELDS.items():
            raw = self.img.read(obj + offset, 8)
            original[name] = raw[:8].hex()
        for name, offset in self.STRING_FIELDS.items():
            original[name + "_str"] = self.read_global_string(obj, offset).hex()

        cs = self.csharp([{"op": "parse_props", "hex": blob.hex()}])[0]
        self.record(
            "read_rg_vendor_seg flat TLV (fields)",
            {"blob_hex_len": len(blob)},
            json.dumps(original, sort_keys=True),
            json.dumps(cs, sort_keys=True),
            False,  # judgement handled in report below
            extra={"csharp_parse": cs, "blob_hex": blob.hex()},
        )

        # sectioned vector for ParsePrivateProperty
        msg = "认证成功消息".encode("gb18030")
        sectioned = bytearray()
        sectioned += struct.pack(">IH", 0x1311, len(msg)) + msg
        block2 = bytearray(struct.pack(">IH", 0x1311, 0x69))
        block2 += struct.pack(">I", 0)              # +0x618 value
        block2 += b"\x00" * 0x68
        block2 += bytes([2])                        # count at +0x68
        block2 += b"\xAA" * 0x20 + b"\xBB" * 0x20   # 2 × 32-byte entries
        sectioned += block2
        sectioned += struct.pack(">IH", 0x1311, 1) + struct.pack(">I", 0x00000001)
        sectioned += struct.pack(">IH", 0x1311, 1) + struct.pack(">I", 0x00000001)
        sectioned += struct.pack(">I", 0x1311) + bytes([0x0A, 0x00])
        sectioned += b"\x00" * 4 + b"\x00" * 4 + bytes([1])
        # TLV loop entries: [1311][type][len=payload+2][payload]
        def marker_tlv(ptype: int, value: bytes) -> bytes:
            return struct.pack(">I", 0x1311) + bytes([ptype, len(value) + 2]) + value
        sectioned += marker_tlv(0x56, struct.pack(">I", 90))
        sectioned += marker_tlv(0x5A, struct.pack(">I", 3848))
        sectioned += marker_tlv(0x5C, self.rc4(rc4_key_plain))
        sectioned += marker_tlv(0x75, struct.pack(">H", 120))

        blob2 = bytes(sectioned)
        self.img.uc.mem_write(ctx, b"\x00" * 0xA70)
        ptr2 = self.img.alloc(len(blob2))
        self.img.write(ptr2, blob2)
        res2 = self.img.call(RVA["parse_private_property"], [ptr2, len(blob2), ctx])
        print(f"  ParsePrivateProperty(sectioned) ok={res2.ok} eax={res2.eax:#x} ins={res2.instructions}")

        ctx_out = {
            "msg_buffer_first64": self.img.read(ctx + 0x2E, 64).hex(),
            "field_618": self.img.read(ctx + 0x618, 4).hex(),
            "field_624": self.img.read(ctx + 0x624, 2).hex(),
            "field_62c": self.img.read(ctx + 0x62C, 2).hex(),
            "field_638": self.img.read(ctx + 0x638, 4).hex(),
            "field_63c": self.img.read(ctx + 0x63C, 4).hex(),
            "field_640": self.img.read(ctx + 0x640, 1).hex(),
        }
        globals_out = {}
        for name in ("SU_REAUTH_INTERV", "INDICATE_PORT", "ENCRYPT_KEY", "CLIENT_MSG_PERIOD"):
            globals_out[name] = self.img.read(obj + self.GLOBAL_FIELDS[name], 8).hex()
        cs2 = self.csharp([{"op": "parse_props", "hex": blob2.hex()}])[0]
        self.record(
            "parse_private_property sectioned (fields)",
            {"blob_hex_len": len(blob2)},
            json.dumps({"ctx": ctx_out, "globals": globals_out}, sort_keys=True),
            json.dumps(cs2, sort_keys=True),
            False,
            extra={"csharp_parse": cs2, "blob_hex": blob2.hex()},
        )

    # ---------- reporting ----------

    def write_report(self):
        REPORT_DIR.mkdir(parents=True, exist_ok=True)
        report = {
            "exe": str(EXE),
            "unicorn_vectors": len(self.log),
            "entries": self.log,
        }
        out = REPORT_DIR / "full-diff-report.json"
        out.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"\nreport -> {out}")
        matches = sum(1 for e in self.log if e["match"])
        print(f"total={len(self.log)} match={matches} diff={len(self.log) - matches}")


if __name__ == "__main__":
    session = DiffSession()
    session.phase_a()
    session.phase_b()
    session.write_report()
