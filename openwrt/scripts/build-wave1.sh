#!/bin/sh
# Build every Wave 1 profile sequentially. Logs go to build/logs/<profile>.log.
# Usage: IB_DIR=/opt/openwrt/... JOBS=8 sh scripts/build-wave1.sh

set -u

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
JOBS=${JOBS:-8}
LOG_DIR=${LOG_DIR:-"$ROOT/build/logs"}
mkdir -p "$LOG_DIR"

FAILED=
for env in "$ROOT"/scripts/profiles/*.env; do
	name=$(basename "$env" .env)
	echo "=== $name ==="
	if IB_DIR="${IB_DIR:-}" JOBS="$JOBS" sh "$ROOT/scripts/build-target.sh" "$env" \
		>"$LOG_DIR/$name.log" 2>&1; then
		echo "OK   $name"
	else
		rc=$?
		echo "FAIL $name (exit $rc)"
		FAILED="$FAILED $name"
	fi
done

if [ -n "$FAILED" ]; then
	echo "FAILED:$FAILED"
	exit 1
fi
echo "ALL OK"
