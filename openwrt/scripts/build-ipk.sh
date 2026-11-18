#!/bin/sh
set -eu

# Build a self-contained hxxy-auth ipk from the same sources that go into
# the firmware image. Installing a newer build on the router is then:
#
#   opkg install --force-reinstall /tmp/hxxy-auth_<version>_mipsel_24kc.ipk
#
# /etc/config/hxxy-auth is a conffile, so accounts and settings survive.

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
VERSION=${VERSION:-0.1.0}
ARCH=${PKG_ARCH:-mipsel_24kc}
IPK="$OUT/ipk/hxxy-auth_${VERSION}_${ARCH}.ipk"
STAGE="$OUT/ipk/stage"
DATA="$STAGE/data"
CONTROL="$STAGE/control"

VERSION="$VERSION" sh "$ROOT/scripts/build-go.sh"

rm -rf "$STAGE"
mkdir -p "$DATA" "$CONTROL"

put() {
	mkdir -p "$DATA/$(dirname "$2")"
	cp "$1" "$DATA/$2"
	chmod "$3" "$DATA/$2"
}

put "$OUT/hxxy-auth" usr/sbin/hxxy-auth 0755
put "$ROOT/files/etc/init.d/hxxy-auth" etc/init.d/hxxy-auth 0755
put "$ROOT/files/etc/init.d/hxxy-disguise" etc/init.d/hxxy-disguise 0755
put "$ROOT/files/etc/config/hxxy-auth" etc/config/hxxy-auth 0600
put "$ROOT/files/etc/sysctl.d/90-hxxy.conf" etc/sysctl.d/90-hxxy.conf 0644
put "$ROOT/files/etc/hotplug.d/iface/90-hxxy-auth" etc/hotplug.d/iface/90-hxxy-auth 0755
put "$ROOT/files/etc/hotplug.d/net/90-hxxy-usbwan" etc/hotplug.d/net/90-hxxy-usbwan 0755
put "$ROOT/luci-app-hxxy-auth/root/etc/uci-defaults/90-hxxy-auth" etc/uci-defaults/90-hxxy-auth 0755
put "$ROOT/luci-app-hxxy-auth/root/usr/share/luci/menu.d/luci-app-hxxy-auth.json" \
	usr/share/luci/menu.d/luci-app-hxxy-auth.json 0644
put "$ROOT/luci-app-hxxy-auth/root/usr/share/rpcd/acl.d/luci-app-hxxy-auth.json" \
	usr/share/rpcd/acl.d/luci-app-hxxy-auth.json 0644

for js in "$ROOT"/luci-app-hxxy-auth/htdocs/luci-static/resources/view/hxxy-auth/*.js; do
	put "$js" "www/luci-static/resources/view/hxxy-auth/$(basename "$js")" 0644
done

SIZE=$(du -sk "$DATA" | awk '{print $1}')

cat > "$CONTROL/control" <<EOF
Package: hxxy-auth
Version: $VERSION
Depends: libc, luci-base, rpcd
Source: hxxy-auth
SourceName: hxxy-auth
License: MIT
Section: net
Architecture: $ARCH
Installed-Size: $SIZE
Maintainer: YG
Description: Ruijie campus network authentication daemon with LuCI management.
EOF

# Keep the user's accounts/settings across an ipk upgrade.
printf '/etc/config/hxxy-auth\n' > "$CONTROL/conffiles"

cat > "$CONTROL/postinst" <<'EOF'
#!/bin/sh
[ -n "${IPKG_INSTROOT:-}" ] && exit 0
/etc/init.d/hxxy-auth restart >/dev/null 2>&1 || true
/etc/init.d/hxxy-disguise restart >/dev/null 2>&1 || true
exit 0
EOF
chmod 0755 "$CONTROL/postinst"

( cd "$DATA" && tar --owner=0 --group=0 --numeric-owner -czf "$STAGE/data.tar.gz" . )
( cd "$CONTROL" && tar --owner=0 --group=0 --numeric-owner -czf "$STAGE/control.tar.gz" \
	./control ./conffiles ./postinst )
printf '2.0\n' > "$STAGE/debian-binary"
# Use the outer tar.gz ipk layout (same as UA3F and opkg-build output).
# The Image Builder's host opkg crashes on GNU ar archives here.
rm -f "$IPK"
( cd "$STAGE" && tar --owner=0 --group=0 --numeric-owner -czf "$IPK" \
	./debian-binary ./control.tar.gz ./data.tar.gz )

echo "ipk: $IPK"
