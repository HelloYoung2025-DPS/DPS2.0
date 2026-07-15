import copy
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker, ValidationError, validators
from referencing import Registry, Resource


SCHEMA_PATH = (
    Path(__file__).resolve().parents[1]
    / "contracts"
    / "provided"
    / "persona.revision.v1.schema.json"
)
CORPUS_PATH = SCHEMA_PATH.parent / "corpus" / "persona.revision.v1.corpus.json"
HISTORY_EXPORT_SCHEMA_PATH = SCHEMA_PATH.parent / "persona.history.export.v1.schema.json"
HISTORY_EXPORT_CORPUS_PATH = SCHEMA_PATH.parent / "corpus" / "persona.history.export.v1.corpus.json"
EXPECTED_CORPUS_CASE_IDS = (
    "persona.valid.active.minimal",
    "persona.valid.active.utc-seven-fraction-int64-max",
    "persona.valid.deleted.empty-traits",
    "persona.invalid.version.unknown-major",
    "persona.invalid.version.trailing-newline",
    "persona.invalid.occurred-at.nonzero-offset",
    "persona.invalid.occurred-at.before-range",
    "persona.invalid.occurred-at.after-range",
    "persona.invalid.occurred-at.eight-fraction-digits",
    "persona.invalid.revision.int64-overflow",
    "persona.invalid.active.empty-traits",
    "persona.invalid.deleted.nonempty-traits",
    "persona.invalid.traits.reversed",
    "persona.invalid.traits.duplicate",
    "persona.invalid.evidence.reversed",
    "persona.invalid.evidence.duplicate",
    "persona.invalid.evidence.over-64",
    "persona.invalid.soul.trailing-newline",
    "persona.invalid.device-binding.bad-length",
    "persona.invalid.platform-account.bad-hex",
    "persona.invalid.trace.trailing-newline",
    "persona.invalid.idempotency.bad-prefix",
    "persona.invalid.traits-hash.trailing-newline",
    "persona.invalid.evidence-hash.trailing-newline",
    "persona.invalid.unknown-field",
    "persona.invalid.contract-id.case-change",
    "persona.invalid.duplicate-json-property",
)
EXPECTED_HISTORY_EXPORT_CASE_IDS = (
    "persona-history.valid.active.retained",
    "persona-history.valid.deleted.metadata-only",
    "persona-history.invalid.version.unknown-major",
    "persona-history.invalid.retained.missing-traits",
    "persona-history.invalid.retained.unknown-trait",
    "persona-history.invalid.retained.keys-mismatch",
    "persona-history.invalid.revisions.noncontiguous",
    "persona-history.invalid.deleted.no-final-tombstone",
    "persona-history.invalid.deleted.early-tombstone",
    "persona-history.invalid.soul.trailing-newline",
    "persona-history.invalid.nested-occurred-at.eight-fraction",
    "persona-history.invalid.unknown-field",
    "persona-history.invalid.privacy-not-sensitive",
    "persona-history.invalid.duplicate-json-property",
    "persona-history.invalid.nested-soul.scope-mismatch",
    "persona-history.invalid.nested-device.scope-mismatch",
    "persona-history.invalid.nested-account.scope-mismatch",
)


def _reject_duplicate_object_members(pairs):
    value = {}
    for key, member in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON property: {key}")
        value[key] = member
    return value


def _load_unique_json(raw_json):
    return json.loads(raw_json, object_pairs_hook=_reject_duplicate_object_members)


def _ordinal_ascending(validator, expected, instance, schema):
    if expected != "ordinal-ascending" or not isinstance(instance, list):
        return
    if any(not isinstance(value, str) for value in instance):
        return
    if any(left >= right for left, right in zip(instance, instance[1:])):
        yield ValidationError("array items must be strictly ordinal ascending and unique")


