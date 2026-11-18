#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
GOARCH=${GOARCH:-mipsle}
VERSION=${VERSION:-0.1.0}
MODULE=github.com/FairyXH/ruijie-campus-auth-hxxy/openwrt/hxxy-auth
COMMIT=$(git -C "$ROOT/.." rev-parse --short HEAD 2>/dev/null || echo dev)

mkdir -p "$OUT"
cd "$ROOT/hxxy-auth"

if [ "$GOARCH" = "mipsle" ]; then
	export GOMIPS=${GOMIPS:-softfloat}
fi

CGO_ENABLED=0 GOOS=linux GOARCH="$GOARCH" \
	go build -trimpath \
	-ldflags "-s -w -X ${MODULE}/internal/version.Version=${VERSION} -X ${MODULE}/internal/version.Commit=${COMMIT}" \
	-o "$OUT/hxxy-auth" ./cmd/hxxy-auth

if command -v file >/dev/null 2>&1; then
	file "$OUT/hxxy-auth"
fi
