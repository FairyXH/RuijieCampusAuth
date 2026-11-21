#!/bin/sh
set -eu

# Build a self-contained ruijie-auth ipk from the same sources that go into
# the firmware image. Installing a newer build on the router is then:
#
#   opkg install --force-reinstall /tmp/ruijie-auth_<version>_mipsel_24kc.ipk
#
# /etc/config/ruijie-auth is a conffile, so accounts, settings and the
# imported school profile survive an upgrade.
#
# The package is pure userland: AF_PACKET + Go stdlib. No kmod dependencies,
# so the same binary runs on any OpenWrt device with the same architecture.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
VERSION=${VERSION:-0.10.0}
ARCH=${PKG_ARCH:-mipsel_24kc}
LUCI="$ROOT/luci-app-ruijie-auth"
IPK="$OUT/ipk/ruijie-auth_${VERSION}_${ARCH}.ipk"
STAGE="$OUT/ipk/stage"
DATA="$STAGE/data"
CONTROL="$STAGE/control"

VERSION="$VERSION" sh "$ROOT/scripts/build-go.sh"

rm -rf "$STAGE"
mkdir -p "$DATA" "$CONTROL" "$DATA/etc/ruijie-auth/profiles"

put() {
	mkdir -p "$DATA/$(dirname "$2")"
	cp "$1" "$DATA/$2"
	chmod "$3" "$DATA/$2"
}

put "$OUT/ruijie-auth" usr/sbin/ruijie-auth 0755
put "$ROOT/files/etc/init.d/ruijie-auth" etc/init.d/ruijie-auth 0755
put "$ROOT/files/etc/init.d/ruijie-disguise" etc/init.d/ruijie-disguise 0755
put "$ROOT/files/etc/config/ruijie-auth" etc/config/ruijie-auth 0600
put "$ROOT/files/etc/sysctl.d/90-ruijie.conf" etc/sysctl.d/90-ruijie.conf 0644
put "$ROOT/files/etc/hotplug.d/iface/90-ruijie-auth" etc/hotplug.d/iface/90-ruijie-auth 0755
put "$ROOT/files/etc/hotplug.d/net/90-ruijie-usbwan" etc/hotplug.d/net/90-ruijie-usbwan 0755
put "$LUCI/root/etc/uci-defaults/90-ruijie-auth" etc/uci-defaults/90-ruijie-auth 0755
put "$LUCI/root/usr/share/luci/menu.d/luci-app-ruijie-auth.json" \
	usr/share/luci/menu.d/luci-app-ruijie-auth.json 0644
put "$LUCI/root/usr/share/rpcd/acl.d/luci-app-ruijie-auth.json" \
	usr/share/rpcd/acl.d/luci-app-ruijie-auth.json 0644

for js in "$LUCI"/htdocs/luci-static/resources/view/ruijie-auth/*.js; do
	put "$js" "www/luci-static/resources/view/ruijie-auth/$(basename "$js")" 0644
done

# LuCI merges every /usr/lib/lua/luci/i18n/<domain>.<lang>.lmo for the
# selected language, so ship the compiled Chinese catalog.
PYTHON=""
for candidate in python3 python; do
	if command -v "$candidate" >/dev/null 2>&1; then
		PYTHON=$candidate
		break
	fi
done
if [ -n "$PYTHON" ]; then
	"$PYTHON" "$ROOT/scripts/po2lmo.py" \
		"$LUCI/po/zh_Hans/ruijie-auth.po" \
		"$STAGE/ruijie-auth.zh-cn.lmo"
	put "$STAGE/ruijie-auth.zh-cn.lmo" usr/lib/lua/luci/i18n/ruijie-auth.zh-cn.lmo 0644
	rm -f "$STAGE/ruijie-auth.zh-cn.lmo"
else
	echo "warning: python3 not found, skipping zh-cn catalog" >&2
fi

SIZE=$(du -sk "$DATA" | awk '{print $1}')

cat > "$CONTROL/control" <<EOF
Package: ruijie-auth
Version: $VERSION
Depends: libc, luci-base, rpcd
Source: ruijie-auth
SourceName: ruijie-auth
License: MIT
Section: net
Architecture: $ARCH
Installed-Size: $SIZE
Maintainer: YG
Description: Ruijie campus network 802.1X authenticator with LuCI management.
EOF

# Keep the user's accounts/settings/profile across an ipk upgrade.
printf '/etc/config/ruijie-auth\n' > "$CONTROL/conffiles"

cat > "$CONTROL/postinst" <<'EOF'
#!/bin/sh
[ -n "${IPKG_INSTROOT:-}" ] && exit 0
mkdir -p /etc/ruijie-auth/profiles
chmod 700 /etc/ruijie-auth /etc/ruijie-auth/profiles 2>/dev/null
chmod 600 /etc/config/ruijie-auth 2>/dev/null
/etc/init.d/ruijie-auth restart >/dev/null 2>&1 || true
/etc/init.d/ruijie-disguise restart >/dev/null 2>&1 || true
exit 0
EOF
chmod 0755 "$CONTROL/postinst"

( cd "$DATA" && tar --owner=0 --group=0 --numeric-owner -czf "$STAGE/data.tar.gz" . )
( cd "$CONTROL" && tar --owner=0 --group=0 --numeric-owner -czf "$STAGE/control.tar.gz" \
	./control ./conffiles ./postinst )
printf '2.0\n' > "$STAGE/debian-binary"
# Use the outer tar.gz ipk layout (same as opkg-build output). The Image
# Builder's host opkg crashes on GNU ar archives here.
rm -f "$IPK"
( cd "$STAGE" && tar --owner=0 --group=0 --numeric-owner -czf "$IPK" \
	./debian-binary ./control.tar.gz ./data.tar.gz )

echo "ipk: $IPK"