def _history_export(validator, expected, instance, schema):
    if expected != "exact-envelope-scope-contiguous-from-one-current-tail" or not isinstance(instance, dict):
        return
    state = instance.get("live_primary_payload_state")
    revisions = instance.get("revisions")
    if state not in ("retained", "live-primary-logically-deleted") or not isinstance(revisions, list) or not revisions:
        return
    tail = revisions[-1].get("revision") if isinstance(revisions[-1], dict) else None
    if not isinstance(tail, dict) or instance.get("snapshot_persona_revision") != tail.get("persona_revision"):
        yield ValidationError("history export snapshot tail must equal its final persona revision")
    if instance.get("export_receipt_id") != "pexport_" + str(instance.get("export_receipt_hmac_sha256", "")):
        yield ValidationError("history export receipt ID must bind its receipt HMAC")
    for index, item in enumerate(revisions):
        if not isinstance(item, dict) or not isinstance(item.get("revision"), dict):
            continue
        revision = item["revision"]
        if revision.get("persona_revision") != index + 1:
            yield ValidationError("history revisions must be contiguous and begin at one")
        if any(
            revision.get(field) != instance.get(field)
            for field in ("soul_id", "device_binding_id", "platform_account_id")
        ):
            yield ValidationError("history revision scope must exactly match its export envelope")
        if item.get("live_primary_payload_state") != state:
            yield ValidationError("history item payload state must match its envelope")
        traits = item.get("traits")
        if state == "retained":
            if revision.get("status") != "active" or not isinstance(traits, dict):
                yield ValidationError("retained history requires active revisions with raw traits")
            elif sorted(traits) != revision.get("trait_keys"):
                yield ValidationError("retained history trait keys must match its revision")
        else:
            expected_status = "deleted" if index == len(revisions) - 1 else "active"
            if revision.get("status") != expected_status or traits is not None:
                yield ValidationError("logical deletion requires active predecessors, one final tombstone, and no raw traits")


DpsDraft202012Validator = validators.extend(
    Draft202012Validator,
    {"x-dps-order": _ordinal_ascending, "x-dps-history-export": _history_export},
)


class PersonaRevisionSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(cls.schema)
        cls.validator = DpsDraft202012Validator(cls.schema, format_checker=FormatChecker())

    def payload(self):
        return {
            "schema_version": "1.0.0",
            "contract_id": "persona.revision/v1",
            "producer_module": "persona-store",
            "soul_id": "soul_" + "a" * 64,
            "device_binding_id": "db_" + "c" * 32,
            "platform_account_id": "pa_" + "d" * 32,
            "trace_id": "trace_" + "e" * 32,
            "idempotency_key": "idem_" + "f" * 64,
            "occurred_at": "2026-07-14T04:00:00Z",
            "privacy_class": "personal",
            "persona_revision": 1,
            "traits_sha256": "1" * 64,
            "trait_keys": ["curiosity", "tone"],
            "evidence_sha256": ["2" * 64, "3" * 64],
            "status": "active",
        }

    def assert_valid(self, payload):
        self.assertEqual([], list(self.validator.iter_errors(payload)))

    def assert_invalid(self, payload):
        self.assertNotEqual([], list(self.validator.iter_errors(payload)))

    def test_real_schema_accepts_exact_contract(self):
        self.assert_valid(self.payload())

    def test_unknown_member_fails_closed(self):
        payload = self.payload()
        payload["unexpected"] = True
        self.assert_invalid(payload)

    def test_every_required_member_is_actually_required(self):
        for field in self.schema["required"]:
            with self.subTest(field=field):
                payload = self.payload()
                del payload[field]
                self.assert_invalid(payload)

    def test_malformed_or_unknown_versions_fail_closed(self):
        for version in ("1.foo", "1.0.0.0", "2", "v1", "", "01.0", "1.+0", "1." + "0" * 31):
            with self.subTest(version=version):
                payload = self.payload()
                payload["schema_version"] = version
                self.assert_invalid(payload)

    def test_phone_like_scope_identifiers_are_not_contract_ids(self):
        for field, value in (
            ("device_binding_id", "db_60123456789"),
            ("platform_account_id", "pa_60123456789"),
            ("soul_id", "soul_60123456789"),
        ):
            with self.subTest(field=field):
                payload = self.payload()
                payload[field] = value
                self.assert_invalid(payload)

    def test_token_or_phone_like_operational_values_are_rejected(self):
        for field, value in (
            ("trace_id", "sk-live-not-a-trace-id"),
            ("trace_id", "trace_60123456789"),
            ("idempotency_key", "api-key-value"),
            ("idempotency_key", "60123456789"),
        ):
            with self.subTest(field=field):
                payload = self.payload()
                payload[field] = value
                self.assert_invalid(payload)

    def test_all_anchored_wire_strings_reject_trailing_newline(self):
        for field in (
            "schema_version",
            "soul_id",
            "device_binding_id",
            "platform_account_id",
            "trace_id",
            "idempotency_key",
            "occurred_at",
            "traits_sha256",
        ):
            with self.subTest(field=field):
                payload = self.payload()
                payload[field] += "\n"
                self.assert_invalid(payload)

        payload = self.payload()
        payload["evidence_sha256"][0] += "\n"
        self.assert_invalid(payload)

    def test_trait_and_evidence_arrays_are_strictly_ordinal_ascending(self):
        payload = self.payload()
        payload["trait_keys"] = list(reversed(payload["trait_keys"]))
        self.assert_invalid(payload)

        payload = self.payload()
        payload["evidence_sha256"] = list(reversed(payload["evidence_sha256"]))
        self.assert_invalid(payload)

    def test_timestamp_is_zero_offset_utc_2020_to_2199_with_at_most_seven_fraction_digits(self):
        for valid in (
            "2020-01-01T00:00:00Z",
            "2199-12-31T23:59:59.1234567+00:00",
        ):
            with self.subTest(valid=valid):
                payload = self.payload()
                payload["occurred_at"] = valid
                self.assert_valid(payload)
        for invalid in (
            "2019-12-31T23:59:59Z",
            "2200-01-01T00:00:00Z",
            "2026-07-14T04:00:00+01:00",
            "2026-07-14T04:00:00.12345678Z",
            "2026-7-14T04:00:00Z",
            "2026-07-14 04:00:00Z",
        ):
            with self.subTest(invalid=invalid):
                payload = self.payload()
                payload["occurred_at"] = invalid
                self.assert_invalid(payload)

    def test_revision_and_evidence_bounds_fail_closed(self):
        payload = self.payload()
        payload["persona_revision"] = 9223372036854775808
        self.assert_invalid(payload)
        payload = self.payload()
        payload["evidence_sha256"] = [f"{index:064x}" for index in range(65)]
        self.assert_invalid(payload)

    def test_duplicates_and_deleted_trait_keys_are_rejected(self):
        duplicate = self.payload()
        duplicate["evidence_sha256"] = ["2" * 64, "2" * 64]
        self.assert_invalid(duplicate)

        deleted = copy.deepcopy(self.payload())
        deleted["status"] = "deleted"
        self.assert_invalid(deleted)
        deleted["trait_keys"] = []
        self.assert_valid(deleted)

    def test_provider_owned_raw_corpus_has_exact_case_set_and_schema_codec_expectations(self):
        corpus = json.loads(CORPUS_PATH.read_text(encoding="utf-8"))
        self.assertEqual("persona.revision/v1", corpus["contract_id"])
        self.assertEqual("persona-store", corpus["owner_module"])
        case_ids = [case["id"] for case in corpus["cases"]]
        self.assertEqual(list(EXPECTED_CORPUS_CASE_IDS), case_ids)

        for case in corpus["cases"]:
            with self.subTest(case_id=case["id"]):
                expected = "accept" if case["valid"] else "reject"
                self.assertEqual(expected, case["expectations"]["dotnet_strict_codec"])
                schema_expectation = case["expectations"]["draft2020"]
                self.assertIn(schema_expectation, (expected, "reject-before-schema"))
                try:
                    payload = _load_unique_json(case["raw_json"])
                except ValueError:
                    self.assertFalse(case["valid"])
                    self.assertEqual("reject-before-schema", schema_expectation)
                    continue
                errors = list(self.validator.iter_errors(payload))
                self.assertEqual(case["valid"], not errors, errors)


class PersonaHistoryExportSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.revision_schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        cls.schema = json.loads(HISTORY_EXPORT_SCHEMA_PATH.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(cls.schema)
        registry = Registry().with_resource(
            cls.revision_schema["$id"],
            Resource.from_contents(cls.revision_schema),
        )
        cls.validator = DpsDraft202012Validator(
            cls.schema,
            format_checker=FormatChecker(),
            registry=registry,
        )

    def test_provider_owned_history_export_corpus_has_exact_case_set_and_dual_expectations(self):
        corpus = json.loads(HISTORY_EXPORT_CORPUS_PATH.read_text(encoding="utf-8"))
        self.assertEqual("persona.history.export/v1", corpus["contract_id"])
        self.assertEqual("persona-store", corpus["owner_module"])
        self.assertEqual(list(EXPECTED_HISTORY_EXPORT_CASE_IDS), [case["id"] for case in corpus["cases"]])

        for case in corpus["cases"]:
            with self.subTest(case_id=case["id"]):
                expected = "accept" if case["valid"] else "reject"
                self.assertEqual(expected, case["expectations"]["dotnet_strict_codec"])
                schema_expectation = case["expectations"]["draft2020"]
                self.assertIn(schema_expectation, (expected, "reject-before-schema"))
                try:
                    payload = _load_unique_json(case["raw_json"])
                except ValueError:
                    self.assertFalse(case["valid"])
                    self.assertEqual("reject-before-schema", schema_expectation)
                    continue
                errors = list(self.validator.iter_errors(payload))
                self.assertEqual(case["valid"], not errors, errors)


if __name__ == "__main__":
    unittest.main()
