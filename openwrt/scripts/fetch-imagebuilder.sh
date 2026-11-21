#!/bin/sh
set -eu

# Fetch and verify the exact OpenWrt Image Builder used for this firmware.
# The version and checksum are pinned so a fresh clone produces the same
# rootfs, package set, and kernel ABI.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
DEST=${1:-"$ROOT/build/ib"}
VERSION=24.10.0
NAME="openwrt-imagebuilder-${VERSION}-ramips-mt7621.Linux-x86_64"
TARBALL="$NAME.tar.zst"
SHA256=37b9bff3d501a79f63f146c6cecbd8223d48559a8b776e5ceb2672493f571b3e
BASE="https://downloads.openwrt.org/releases/${VERSION}/targets/ramips/mt7621"

mkdir -p "$DEST"
if [ ! -f "$DEST/$TARBALL" ]; then
	curl -fL --retry 4 --retry-all-errors -o "$DEST/$TARBALL" "$BASE/$TARBALL"
fi
printf '%s  %s\n' "$SHA256" "$DEST/$TARBALL" | sha256sum -c -

if [ ! -d "$DEST/$NAME" ]; then
	if tar --zstd -xf "$DEST/$TARBALL" -C "$DEST" 2>/dev/null; then
		:
	else
		command -v zstd >/dev/null 2>&1 || {
			echo "zstd is required to extract $TARBALL" >&2
			exit 2
		}
		zstd -dc "$DEST/$TARBALL" | tar -xf - -C "$DEST"
	fi
fi

echo "$DEST/$NAME"
