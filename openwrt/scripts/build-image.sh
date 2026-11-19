#!/bin/sh
set -eu

# Assemble a firmware image that already contains the authenticator ipk.
#
# The default package set stays deliberately small and kmod-free so the same
# build runs on any device with the same architecture: the daemon only needs
# AF_PACKET and the Go runtime. Add board-specific kmods via PACKAGES=... if
# you want them, but the auth plugin itself never requires any.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
IB=${IB_DIR:-/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64}
PROFILE=${PROFILE:-xiaomi_mi-router-3g}
JOBS=${JOBS:-4}
VERSION=${VERSION:-0.2.0}

DEFAULT_PACKAGES="luci-ssl luci-i18n-base-zh-cn"
PACKAGES=${PACKAGES:-$DEFAULT_PACKAGES}

if [ ! -d "$IB" ]; then
	echo "Image Builder not found: $IB" >&2
	exit 2
fi

VERSION="$VERSION" sh "$ROOT/scripts/build-ipk.sh"
APP_IPK="$OUT/ipk/ruijie-auth_${VERSION}_mipsel_24kc.ipk"

STAGE="$OUT/files"
rm -rf "$STAGE"
mkdir -p "$STAGE"

cd "$IB"
PATH=/opt/openwrt/gnu-bin:$PATH make image \
	PROFILE="$PROFILE" \
	PACKAGES="$PACKAGES $APP_IPK" \
	FILES="$STAGE" \
	-j"$JOBS"

echo
echo "Image output: $IB/bin/targets/ramips/mt7621/"
