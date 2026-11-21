#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
OUT=${OUT_DIR:-"$ROOT/build"}
GOARCH=${GOARCH:-mipsle}
VERSION=${VERSION:-0.10.0}
MODULE=ruijie-auth
# The build host often has a synced subset of the tree rather than a git
# checkout, so allow the caller to pass the commit explicitly.
COMMIT=${COMMIT:-$(git -C "$ROOT/.." rev-parse --short HEAD 2>/dev/null || echo dev)}

mkdir -p "$OUT"
cd "$ROOT/ruijie-auth"

if [ "$GOARCH" = "mipsle" ]; then
	export GOMIPS=${GOMIPS:-softfloat}
fi

CGO_ENABLED=0 GOOS=linux GOARCH="$GOARCH" \
	go build -trimpath \
	-ldflags "-s -w -X ${MODULE}/internal/version.Version=${VERSION} -X ${MODULE}/internal/version.Commit=${COMMIT}" \
	-o "$OUT/ruijie-auth" ./cmd/ruijie-auth

if command -v file >/dev/null 2>&1; then
	file "$OUT/ruijie-auth"
fi
