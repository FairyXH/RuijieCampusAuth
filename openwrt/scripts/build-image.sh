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
IB=${IB_DIR:-"$ROOT/build/ib/openwrt-imagebuilder-24.10.0-ramips-mt7621.Linux-x86_64"}
PROFILE=${PROFILE:-xiaomi_mi-router-3g}
JOBS=${JOBS:-4}
VERSION=${VERSION:-0.10.0}

UA3F_VERSION=3.7.0
UA3F_SHA256=a1d710233ab69af2e127193d7de81d7fd7c400294dc2147368694f2fd56e073f
UA3F_URL="https://github.com/SunBK201/UA3F/releases/download/v${UA3F_VERSION}/ua3f_${UA3F_VERSION}-r1_mipsel_24kc.ipk"
UA3F_IPK=${UA3F_IPK:-"$OUT/ua3f/ua3f.ipk"}
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

# Every kernel module built for this target, minus the three mutually
# exclusive variants. Kernel modules are tied to the exact kernel ABI, so
# anything not preinstalled here can only be added by rebuilding the image.
# The list is generated from the 24.10.0 ramips/mt7621 kmods index.
KMODS_FILE=${KMODS_FILE:-"$ROOT/scripts/kmods-all.txt"}
if [ ! -s "$KMODS_FILE" ]; then
	echo "kmod list not found: $KMODS_FILE" >&2
	exit 2
fi
ALL_KMODS=$(tr '\n' ' ' <"$KMODS_FILE")

OPENCLASH_VERSION=0.48.002
OPENCLASH_SHA256=f3f0d0514553aea8d542c62dc52ac26d1f79dbda24204280f1cb8ea7fff71e9e
OPENCLASH_URL="https://github.com/vernesong/OpenClash/releases/download/v${OPENCLASH_VERSION}/luci-app-openclash_${OPENCLASH_VERSION}_all.ipk"
OPENCLASH_IPK=${OPENCLASH_IPK:-"$OUT/openclash/luci-app-openclash_${OPENCLASH_VERSION}_all.ipk"}

# MT7621 is a MIPS 24KEc without an FPU; the softfloat build is the correct
# mihomo core. OpenClash looks for it at /etc/openclash/core/clash_meta.
MIHOMO_VERSION=1.19.34
MIHOMO_SHA256=b34bbb86196c32915abf4a5b361c11218ea1da4344a353768f570e79f69d2c04
MIHOMO_URL="https://github.com/MetaCubeX/mihomo/releases/download/v${MIHOMO_VERSION}/mihomo-linux-mipsle-softfloat-v${MIHOMO_VERSION}.gz"
MIHOMO_GZ=${MIHOMO_GZ:-"$OUT/openclash/mihomo-linux-mipsle-softfloat-v${MIHOMO_VERSION}.gz"}

# Defaults chosen for a single-router, unattended deployment:
# - wpad-mbedtls replaces the basic build so STA/enterprise/802.11r work.
# - dnsmasq-full replaces dnsmasq; OpenClash needs its nftset/ipset support.
# - tc-tiny is the smallest provider of the virtual `tc` package SQM needs.
# - OpenClash and frpc are installed and configured but not started; both
#   need user credentials before they can do anything useful.
DEFAULT_PACKAGES="luci-ssl luci-i18n-base-zh-cn \
	https-dns-proxy luci-app-https-dns-proxy luci-i18n-https-dns-proxy-zh-cn \
	kmod-nft-queue kmod-nft-tproxy kmod-nfnetlink-queue kmod-nf-conntrack-netlink \
	iptables iptables-mod-tproxy iptables-mod-extra iptables-mod-ipopt \
	iptables-mod-nfqueue iptables-mod-conntrack-extra ipset luci-compat \
	wpad-mbedtls -wpad-basic-mbedtls \
	dnsmasq-full -dnsmasq \
	mwan3 luci-app-mwan3 luci-i18n-mwan3-zh-cn \
	sqm-scripts luci-app-sqm luci-i18n-sqm-zh-cn tc-tiny kmod-ifb kmod-sched-cake \
	nlbwmon luci-app-nlbwmon luci-i18n-nlbwmon-zh-cn \
	vnstat2 vnstati2 luci-app-vnstat2 luci-i18n-vnstat2-zh-cn \
	frpc luci-app-frpc luci-i18n-frpc-zh-cn \
	miniupnpd-nftables luci-app-upnp luci-i18n-upnp-zh-cn \
	luci-app-wol luci-i18n-wol-zh-cn etherwake \
	luci-app-commands luci-i18n-commands-zh-cn \
	usbutils usb-modeswitch pciutils ethtool iw \
	e2fsprogs dosfstools f2fs-tools ntfs-3g btrfs-progs \
	bash curl ca-bundle unzip ip-full httping \
	ruby ruby-yaml kmod-tun kmod-inet-diag kmod-netlink-diag \
	$ALL_KMODS"
PACKAGES=${PACKAGES:-$DEFAULT_PACKAGES}

if [ ! -d "$IB" ]; then
	IB=$(sh "$ROOT/scripts/fetch-imagebuilder.sh" "$ROOT/build/ib") || exit 2
fi

fetch() {
	local url=$1 file=$2 sum=$3 vendor
	vendor="$ROOT/vendor/$(basename "$file")"
	mkdir -p "$(dirname "$file")"
	if [ -f "$vendor" ]; then
		cp "$vendor" "$file"
	elif [ ! -f "$file" ]; then
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

# UA3F: installed as a package so opkg can upgrade it later; the binary is
# also extracted only to validate the package layout.
fetch "$UA3F_URL" "$UA3F_IPK" "$UA3F_SHA256"
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

# OpenClash and its core are fetched with pinned checksums. The app package
# does not ship a core, so the softfloat mihomo binary is staged separately.
fetch "$OPENCLASH_URL" "$OPENCLASH_IPK" "$OPENCLASH_SHA256"
fetch "$MIHOMO_URL" "$MIHOMO_GZ" "$MIHOMO_SHA256"

STAGE="$OUT/files"
rm -rf "$STAGE"
mkdir -p "$STAGE/usr/bin" "$STAGE/etc/openclash/core"
cp -a "$ROOT/files/." "$STAGE/"
cp "$UA3F_DIR/root/usr/bin/ua3f" "$STAGE/usr/bin/ua3f"
gzip -dc "$MIHOMO_GZ" >"$STAGE/etc/openclash/core/clash_meta"
chmod 0755 "$STAGE/usr/bin/ua3f" \
	"$STAGE/etc/openclash/core/clash_meta" \
	"$STAGE/etc/init.d/ua3f" \
	"$STAGE/etc/init.d/ruijie-disguise" \
	"$STAGE/etc/init.d/ruijie-auth" 2>/dev/null || true

cd "$IB"
PATH=/opt/openwrt/gnu-bin:$PATH make image \
	PROFILE="$PROFILE" \
	PACKAGES="$PACKAGES $UA3F_IPK $THEME_IPK $THEME_I18N_IPK $VANTAGE_APP_IPK $VANTAGE_I18N_IPK $OPENCLASH_IPK $APP_IPK" \
	FILES="$STAGE" \
	-j"$JOBS"

echo
echo "Image output: $IB/bin/targets/ramips/mt7621/"
