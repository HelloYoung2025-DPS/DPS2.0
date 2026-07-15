from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import pathlib
import struct
import unittest
import uuid

from jsonschema import Draft202012Validator, FormatChecker, ValidationError, validators


TEST_ROOT = pathlib.Path(__file__).resolve().parent
SCHEMA_PATH = TEST_ROOT.parent / "contracts" / "provided" / "action.proposal.v2.schema.json"
LEGACY_SCHEMA_PATH = TEST_ROOT.parent / "contracts" / "provided" / "action.proposal.v1.schema.json"
CASES_PATH = TEST_ROOT / "action-proposal-contract-cases.v2.json"
MAXIMUM_WIRE_BYTES = 32 * 1024

FORMAT_CHECKER = FormatChecker()


@FORMAT_CHECKER.checks("date-time", raises=(TypeError, ValueError))
def _is_rfc3339_datetime(value: object) -> bool:
    if not isinstance(value, str):
        return True
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    return parsed.tzinfo is not None and parsed.utcoffset() == dt.timedelta(0)


def _strict_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON member: {key}")
        result[key] = value
    return result


def _strict_loads(raw: str) -> object:
    if not raw or len(raw.encode("utf-8", errors="strict")) > MAXIMUM_WIRE_BYTES:
        raise ValueError("action.proposal/v2 wire size is outside the allowed range")
    return json.loads(
        raw,
        object_pairs_hook=_strict_object,
        parse_constant=lambda value: (_ for _ in ()).throw(ValueError(f"invalid JSON constant: {value}")),
    )


def _validate_derived_proposal_id(validator, specification, instance, schema):
    del validator, schema
    if not isinstance(instance, dict) or not isinstance(specification, dict):
        return
    field_names = ("soul_id", "device_binding_id", "platform_account_id", "idempotency_key")
    proposal_id = instance.get("proposal_id")
    values = [instance.get(field_name) for field_name in field_names]
    domain = specification.get("domain")
    if not isinstance(proposal_id, str) or not isinstance(domain, str) or not all(
        isinstance(value, str) for value in values
    ):
        return
    try:
        fields = [domain, *values]
        canonical = b"".join(
            struct.pack(">I", len(encoded)) + encoded
            for encoded in (field.encode("utf-8", errors="strict") for field in fields)
        )
    except (OverflowError, UnicodeEncodeError):
        return
    digest = bytearray(hashlib.sha256(canonical).digest()[:16])
    digest[6] = (digest[6] & 0x0F) | 0x80
    digest[8] = (digest[8] & 0x3F) | 0x80
    expected = str(uuid.UUID(bytes=bytes(digest)))
    if proposal_id != expected:
        yield ValidationError("proposal_id does not match the declared canonical derivation")


DpsDraft202012Validator = validators.extend(
    Draft202012Validator,
    {"x-dps-proposal-id": _validate_derived_proposal_id},
)


class ActionProposalSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        cls.corpus = json.loads(CASES_PATH.read_text(encoding="utf-8"))
        cls.validator = DpsDraft202012Validator(cls.schema, format_checker=FORMAT_CHECKER)

    def test_schema_is_valid_draft_2020_12(self) -> None:
        self.assertEqual(
            "https://json-schema.org/draft/2020-12/schema",
            self.schema["$schema"],
        )
        self.assertEqual(MAXIMUM_WIRE_BYTES, self.schema["x-dps-max-wire-bytes"])
        self.assertEqual(
            hashlib.sha256(CASES_PATH.read_bytes()).hexdigest(),
            self.schema["x-dps-adversarial-corpus-sha256"],
        )
        self.assertEqual(
            {
                "domain": "dps.planner.proposal-id/v1",
                "digest": "SHA-256",
                "encoding": "uint32-big-endian-length-prefixed-strict-utf8",
                "uuidVersion": 8,
                "fields": [
                    "domain",
                    "soul_id",
                    "device_binding_id",
                    "platform_account_id",
                    "idempotency_key",
                ],
            },
            self.schema["x-dps-proposal-id"],
        )
        self.assertEqual(
            "dps.planner.action-proposal-sha256/v2",
            self.schema["x-dps-canonical-sha256"]["domain"],
        )
        self.assertEqual(
            "^selector_[a-f0-9]{64}$(?![\\s\\S])",
            self.schema["$defs"]["selector_ref"]["pattern"],
        )
        self.assertEqual(
            "^value_[a-f0-9]{64}$(?![\\s\\S])",
            self.schema["$defs"]["value_ref"]["pattern"],
        )
        self.assertEqual(
            "^evidence_[a-f0-9]{64}$(?![\\s\\S])",
            self.schema["$defs"]["evidence_ref"]["pattern"],
        )
        Draft202012Validator.check_schema(self.schema)

    def test_shared_adversarial_corpus_has_expected_result(self) -> None:
        for case in self.corpus["cases"]:
            with self.subTest(case=case["name"]):
                instance = copy.deepcopy(self.corpus["base"])
                instance.update(copy.deepcopy(case.get("overrides", {})))
                for property_name in case.get("remove", []):
                    instance.pop(property_name, None)
                errors = list(self.validator.iter_errors(instance))
                self.assertEqual(case["valid"], not errors, [error.message for error in errors])

    def test_wire_loader_rejects_duplicate_root_and_parameter_members(self) -> None:
        base = copy.deepcopy(self.corpus["base"])
        raw = json.dumps(base, separators=(",", ":"))
        duplicate_root = raw[:-1] + ',"trace_id":"trace_attack"}'
        with self.assertRaisesRegex(ValueError, "duplicate JSON member: trace_id"):
            _strict_loads(duplicate_root)

        base.update(
            {
                "action_kind": "locate",
                "parameters": {
                    "selector_ref": "selector_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                },
            }
        )
        raw = json.dumps(base, separators=(",", ":"))
        duplicate_parameter = raw.replace(
            '"parameters":{"selector_ref":"selector_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}',
            '"parameters":{"selector_ref":"selector_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","selector_ref":"selector_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}',
        )
        with self.assertRaisesRegex(ValueError, "duplicate JSON member: selector_ref"):
            _strict_loads(duplicate_parameter)

        with self.assertRaisesRegex(ValueError, "wire size"):
            _strict_loads(raw + (" " * MAXIMUM_WIRE_BYTES))
        with self.assertRaisesRegex(ValueError, "invalid JSON constant"):
            _strict_loads('{"value":NaN}')

    def test_unpaired_surrogate_and_invalid_utf8_fail_closed(self) -> None:
        instance = copy.deepcopy(self.corpus["base"])
        instance["trace_id"] = "trace_\ud800"
        parsed = _strict_loads(json.dumps(instance, ensure_ascii=True))
        self.assertTrue(list(self.validator.iter_errors(parsed)))
        with self.assertRaises(UnicodeDecodeError):
            _strict_loads(b'{"trace_id":"\xff"}'.decode("utf-8", errors="strict"))

    def test_action_matrix_is_exact_and_side_effect_bound(self) -> None:
        expected = {
            "observe": (False, set()),
            "locate": (False, {"selector_ref"}),
            "verify": (False, {"selector_ref"}),
            "wait": (False, {"duration_ms"}),
            "fixture.tap": (True, {"selector_ref"}),
            "fixture.type": (True, {"selector_ref", "value_ref"}),
        }
        branches = self.schema["allOf"][0]["oneOf"]
        actual: dict[str, tuple[bool, set[str]]] = {}
        for branch in branches:
            properties = branch["properties"]
            actual[properties["action_kind"]["const"]] = (
                properties["is_side_effect"]["const"],
                set(properties["parameters"].get("required", [])),
            )
        self.assertEqual(expected, actual)

    def test_legacy_v1_schema_remains_separate_shadow_only_compatibility(self) -> None:
        legacy = json.loads(LEGACY_SCHEMA_PATH.read_text(encoding="utf-8"))
        self.assertEqual("action.proposal/v1", legacy["title"])
        self.assertEqual("1.0.0", legacy["properties"]["schema_version"]["const"])
        self.assertEqual("action.proposal/v1", legacy["properties"]["contract_id"]["const"])
        self.assertTrue(legacy["properties"]["shadow_only"]["const"])
        self.assertIn("opaque_ref", legacy["$defs"])
        self.assertNotIn("selector_ref", legacy["$defs"])
        self.assertNotEqual(legacy["$id"], self.schema["$id"])


if __name__ == "__main__":
    unittest.main()
