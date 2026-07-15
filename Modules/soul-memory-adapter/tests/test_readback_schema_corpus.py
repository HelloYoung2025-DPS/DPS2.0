#!/usr/bin/env python3
"""Provider-owned Draft 2020-12 gate for soul.memory.readback/v1."""

from __future__ import annotations

import copy
import json
import unittest
from datetime import datetime, timezone
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


MODULE_ROOT = Path(__file__).resolve().parents[1]
SCHEMA_PATH = MODULE_ROOT / "contracts" / "provided" / "soul.memory.readback.v1.schema.json"
CORPUS_PATH = MODULE_ROOT / "contracts" / "provided" / "soul.memory.readback.v1.corpus.json"
EXPECTED_CASE_IDS = (
    "valid",
    "valid-no-fraction",
    "additional-field",
    "missing-occurred-at",
    "wrong-revision-type",
    "duplicate-schema-version",
    "schema-version-terminal-newline",
    "projection-version-terminal-newline",
    "soul-terminal-newline",
    "device-terminal-newline",
    "account-terminal-newline",
    "trace-terminal-newline",
    "idempotency-terminal-newline",
    "source-terminal-newline",
    "wrong-but-well-formed-source",
    "nonzero-offset",
    "zero-offset-not-zulu",
    "eight-fractional-digits",
    "fractional-trailing-zero",
    "invalid-calendar-date",
    "uppercase-revision",
    "checksum-terminal-newline",
    "prepared-claims-readback",
    "verified-valid",
    "verified-checksum-mismatch",
)


FORMAT_CHECKER = FormatChecker()


@FORMAT_CHECKER.checks("date-time", raises=(TypeError, ValueError))
def _canonical_rfc3339_datetime(value: object) -> bool:
    if not isinstance(value, str):
        return False
    parsed = datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
    return parsed.tzinfo is not None and parsed.utcoffset() == timezone.utc.utcoffset(parsed)


class ReadbackSchemaCorpusTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        cls.corpus = json.loads(CORPUS_PATH.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(cls.schema)
        cls.validator = Draft202012Validator(cls.schema, format_checker=FORMAT_CHECKER)

    def test_exact_provider_owned_case_set(self) -> None:
        case_ids = tuple(case["id"] for case in self.corpus["cases"])
        self.assertEqual(EXPECTED_CASE_IDS, case_ids)
        self.assertEqual(len(EXPECTED_CASE_IDS), len(set(case_ids)))

    def test_every_object_level_case_matches_draft_2020_12(self) -> None:
        baseline = self.corpus["base"]
        for case in self.corpus["cases"]:
            candidate = copy.deepcopy(baseline)
            candidate.update(case["patch"])
            for property_name in case["remove"]:
                candidate.pop(property_name)
            observed = not list(self.validator.iter_errors(candidate))
            self.assertEqual(case["schemaValid"], observed, case["id"])

    def test_recursive_terminal_and_case_negatives_are_present(self) -> None:
        ids = set(EXPECTED_CASE_IDS)
        for required in (
            "schema-version-terminal-newline",
            "projection-version-terminal-newline",
            "soul-terminal-newline",
            "device-terminal-newline",
            "account-terminal-newline",
            "trace-terminal-newline",
            "idempotency-terminal-newline",
            "source-terminal-newline",
            "uppercase-revision",
            "checksum-terminal-newline",
            "nonzero-offset",
            "eight-fractional-digits",
        ):
            self.assertIn(required, ids)


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ReadbackSchemaCorpusTests)
    if suite.countTestCases() != 3:
        raise SystemExit(f"FAIL: expected exactly 3 schema-corpus tests, found {suite.countTestCases()}")
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    raise SystemExit(0 if result.wasSuccessful() else 1)
