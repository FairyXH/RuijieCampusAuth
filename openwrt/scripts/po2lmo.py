#!/usr/bin/env python3
"""Compile a LuCI .po catalog into the .lmo format LuCI loads.

LuCI has no build-time dependency here other than this script: the dispatcher
merges every /usr/lib/lua/luci/i18n/<domain>.<lang>.lmo for the selected
language, so the router only needs the compiled file.

Layout (little tooling, big-endian on the wire):

    [string table: UTF-8 values, NUL-padded to 4-byte alignment]
    [index: N * 16 bytes, sorted by key_id]
        uint32 key_id    hash of msgid (+ context/plural suffix)
        uint32 val_id    plural count + 1
        uint32 offset    byte offset of the value inside the string table
        uint32 length    value length in bytes, excluding padding
    [uint32 index_offset]

Hashing is LuCI's sfh_hash(data, len, len).
"""

import struct
import sys

MASK = 0xFFFFFFFF


def sfh_get16(data: bytes, offset: int) -> int:
    return data[offset] | (data[offset + 1] << 8)


def sfh_hash(data: bytes) -> int:
    """Paul Hsieh's SuperFastHash as shipped in LuCI lib/lmo.c.

    The caller passes len as the initial value, which is also the length.
    """
    length = len(data)
    if length <= 0:
        return 0
    remaining = length & 3
    blocks = length >> 2
    value = length & MASK
    offset = 0
    for _ in range(blocks):
        value = (value + sfh_get16(data, offset)) & MASK
        tmp = ((sfh_get16(data, offset + 2) << 11) ^ value) & MASK
        value = ((value << 16) ^ tmp) & MASK
        offset += 4
        value = (value + (value >> 11)) & MASK
    if remaining == 3:
        value = (value + sfh_get16(data, offset)) & MASK
        value ^= (value << 16) & MASK
        signed = data[offset + 2]
        if signed > 127:
            signed -= 256
        value ^= (signed << 18) & MASK
        value = (value + (value >> 11)) & MASK
    elif remaining == 2:
        value = (value + sfh_get16(data, offset)) & MASK
        value ^= (value << 11) & MASK
        value = (value + (value >> 17)) & MASK
    elif remaining == 1:
        signed = data[offset]
        if signed > 127:
            signed -= 256
        value = (value + signed) & MASK
        value ^= (value << 10) & MASK
        value = (value + (value >> 1)) & MASK
    value ^= (value << 3) & MASK
    value = (value + (value >> 5)) & MASK
    value ^= (value << 4) & MASK
    value = (value + (value >> 17)) & MASK
    value ^= (value << 25) & MASK
    value = (value + (value >> 6)) & MASK
    return value


def canonical(text: str) -> str:
    """Whitespace normalisation performed by lmo_canon_hash()."""
    out = []
    previous_space = True
    for char in text:
        if char.isspace():
            if not previous_space:
                out.append(" ")
            previous_space = True
        else:
            out.append(char)
            previous_space = False
    if out and out[-1] == " ":
        out.pop()
    return "".join(out)


