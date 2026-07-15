#!/usr/bin/env bash
set -euo pipefail

POSTGRES_APP="${POSTGRES_APP:-$HOME/Applications/Postgres.app}"
POSTGRES_DATA="${DPS_POSTGRES_DATA:-$HOME/.local/share/dps-postgres-18}"
POSTGRES_BIN="$POSTGRES_APP/Contents/Versions/18/bin"

if [[ ! -x "$POSTGRES_BIN/pg_ctl" || ! -f "$POSTGRES_DATA/PG_VERSION" ]]; then
  echo "No DPS PostgreSQL test instance is installed."
  exit 0
fi

if "$POSTGRES_BIN/pg_ctl" -D "$POSTGRES_DATA" status >/dev/null 2>&1; then
  "$POSTGRES_BIN/pg_ctl" -D "$POSTGRES_DATA" stop -m fast
else
  echo "DPS PostgreSQL test instance is already stopped."
fi
