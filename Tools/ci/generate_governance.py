#!/usr/bin/env python3
"""Generate or verify the checked-in DPS module governance snapshots."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = Path(__file__).resolve().parent
if str(CI_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(CI_DIRECTORY))

from phase0 import (  # noqa: E402
    Phase0Error,
    governance_snapshots,
    load_module_records_without_schema,
    validate_governance_snapshots,
)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--write",
        action="store_true",
        help="write canonical snapshots instead of checking them",
    )
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    try:
        records = load_module_records_without_schema(ROOT)
        if arguments.write:
            for relative_path, value in sorted(governance_snapshots(records).items()):
                path = ROOT / relative_path
                path.parent.mkdir(parents=True, exist_ok=True)
                temporary = path.with_suffix(path.suffix + ".tmp")
                temporary.write_text(
                    json.dumps(value, ensure_ascii=False, indent=2) + "\n",
                    encoding="utf-8",
                )
                temporary.replace(path)
                print("generated " + relative_path)
        details = validate_governance_snapshots(ROOT, records)
        print("governance snapshots verified: {0}".format(len(details["files"])))
        return 0
    except (Phase0Error, OSError, ValueError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
