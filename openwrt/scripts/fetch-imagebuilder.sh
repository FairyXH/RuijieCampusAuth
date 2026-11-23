#!/bin/sh
set -eu

# Fetch and verify one OpenWrt Image Builder.
#
#   sh scripts/fetch-imagebuilder.sh [target] [subtarget] [dest]
#
# The version is pinned so a fresh clone produces the same rootfs, package
# set, and kernel ABI. Only ramips/mt7621 has a pinned SHA256 here; other
# targets are downloaded from the official release and should be verified
# against the target's sha256sums before publishing.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
TARGET=${1:-ramips/mt7621}
SUBTARGET=${2:-mt7621}
DEST=${3:-"$ROOT/build/ib"}
VERSION=${VERSION:-24.10.0}
TARGET_DASH=$(echo "$TARGET-$SUBTARGET" | tr '/' '-')
NAME="openwrt-imagebuilder-${VERSION}-${TARGET_DASH}.Linux-x86_64"
TARBALL="$NAME.tar.zst"
BASE=${BASE:-"https://downloads.openwrt.org/releases/${VERSION}/targets/${TARGET}/${SUBTARGET}"}
SHA256=${SHA256:-}
if [ "$TARGET/$SUBTARGET" = "ramips/mt7621" ]; then
	SHA256=${SHA256:-37b9bff3d501a79f63f146c6cecbd8223d48559a8b776e5ceb2672493f571b3e}
fi

mkdir -p "$DEST"
if [ ! -f "$DEST/$TARBALL" ]; then
	curl -fL --retry 4 --retry-all-errors -o "$DEST/$TARBALL" "$BASE/$TARBALL"
fi
if [ -n "$SHA256" ]; then
	printf '%s  %s\n' "$SHA256" "$DEST/$TARBALL" | sha256sum -c -
else
	echo "warning: no pinned SHA256 for $TARGET/$SUBTARGET" >&2
fi

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
