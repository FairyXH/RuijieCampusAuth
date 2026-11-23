#!/bin/sh
set -eu

# Build one OpenWrt profile from a profile env file.
#
#   sh scripts/build-target.sh scripts/profiles/xiaomi_mi-router-4a-gigabit.env
#
# The env file supplies TARGET/SUBTARGET/PROFILE/ARCH/GOARCH/TIER/UA.
# Package tiers live in scripts/packages/<tier>.txt; the selected UA ipk
# and the freshly built ruijie-auth ipk are appended automatically.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
PROFILE_ENV=${1:?usage: build-target.sh <profile.env>}
[ -f "$PROFILE_ENV" ] || PROFILE_ENV="$ROOT/$PROFILE_ENV"
[ -f "$PROFILE_ENV" ] || { echo "profile env not found: $1" >&2; exit 2; }
. "$PROFILE_ENV"

: "${TARGET:?TARGET missing}"
: "${SUBTARGET:?SUBTARGET missing}"
: "${PROFILE:?PROFILE missing}"
: "${ARCH:?ARCH missing}"
: "${GOARCH:?GOARCH missing}"
TIER=${TIER:-lite}
UA=${UA:-ua2f}
VERSION=${VERSION:-0.11.4}
OWRT_VERSION=${OWRT_VERSION:-24.10.0}
JOBS=${JOBS:-$(nproc 2>/dev/null || echo 4)}
OUT=${OUT_DIR:-"$ROOT/build"}

TARGET_DASH=$(echo "$TARGET-$SUBTARGET" | tr '/' '-')
IB_NAME="openwrt-imagebuilder-${OWRT_VERSION}-${TARGET_DASH}.Linux-x86_64"
IB=${IB_DIR:-"$OUT/ib/$IB_NAME"}
if [ ! -d "$IB" ] && [ -d "/opt/openwrt/$IB_NAME" ]; then
	IB="/opt/openwrt/$IB_NAME"
fi
if [ ! -d "$IB" ]; then
	IB=$(sh "$ROOT/scripts/fetch-imagebuilder.sh" "$TARGET" "$SUBTARGET" "$OUT/ib")
fi

