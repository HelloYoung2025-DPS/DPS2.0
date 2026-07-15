"""Child-process probe for PostgreSQL lease recovery integration evidence."""

import json
import os
import sys
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "src"
sys.path.insert(0, str(SOURCE))

from postgres_lease_store import PostgresLeaseStore


def main() -> int:
    dsn = os.environ.get("DPS_TEST_POSTGRES_URI")
    schema = os.environ.get("DPS_TEST_SCHEMA")
    lease_path = os.environ.get("DPS_TEST_LEASE_JSON")
    if not dsn or not schema or not lease_path:
        raise RuntimeError("INFRA_ERROR: subprocess lease recovery inputs missing")
    path = Path(lease_path)
    if path.is_symlink() or not path.is_file():
        raise RuntimeError("INFRA_ERROR: invalid subprocess lease fixture")
    lease = json.loads(path.read_text(encoding="utf-8"))
    store = PostgresLeaseStore(dsn, schema=schema)
    store.assert_fence(lease["lease_id"], lease["lock_tokens"])
    fact = store.verify_fence_fact(lease)
    if fact["fact_id"] != lease["lease_id"]:
        raise RuntimeError("recovered immutable lease fact mismatch")
    print("OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
