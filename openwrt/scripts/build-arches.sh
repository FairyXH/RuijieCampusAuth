#!/bin/sh
# Build the ruijie-auth ipk for every Go-supported OpenWrt 24.10 arch.
# Usage: VERSION=0.11.4 sh scripts/build-arches.sh

set -u

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
VERSION=${VERSION:-0.11.4}
OUT=${OUT_DIR:-"$ROOT/build"}
MIRROR_ROOT=${MIRROR_ROOT:-"$ROOT/ipk"}
LOG_DIR=${LOG_DIR:-"$OUT/logs"}
mkdir -p "$LOG_DIR" "$MIRROR_ROOT"

FAILED=
while IFS='|' read -r pkg goarch goarm go386 gomips gomips64 note; do
	[ -n "$pkg" ] || continue
	case "$pkg" in \#*) continue ;; esac
	echo "=== $pkg  GOARCH=$goarch ${goarm:+GOARM=$goarm} ${go386:+GO386=$go386} ${gomips:+GOMIPS=$gomips} ${gomips64:+GOMIPS64=$gomips64}"
	if (
		export GOARCH="$goarch"
		[ -n "$goarm" ] && export GOARM="$goarm"
		[ -n "$go386" ] && export GO386="$go386"
		[ -n "$gomips" ] && export GOMIPS="$gomips"
		[ -n "$gomips64" ] && export GOMIPS64="$gomips64"
		VERSION="$VERSION" PKG_ARCH="$pkg" sh "$ROOT/scripts/build-ipk.sh"
	) >"$LOG_DIR/ipk-$pkg.log" 2>&1; then
		src="$OUT/ipk/ruijie-auth_${VERSION}_${pkg}.ipk"
		dst="$MIRROR_ROOT/$pkg/ruijie-auth/$VERSION"
		mkdir -p "$dst"
		cp -f "$src" "$dst/"
		rm -f "$MIRROR_ROOT/$pkg/.gitkeep"
		( cd "$dst" && sha256sum ./*.ipk >SHA256SUMS )
		printf '%s\n' "$VERSION" >"$MIRROR_ROOT/$pkg/ruijie-auth/latest.txt"
		echo "OK   $pkg"
	else
		echo "FAIL $pkg"
		tail -6 "$LOG_DIR/ipk-$pkg.log"
		FAILED="$FAILED $pkg"
	fi
done <<'EOF'
# pkg_arch|goarch|goarm|go386|gomips|gomips64|note
aarch64_cortex-a53|arm64||||||mediatek/filogic, mt7622, qualcommax
aarch64_cortex-a72|arm64||||||bcm27xx/bcm2711
aarch64_cortex-a76|arm64||||||bcm27xx/bcm2712
aarch64_generic|arm64||||||rockchip/armv8, armsr/armv8
arm_arm1176jzf-s_vfp|arm|6|||||ARMv6
arm_arm926ej-s|arm|5|||||ARMv5TE
arm_cortex-a15_neon-vfpv4|arm|7|||||ipq806x
arm_cortex-a5_vfpv4|arm|7|||||ARMv7-A
arm_cortex-a7|arm|7|||||ARMv7-A
arm_cortex-a7_neon-vfpv4|arm|7|||||ipq40xx, sunxi/cortexa7
arm_cortex-a7_vfpv4|arm|7|||||ARMv7-A
arm_cortex-a8_vfpv3|arm|7|||||ARMv7-A
arm_cortex-a9|arm|7|||||bcm53xx
arm_cortex-a9_neon|arm|7|||||ARMv7-A
arm_cortex-a9_vfpv3-d16|arm|7|||||mvebu/cortexa9
arm_fa526|arm|5|||||best-effort: ARMv4 core, Go minimum is ARMv5
arm_xscale|arm|5|||||ARMv5TE
i386_pentium-mmx|386||softfloat|||i386, no SSE2
i386_pentium4|386||sse2|||i386, SSE2
loongarch64_generic|loong64||||||LoongArch 64
mips64_mips64r2|mips64||||softfloat|MIPS64r2 big-endian
mips64_octeonplus|mips64||||softfloat|Octeon big-endian
mips64el_mips64r2|mips64le||||softfloat|MIPS64r2 little-endian
mips_24kc|mips|||softfloat||ath79/lantiq big-endian
mips_4kec|mips|||softfloat||MIPS32 big-endian
mips_mips32|mips|||softfloat||best-effort: generic MIPS32 big-endian
mipsel_24kc|mipsle|||softfloat||ramips/mt7621, mt7620, mt76x8
mipsel_24kc_24kf|mipsle|||softfloat||ramips hardfloat userland
mipsel_74kc|mipsle|||softfloat||ramips/rt3883
mipsel_mips32|mipsle|||softfloat||best-effort: generic MIPS32 little-endian
powerpc64_e5500|ppc64||||||best-effort: Go ppc64 assumes POWER8
riscv64_riscv64|riscv64||||||RISC-V 64
x86_64|amd64||||||x86/64
EOF

if [ -n "$FAILED" ]; then
	echo "FAILED:$FAILED"
	exit 1
fi
echo "ALL OK"