# Image Builder's local package index can be emptied by `make clean`. Point
# its repositories at a reachable mirror; override with MIRROR=official.
MIRROR=${MIRROR:-https://mirrors.tuna.tsinghua.edu.cn/openwrt}
[ "$MIRROR" = "official" ] && MIRROR=https://downloads.openwrt.org
if [ -f "$IB/repositories.conf" ]; then
	sed -i \
		-e "s|https://downloads.openwrt.org|${MIRROR}|g" \
		-e "s|https://mirrors.tuna.tsinghua.edu.cn/openwrt|${MIRROR}|g" \
		"$IB/repositories.conf"
fi

echo "== target:   $TARGET/$SUBTARGET"
echo "== profile:  $PROFILE"
echo "== arch:     $ARCH ($GOARCH)"
echo "== tier:     $TIER"
echo "== ua:       $UA"
echo "== version:  $VERSION"
echo "== builder:  $IB"
echo "== mirror:   $MIRROR"

# Authenticator ipk for this architecture.
VERSION="$VERSION" GOARCH="$GOARCH" PKG_ARCH="$ARCH" sh "$ROOT/scripts/build-ipk.sh"
AUTH_IPK="$OUT/ipk/ruijie-auth_${VERSION}_${ARCH}.ipk"
[ -f "$AUTH_IPK" ] || { echo "missing $AUTH_IPK" >&2; exit 2; }

# UA3F/UA2F from the mirror (arch/package/version).
UA_IPK=
if [ "$UA" != "none" ]; then
	UA_VER=$(cat "$ROOT/ipk/$ARCH/$UA/latest.txt")
	UA_IPK="$ROOT/ipk/$ARCH/$UA/$UA_VER/${UA}_${UA_VER}_${ARCH}.ipk"
	[ -f "$UA_IPK" ] || { echo "missing $UA_IPK" >&2; exit 2; }
fi

PKG_FILE="$ROOT/scripts/packages/${TIER}.txt"
[ -f "$PKG_FILE" ] || { echo "missing $PKG_FILE" >&2; exit 2; }
PACKAGES=$(tr '\n' ' ' <"$PKG_FILE")
[ -n "$UA_IPK" ] && PACKAGES="$PACKAGES $UA_IPK"
PACKAGES="$PACKAGES $AUTH_IPK ${EXTRA_PACKAGES:-}"

# Files overlay: generic + board-specific.
STAGE="$OUT/stage/$PROFILE"
rm -rf "$STAGE"
mkdir -p "$STAGE"
# Curated generic overlay for matrix builds. Do not copy openwrt/files
# wholesale: it also contains R3G/full-image extras (mwan3, SQM, USB/IP,
# ua3f-scope, custom-packages).
for p in \
	etc/config/ruijie-auth \
	etc/init.d/ruijie-auth \
	etc/init.d/ruijie-disguise \
	etc/hotplug.d/iface/90-ruijie-auth \
	etc/hotplug.d/net/90-ruijie-usbwan \
	etc/sysctl.d/90-ruijie.conf \
	etc/uci-defaults/99-luci-cache-bust; do
	mkdir -p "$STAGE/$(dirname "$p")"
	cp -a "$ROOT/files/$p" "$STAGE/$p"
done
if [ -d "$ROOT/files/boards/common" ]; then
	cp -a "$ROOT/files/boards/common/." "$STAGE/"
fi
if [ -d "$ROOT/files/boards/$PROFILE" ]; then
	cp -a "$ROOT/files/boards/$PROFILE/." "$STAGE/"
fi
if [ -n "${EXTRA_FILES:-}" ]; then
	cp -a "$EXTRA_FILES/." "$STAGE/"
fi

cd "$IB"
mkdir -p "$IB/tmp"
PATH=/opt/openwrt/gnu-bin:$PATH make clean >/dev/null 2>&1 || true
mkdir -p "$IB/tmp"
PATH=/opt/openwrt/gnu-bin:$PATH make image \
	PROFILE="$PROFILE" \
	PACKAGES="$PACKAGES" \
	FILES="$STAGE" \
	-j"$JOBS"

DEST="$ROOT/firmware/$TARGET/$SUBTARGET/$PROFILE/$VERSION"
mkdir -p "$DEST"
SRC="$IB/bin/targets/$TARGET/$SUBTARGET"
for f in "$SRC"/*"$PROFILE"*.bin; do
	[ -f "$f" ] || continue
	cp -f "$f" "$DEST/"
done
MANIFEST="$SRC/openwrt-${OWRT_VERSION}-${TARGET_DASH}-${PROFILE}.manifest"
[ -f "$MANIFEST" ] || MANIFEST="$SRC/openwrt-${OWRT_VERSION}-${TARGET_DASH}.manifest"
[ -f "$MANIFEST" ] && cp -f "$MANIFEST" "$DEST/openwrt-${OWRT_VERSION}-${TARGET_DASH}-${PROFILE}.manifest"

(
	cd "$DEST"
	sha256sum ./*.bin ./*.manifest >SHA256SUMS 2>/dev/null || true
)

MF="$DEST/openwrt-${OWRT_VERSION}-${TARGET_DASH}-${PROFILE}.manifest"
if [ -f "$MF" ]; then
	PKG_COUNT=$(wc -l <"$MF")
else
	PKG_COUNT=?
fi

{
	echo "版本: $VERSION"
	echo "构建时间: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
	echo "OpenWrt: $OWRT_VERSION"
	echo "Image Builder: $IB_NAME"
	echo "Target: $TARGET/$SUBTARGET"
	echo "Profile: $PROFILE"
	echo "架构: $ARCH"
	echo "Go: $GOARCH"
	echo "层: $TIER"
	echo "UA: $UA"
	echo "包数: $PKG_COUNT"
	echo
	echo "镜像:"
	ls -l "$DEST"/*.bin 2>/dev/null || true
} >"$DEST/BUILD_INFO.txt"

mkdir -p "$ROOT/firmware/$TARGET/$SUBTARGET/$PROFILE"
printf '%s\n' "$VERSION" >"$ROOT/firmware/$TARGET/$SUBTARGET/$PROFILE/latest.txt"

echo
echo "== output: $DEST"
ls -lh "$DEST"
