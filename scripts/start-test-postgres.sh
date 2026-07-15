#!/usr/bin/env bash
set -euo pipefail

POSTGRES_APP="${POSTGRES_APP:-$HOME/Applications/Postgres.app}"
POSTGRES_VERSION="${DPS_POSTGRES_VERSION:-18.4}"
POSTGRES_PORT="${DPS_POSTGRES_PORT:-55432}"
POSTGRES_DATA="${DPS_POSTGRES_DATA:-$HOME/.local/share/dps-postgres-18}"
POSTGRES_SOCKET="${DPS_POSTGRES_SOCKET:-$HOME/.local/share/dps-postgres-socket}"
POSTGRES_DATABASE="${DPS_POSTGRES_DATABASE:-dps_test}"
POSTGRES_BIN="$POSTGRES_APP/Contents/Versions/18/bin"

if [[ ! "$POSTGRES_DATABASE" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
  echo "Invalid DPS_POSTGRES_DATABASE identifier" >&2
  exit 2
fi

if [[ ! -x "$POSTGRES_BIN/postgres" ]]; then
  echo "PostgreSQL 18 binaries not found at $POSTGRES_BIN" >&2
  exit 2
fi

actual_version="$($POSTGRES_BIN/postgres --version)"
if [[ "$actual_version" != *" $POSTGRES_VERSION "* && "$actual_version" != *" $POSTGRES_VERSION" ]]; then
  echo "Expected PostgreSQL $POSTGRES_VERSION, found: $actual_version" >&2
  exit 2
fi

mkdir -p "$(dirname "$POSTGRES_DATA")" "$POSTGRES_SOCKET"

if [[ ! -f "$POSTGRES_DATA/PG_VERSION" ]]; then
  "$POSTGRES_BIN/initdb" \
    -D "$POSTGRES_DATA" \
    --encoding=UTF8 \
    --locale=C \
    --auth=trust
fi

if ! "$POSTGRES_BIN/pg_ctl" -D "$POSTGRES_DATA" status >/dev/null 2>&1; then
  "$POSTGRES_BIN/pg_ctl" \
    -D "$POSTGRES_DATA" \
    -l "$POSTGRES_DATA/postgres.log" \
    -o "-h '' -k '$POSTGRES_SOCKET' -p $POSTGRES_PORT" \
    start
fi

if ! "$POSTGRES_BIN/psql" \
  -h "$POSTGRES_SOCKET" \
  -p "$POSTGRES_PORT" \
  -d postgres \
  -Atqc "SELECT 1 FROM pg_database WHERE datname = '$POSTGRES_DATABASE'" | grep -qx 1; then
  "$POSTGRES_BIN/createdb" \
    -h "$POSTGRES_SOCKET" \
    -p "$POSTGRES_PORT" \
    "$POSTGRES_DATABASE"
fi

echo "PostgreSQL test instance is ready."
echo "export DPS_TEST_POSTGRES='Host=$POSTGRES_SOCKET;Port=$POSTGRES_PORT;Database=$POSTGRES_DATABASE;Username=$(id -un);Pooling=false'"
echo "export DPS_TEST_POSTGRES_URI='host=$POSTGRES_SOCKET port=$POSTGRES_PORT dbname=$POSTGRES_DATABASE user=$(id -un)'"
echo "export DPS_PSQL='$POSTGRES_BIN/psql'"
