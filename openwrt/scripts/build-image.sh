#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
IB=${IB_DIR:-/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64}
PROFILE=${PROFILE:-xiaomi_mi-router-3g}
PACKAGES=${PACKAGES:-luci-ssl}
JOBS=${JOBS:-4}

if [ ! -d "$IB" ]; then
	echo "Image Builder not found: $IB" >&2
	exit 2
fi

sh "$ROOT/scripts/build-go.sh"

STAGE="$OUT/files"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -a "$ROOT/files/." "$STAGE/"
mkdir -p "$STAGE/usr/sbin"
cp "$OUT/hxxy-auth" "$STAGE/usr/sbin/hxxy-auth"

mkdir -p "$STAGE/www/luci-static/resources/view/hxxy-auth"
cp "$ROOT/luci-app-hxxy-auth/htdocs/luci-static/resources/view/hxxy-auth/"*.js \
	"$STAGE/www/luci-static/resources/view/hxxy-auth/"

mkdir -p "$STAGE/usr/share/luci/menu.d" "$STAGE/usr/share/rpcd/acl.d"
cp "$ROOT/luci-app-hxxy-auth/root/usr/share/luci/menu.d/"*.json \
	"$STAGE/usr/share/luci/menu.d/"
cp "$ROOT/luci-app-hxxy-auth/root/usr/share/rpcd/acl.d/"*.json \
	"$STAGE/usr/share/rpcd/acl.d/"

mkdir -p "$STAGE/etc/uci-defaults"
cp "$ROOT/luci-app-hxxy-auth/root/etc/uci-defaults/"* "$STAGE/etc/uci-defaults/"
chmod 0755 "$STAGE/etc/uci-defaults/"*
chmod 0755 "$STAGE/etc/init.d/hxxy-auth" "$STAGE/etc/hotplug.d/iface/90-hxxy-auth"

cd "$IB"
PATH=/opt/openwrt/gnu-bin:$PATH make image \
	PROFILE="$PROFILE" \
	PACKAGES="$PACKAGES" \
	FILES="$STAGE" \
	-j"$JOBS"

echo
echo "Image output: $IB/bin/targets/ramips/mt7621/"