def unquote(text: str) -> str:
    text = text.strip()
    if len(text) >= 2 and text[0] == '"' and text[-1] == '"':
        body = text[1:-1]
        out = []
        escaped = False
        for char in body:
            if escaped:
                out.append({"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\"}.get(char, char))
                escaped = False
            elif char == "\\":
                escaped = True
            else:
                out.append(char)
        return "".join(out)
    return text


class Entry:
    def __init__(self):
        self.context = None
        self.msgid = None
        self.msgid_plural = None
        self.msgstr = {}
        self.fuzzy = False


def parse_po(path: str):
    entries = []
    current = Entry()
    field = None
    index = None

    def flush():
        nonlocal current
        if current.msgid is not None or current.msgstr:
            entries.append(current)
        current = Entry()

    with open(path, "r", encoding="utf-8") as handle:
        for raw in handle:
            line = raw.rstrip("\n")
            stripped = line.strip()
            if not stripped:
                continue
            if stripped.startswith("#~"):
                continue
            if stripped.startswith("#"):
                if stripped.startswith("#, ") and "fuzzy" in stripped:
                    current.fuzzy = True
                continue
            if stripped.startswith("msgctxt "):
                if current.msgid is not None:
                    flush()
                current.context = unquote(stripped[len("msgctxt "):])
                field = ("context", None)
            elif stripped.startswith("msgid_plural "):
                current.msgid_plural = unquote(stripped[len("msgid_plural "):])
                field = ("plural", None)
            elif stripped.startswith("msgid "):
                if current.msgid is not None:
                    flush()
                current.msgid = unquote(stripped[len("msgid "):])
                field = ("id", None)
            elif stripped.startswith("msgstr["):
                close = stripped.index("]")
                index = int(stripped[8:close])
                value = unquote(stripped[close + 2:])
                current.msgstr[index] = current.msgstr.get(index, "") + value
                field = ("str", index)
            elif stripped.startswith("msgstr "):
                value = unquote(stripped[len("msgstr "):])
                current.msgstr[0] = current.msgstr.get(0, "") + value
                field = ("str", 0)
            elif stripped.startswith('"'):
                value = unquote(stripped)
                if field is None:
                    continue
                kind, slot = field
                if kind == "context":
                    current.context = (current.context or "") + value
                elif kind == "id":
                    current.msgid = (current.msgid or "") + value
                elif kind == "plural":
                    current.msgid_plural = (current.msgid_plural or "") + value
                elif kind == "str":
                    current.msgstr[slot] = current.msgstr.get(slot, "") + value
    flush()
    return entries


def plural_forms(header: str) -> str:
    for line in header.split("\n"):
        if line.lower().startswith("plural-forms:"):
            return line[len("plural-forms:"):].strip()
    return "nplurals=1; plural=0;"


def compile_po(path: str) -> bytes:
    entries = parse_po(path)
    strings = bytearray()
    index = []

    def add(key_id: int, val_id: int, value: str):
        encoded = value.encode("utf-8")
        offset = len(strings)
        strings.extend(encoded)
        pad = (4 - (len(encoded) % 4)) % 4
        strings.extend(b"\0" * pad)
        index.append((key_id, val_id, offset, len(encoded)))

    for entry in entries:
        if entry.fuzzy:
            continue
        if entry.msgid == "":
            if entry.msgstr.get(0):
                add(0, 0, plural_forms(entry.msgstr[0]))
            continue
        if entry.msgid is None:
            continue
        plural = entry.msgid_plural is not None
        forms = list(entry.msgstr.keys()) or [0]
        plural_num = max(forms) if plural else 0
        for i in range(0, plural_num + 1):
            value = entry.msgstr.get(i, "")
            if not value:
                continue
            if entry.context:
                key = canonical(entry.context) + "\x01" + canonical(entry.msgid)
            else:
                key = canonical(entry.msgid)
            if plural:
                key = key + "\x02" + str(i)
            key_bytes = key.encode("utf-8")
            key_id = sfh_hash(key_bytes)
            val_id = sfh_hash(value.encode("utf-8"))
            if key_id == val_id:
                continue
            add(key_id, plural_num + 1, value)

    index.sort(key=lambda item: item[0])
    index_offset = len(strings)
    table = bytearray()
    for key_id, val_id, offset, length in index:
        table.extend(struct.pack(">IIII", key_id, val_id, offset, length))
    table.extend(struct.pack(">I", index_offset))
    return bytes(strings) + bytes(table)


def main(argv):
    if len(argv) != 3:
        print("usage: po2lmo.py <input.po> <output.lmo>", file=sys.stderr)
        return 2
    data = compile_po(argv[1])
    with open(argv[2], "wb") as handle:
        handle.write(data)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
