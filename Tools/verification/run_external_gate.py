#!/usr/bin/env python3
"""Command-line entry point for DPS F6-F9 external verification gates."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from external_gate import STAGE_SPECS, run_gate


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate signed external evidence without issuing a verification receipt."
    )
    parser.add_argument("--stage", choices=sorted(STAGE_SPECS), required=True)
    parser.add_argument("--input", type=Path, help="absolute or runner-local path to external evidence JSON")
    parser.add_argument(
        "--trust-policy",
        type=Path,
        help="absolute path to a deployment-owned, read-only trust policy JSON",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    decision = run_gate(args.stage, args.input, args.trust_policy)
    print(json.dumps(decision.as_dict(), sort_keys=True, separators=(",", ":")))
    return decision.exit_code


if __name__ == "__main__":
    raise SystemExit(main())
