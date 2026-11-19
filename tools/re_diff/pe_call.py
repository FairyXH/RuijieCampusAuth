#!/usr/bin/env python3
"""Call internal functions of a 32-bit PE by RVA inside Unicorn.

The original Ruijie engine exports no symbols. This harness maps the whole
image, stubs the import table and calls a function address directly. It is
meant for pure algorithm functions (crypto/CRC/obfuscation), not UI code.
"""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass
from typing import Iterable

import pefile
from unicorn import (
    Uc,
    UC_ARCH_X86,
    UC_HOOK_CODE,
    UC_MODE_32,
    UcError,
)
import unicorn.x86_const as xc

STACK_BASE = 0x20000000
STACK_SIZE = 0x100000
STUB_BASE = 0x70000000
STUB_SIZE = 0x10000
HEAP_BASE = 0x30000000
HEAP_SIZE = 0x400000
SENTINEL = 0x50000000


@dataclass
class CallResult:
    ok: bool
    eax: int
    error: str | None
    instructions: int


class PeCall:
    def __init__(self, path: str):
        self.pe = pefile.PE(path, fast_load=False)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.image = self.pe.get_memory_mapped_image()
        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        self.heap = HEAP_BASE
        self._stub_index = 0
        self._imports: dict[int, str] = {}
        self._instructions = 0
        self._map_image()
        self._map_imports()
        self._map_stack()

    def _align(self, value: int, page: int = 0x1000) -> int:
        return (value + page - 1) & ~(page - 1)

    def _map_image(self) -> None:
        size = self._align(self.pe.OPTIONAL_HEADER.SizeOfImage)
        self.uc.mem_map(self.base, size)
        self.uc.mem_write(self.base, self.image)

    def _map_stack(self) -> None:
        # FS:[0] is read/written by the MSVC SEH prologue. Unicorn's 32-bit
        # x86 has no settable FS base, so page zero doubles as FS:[0].
        self.uc.mem_map(0, 0x1000)
        self.uc.mem_write(0, b"\xff\xff\xff\xff")
        self.uc.mem_map(STACK_BASE, STACK_SIZE)
        self.uc.mem_map(HEAP_BASE, HEAP_SIZE)
        self.uc.mem_map(SENTINEL, 0x1000)
        self.uc.mem_write(SENTINEL, b"\xc3")

    def _map_imports(self) -> None:
        self.uc.mem_map(STUB_BASE, STUB_SIZE)
        for entry in getattr(self.pe, "DIRECTORY_ENTRY_IMPORT", []):
            for imp in entry.imports:
                if not imp.name:
                    continue
                stub = STUB_BASE + self._stub_index * 0x10
                self._stub_index += 1
                if self._stub_index * 0x10 >= STUB_SIZE:
                    raise RuntimeError("import stub region exhausted")
                # Each stub is a single `ret` (0xC3). A code hook implements it.
                self.uc.mem_write(stub, b"\xc3")
                self._imports[stub] = imp.name.decode("ascii", "replace")
                if imp.address:
                    self.uc.mem_write(
                        imp.address,
                        struct.pack("<I", stub),
                    )
        self.uc.hook_add(UC_HOOK_CODE, self._hook_stub, begin=STUB_BASE,
                         end=STUB_BASE + STUB_SIZE - 1)
        self.uc.hook_add(UC_HOOK_CODE, self._count)

    def _count(self, uc, address, size, user_data) -> None:
        self._instructions += 1

    def _return(self, uc) -> None:
        esp = uc.reg_read(xc.UC_X86_REG_ESP)
        ret = struct.unpack("<I", uc.mem_read(esp, 4))[0]
        uc.reg_write(xc.UC_X86_REG_ESP, esp + 4)
        uc.reg_write(xc.UC_X86_REG_EIP, ret)

    def _args(self, uc, count: int) -> list[int]:
        esp = uc.reg_read(xc.UC_X86_REG_ESP)
        data = uc.mem_read(esp + 4, 4 * count)
        return list(struct.unpack("<" + "I" * count, data))

    def _hook_stub(self, uc, address, size, user_data) -> None:
        name = self._imports.get(address, "")
        if name.startswith(("memcpy", "_memcpy")):
            dest, src, length = self._args(uc, 3)
            if length:
                uc.mem_write(dest, bytes(uc.mem_read(src, length)))
            uc.reg_write(xc.UC_X86_REG_EAX, dest)
            self._return(uc)
        elif name.startswith(("memset", "_memset")):
            dest, value, length = self._args(uc, 3)
            if length:
                uc.mem_write(dest, bytes([value & 0xFF]) * length)
            uc.reg_write(xc.UC_X86_REG_EAX, dest)
            self._return(uc)
        elif name.startswith(("memmove", "_memmove")):
            dest, src, length = self._args(uc, 3)
            if length:
                uc.mem_write(dest, bytes(uc.mem_read(src, length)))
            uc.reg_write(xc.UC_X86_REG_EAX, dest)
            self._return(uc)
        elif name.startswith(("strlen", "_strlen")):
            (src,) = self._args(uc, 1)
            data = bytes(uc.mem_read(src, 4096)).split(b"\x00", 1)[0]
            uc.reg_write(xc.UC_X86_REG_EAX, len(data))
            self._return(uc)
        elif name.startswith(("strcpy", "_strcpy")):
            dest, src = self._args(uc, 2)
            data = bytes(uc.mem_read(src, 4096)).split(b"\x00", 1)[0] + b"\x00"
            uc.mem_write(dest, data)
            uc.reg_write(xc.UC_X86_REG_EAX, dest)
            self._return(uc)
        elif name.startswith(("malloc", "_malloc")):
            (size,) = self._args(uc, 1)
            address = self.heap
            self.heap += self._align(size + 8, 16)
            uc.reg_write(xc.UC_X86_REG_EAX, address)
            self._return(uc)
        elif name.startswith(("calloc", "_calloc")):
            count, size = self._args(uc, 2)
            total = count * size
            address = self.heap
            self.heap += self._align(total + 8, 16)
            uc.mem_write(address, b"\x00" * total)
            uc.reg_write(xc.UC_X86_REG_EAX, address)
            self._return(uc)
        elif name.startswith(("free", "_free")):
            uc.reg_write(xc.UC_X86_REG_EAX, 0)
            self._return(uc)
        elif name.startswith(("__security_check_cookie",)):
            self._return(uc)
        else:
            # Unknown imports: return zero and let the caller continue.
            uc.reg_write(xc.UC_X86_REG_EAX, 0)
            self._return(uc)

    def write(self, address: int, data: bytes) -> None:
        self.uc.mem_write(address, data)

    def alloc(self, size: int) -> int:
        address = self.heap
        self.heap += self._align(size + 8, 16)
        return address

    def read(self, address: int, size: int) -> bytes:
        return bytes(self.uc.mem_read(address, size))

    def call(
        self,
        rva: int,
        args: Iterable[int] = (),
        ecx: int | None = None,
        max_instructions: int = 2_000_000,
    ) -> CallResult:
        self._instructions = 0
        argument_list = list(args)
        esp = STACK_BASE + STACK_SIZE - 0x1000 - 4 * len(argument_list) - 4
        self.uc.reg_write(xc.UC_X86_REG_ESP, esp)
        self.uc.mem_write(esp, struct.pack("<I", SENTINEL))
        for index, value in enumerate(argument_list):
            self.uc.mem_write(
                esp + 4 + index * 4,
                struct.pack("<I", value),
            )
        if ecx is not None:
            self.uc.reg_write(xc.UC_X86_REG_ECX, ecx)
        try:
            self.uc.emu_start(
                self.base + rva,
                SENTINEL,
                count=max_instructions,
            )
            return CallResult(
                True,
                self.uc.reg_read(xc.UC_X86_REG_EAX),
                None,
                self._instructions,
            )
        except UcError as error:
            return CallResult(
                False,
                self.uc.reg_read(xc.UC_X86_REG_EAX),
                str(error),
                self._instructions,
            )


if __name__ == "__main__":
    image = PeCall(sys.argv[1])
    result = image.call(int(sys.argv[2], 16))
    print(result)
