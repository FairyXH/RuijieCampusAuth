#!/bin/sh
set -eu

# Assemble the full R3G firmware image.
#
# The ruijie-auth ipk itself stays portable: AF_PACKET + Go stdlib, no kmod.
# This firmware image is the place for the board-level extras: UA3F (NFQUEUE
# L3 disguise), https-dns-proxy (DoH), the required kmods/iptables modules,
# and the Vantage LuCI theme.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
IB=${IB_DIR:-/opt/openwrt/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64}
PROFILE=${PROFILE:-xiaomi_mi-router-3g}
JOBS=${JOBS:-4}
VERSION=${VERSION:-0.9.0}

UA3F_VERSION=3.7.0
UA3F_SHA256=a1d710233ab69af2e127193d7de81d7fd7c400294dc2147368694f2fd56e073f
UA3F_URL="https://github.com/SunBK201/UA3F/releases/download/v${UA3F_VERSION}/ua3f_${UA3F_VERSION}-r1_mipsel_24kc.ipk"
UA3F_IPK=${UA3F_IPK:-/opt/openwrt/ua3f/ua3f.ipk}
UA3F_DIR="$OUT/ua3f"

THEME_VERSION=1.0.21
THEME_SHA256=fcb3c7cc9bea21fdb2a895155ef5eda65ac6ed797d966b7595c7b314159d1bda
THEME_I18N_SHA256=9aee7a13ec6c65036b99de5cc595975e90de1265ffaf12ca9b4523e13dc2fc42
VANTAGE_APP_SHA256=5befdc0313457394e600238a265da848d611a80ae1786d7433196d770c4717f1
VANTAGE_I18N_SHA256=d8767e292374d6c87620fe273930d96c8203e04ec7c7ae416bc840768be51537
THEME_BASE_URL="https://github.com/anothaDev/luci-theme-vantage/releases/download/v${THEME_VERSION}"
THEME_IPK=${THEME_IPK:-"$OUT/theme/luci-theme-vantage_${THEME_VERSION}-r1_all.ipk"}
THEME_I18N_IPK=${THEME_I18N_IPK:-"$OUT/theme/luci-i18n-vantage-theme-zh-cn_${THEME_VERSION}-r1_all.ipk"}
VANTAGE_APP_IPK=${VANTAGE_APP_IPK:-"$OUT/theme/luci-app-vantage_${THEME_VERSION}-r1_all.ipk"}
VANTAGE_I18N_IPK=${VANTAGE_I18N_IPK:-"$OUT/theme/luci-i18n-vantage-zh-cn_${THEME_VERSION}-r1_all.ipk"}

DEFAULT_PACKAGES="luci-ssl luci-i18n-base-zh-cn \
	https-dns-proxy luci-app-https-dns-proxy luci-i18n-https-dns-proxy-zh-cn \
	kmod-nft-queue kmod-nft-tproxy kmod-nfnetlink-queue kmod-nf-conntrack-netlink \
	iptables iptables-mod-tproxy iptables-mod-extra iptables-mod-ipopt \
	iptables-mod-nfqueue iptables-mod-conntrack-extra ipset luci-compat"
PACKAGES=${PACKAGES:-$DEFAULT_PACKAGES}

if [ ! -d "$IB" ]; then
	echo "Image Builder not found: $IB" >&2
	exit 2
fi

fetch() {
	local url=$1 file=$2 sum=$3
	mkdir -p "$(dirname "$file")"
	if [ ! -f "$file" ]; then
		curl -fL --retry 4 --retry-all-errors -o "$file" "$url" || \
			curl -fL --retry 4 --retry-all-errors -o "$file" "https://ghproxy.net/$url"
	fi
	printf '%s  %s\n' "$sum" "$file" | sha256sum -c -
}

extract_ipk() {
	local ipk=$1 dest=$2
	rm -rf "$dest" "$dest.extract"
	mkdir -p "$dest" "$dest.extract"
	tar -xzf "$ipk" -C "$dest.extract"
	tar -xzf "$dest.extract/data.tar.gz" -C "$dest"
}

VERSION="$VERSION" sh "$ROOT/scripts/build-ipk.sh"
APP_IPK="$OUT/ipk/ruijie-auth_${VERSION}_mipsel_24kc.ipk"

# UA3F: extract the binary; the init/config live in files/.
if [ ! -f "$UA3F_IPK" ]; then
	UA3F_IPK="$UA3F_DIR/ua3f.ipk"
	fetch "$UA3F_URL" "$UA3F_IPK" "$UA3F_SHA256"
else
	printf '%s  %s\n' "$UA3F_SHA256" "$UA3F_IPK" | sha256sum -c -
fi
extract_ipk "$UA3F_IPK" "$UA3F_DIR/root"
if [ ! -x "$UA3F_DIR/root/usr/bin/ua3f" ]; then
	echo "UA3F package layout is unexpected" >&2
	exit 2
fi

# Vantage theme + app + zh-cn catalogs, installed through the Image Builder.
fetch "$THEME_BASE_URL/luci-theme-vantage_${THEME_VERSION}-r1_all.ipk" "$THEME_IPK" "$THEME_SHA256"
fetch "$THEME_BASE_URL/luci-i18n-vantage-theme-zh-cn_${THEME_VERSION}-r1_all.ipk" "$THEME_I18N_IPK" "$THEME_I18N_SHA256"
fetch "$THEME_BASE_URL/luci-app-vantage_${THEME_VERSION}-r1_all.ipk" "$VANTAGE_APP_IPK" "$VANTAGE_APP_SHA256"
fetch "$THEME_BASE_URL/luci-i18n-vantage-zh-cn_${THEME_VERSION}-r1_all.ipk" "$VANTAGE_I18N_IPK" "$VANTAGE_I18N_SHA256"

STAGE="$OUT/files"
rm -rf "$STAGE"
mkdir -p "$STAGE/usr/bin"
cp -a "$ROOT/files/." "$STAGE/"
cp "$UA3F_DIR/root/usr/bin/ua3f" "$STAGE/usr/bin/ua3f"
chmod 0755 "$STAGE/usr/bin/ua3f" \
	"$STAGE/etc/init.d/ua3f" \
	"$STAGE/etc/init.d/ruijie-disguise" \
	"$STAGE/etc/init.d/ruijie-auth" 2>/dev/null || true

cd "$IB"
PATH=/opt/openwrt/gnu-bin:$PATH make image \
	PROFILE="$PROFILE" \
	PACKAGES="$PACKAGES $THEME_IPK $THEME_I18N_IPK $VANTAGE_APP_IPK $VANTAGE_I18N_IPK $APP_IPK" \
	FILES="$STAGE" \
	-j"$JOBS"

echo
echo "Image output: $IB/bin/targets/ramips/mt7621/"
