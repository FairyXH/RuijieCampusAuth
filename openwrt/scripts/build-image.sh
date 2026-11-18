#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
IB=${IB_DIR:-/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64}
PROFILE=${PROFILE:-xiaomi_mi-router-3g}
JOBS=${JOBS:-4}

UA3F_VERSION=3.7.0
UA3F_SHA256=a1d710233ab69af2e127193d7de81d7fd7c400294dc2147368694f2fd56e073f
UA3F_URL="https://github.com/SunBK201/UA3F/releases/download/v${UA3F_VERSION}/ua3f_${UA3F_VERSION}-r1_mipsel_24kc.ipk"
UA3F_DIR="$OUT/ua3f"
UA3F_IPK="$UA3F_DIR/ua3f.ipk"

DEFAULT_PACKAGES="luci-ssl luci-i18n-base-zh-cn https-dns-proxy luci-app-https-dns-proxy luci-i18n-https-dns-proxy-zh-cn kmod-nft-queue kmod-nft-tproxy kmod-nfnetlink-queue kmod-nf-conntrack-netlink iptables iptables-mod-tproxy iptables-mod-extra iptables-mod-ipopt iptables-mod-nfqueue iptables-mod-conntrack-extra ipset luci-compat"
PACKAGES=${PACKAGES:-$DEFAULT_PACKAGES}

if [ ! -d "$IB" ]; then
	echo "Image Builder not found: $IB" >&2
	exit 2
fi

sh "$ROOT/scripts/build-go.sh"

mkdir -p "$UA3F_DIR"
if [ ! -f "$UA3F_IPK" ]; then
	curl -fL --retry 4 --retry-all-errors -o "$UA3F_IPK" "$UA3F_URL" || \
		curl -fL --retry 4 --retry-all-errors -o "$UA3F_IPK" "https://ghproxy.net/$UA3F_URL"
fi
printf '%s  %s\n' "$UA3F_SHA256" "$UA3F_IPK" | sha256sum -c -

rm -rf "$UA3F_DIR/extract" "$UA3F_DIR/root"
mkdir -p "$UA3F_DIR/extract" "$UA3F_DIR/root"
tar -xzf "$UA3F_IPK" -C "$UA3F_DIR/extract"
tar -xzf "$UA3F_DIR/extract/data.tar.gz" -C "$UA3F_DIR/root"
if [ ! -x "$UA3F_DIR/root/usr/bin/ua3f" ] || [ ! -f "$UA3F_DIR/root/etc/init.d/ua3f" ]; then
	echo "UA3F package layout is unexpected" >&2
	exit 2
fi

STAGE="$OUT/files"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -a "$ROOT/files/." "$STAGE/"
mkdir -p "$STAGE/usr/sbin"
cp "$OUT/hxxy-auth" "$STAGE/usr/sbin/hxxy-auth"
mkdir -p "$STAGE/usr/bin" "$STAGE/etc/init.d"
cp "$UA3F_DIR/root/usr/bin/ua3f" "$STAGE/usr/bin/ua3f"
cp "$UA3F_DIR/root/etc/init.d/ua3f" "$STAGE/etc/init.d/ua3f"

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
chmod 0755 "$STAGE/usr/sbin/hxxy-auth" "$STAGE/usr/bin/ua3f"
chmod 0755 "$STAGE/etc/init.d/hxxy-auth" "$STAGE/etc/init.d/hxxy-disguise"
chmod 0755 "$STAGE/etc/init.d/ua3f" "$STAGE/etc/hotplug.d/iface/90-hxxy-auth"
chmod 0755 "$STAGE/etc/uci-defaults/"*

cd "$IB"
PATH=/opt/openwrt/gnu-bin:$PATH make image \
	PROFILE="$PROFILE" \
	PACKAGES="$PACKAGES" \
	FILES="$STAGE" \
	-j"$JOBS"

echo
echo "Image output: $IB/bin/targets/ramips/mt7621/"
