from __future__ import annotations

import copy
import json
import unittest
from datetime import datetime, timezone
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


MODULE_ROOT = Path(__file__).resolve().parents[1]
PROVIDED = MODULE_ROOT / "contracts/provided"
POLICY_PROVIDED = MODULE_ROOT.parent / "policy-approval/contracts/provided"
CONTRACTS = (
    (
        "action.execution.promotion/v1",
        POLICY_PROVIDED / "action.execution.promotion.v1.schema.json",
        POLICY_PROVIDED / "action.execution.promotion.v1.corpus.json",
    ),
    (
        "control.plane.receipt/v1",
        PROVIDED / "control.plane.receipt.v1.schema.json",
        PROVIDED / "control.plane.receipt.v1.corpus.json",
    ),
    (
        "approval.submission.reconciliation/v1",
        POLICY_PROVIDED / "approval.submission.reconciliation.v1.schema.json",
        POLICY_PROVIDED / "approval.submission.reconciliation.v1.corpus.json",
    ),
    (
        "approval.submission.recovery/v1",
        POLICY_PROVIDED / "approval.submission.recovery.v1.schema.json",
        POLICY_PROVIDED / "approval.submission.recovery.v1.corpus.json",
    ),
    (
        "approval.submission.state/v1",
        POLICY_PROVIDED / "approval.submission.state.v1.schema.json",
        POLICY_PROVIDED / "approval.submission.state.v1.corpus.json",
    ),
)
EXPECTED_CASE_IDS = {
    "action.execution.promotion/v1": {
        "valid",
        "additional-field",
        "missing-field",
        "uppercase-uuid",
        "zero-uuid",
        "offset-not-zulu",
        "invalid-calendar-date",
        "year-zero",
        "fractional-trailing-zero",
        "trailing-newline-id",
        "trailing-newline-proposal-sha",
        "trailing-carriage-return-release-bom-sha",
        "digest-space",
        "digest-uppercase",
        "digest-short",
        "digest-long",
        "unknown-contract",
        "fractional-revision",
        "revision-over-int64",
        "noncanonical-base64-pad-bits",
        "lifetime-over-five-minutes",
    },
    "control.plane.receipt/v1": {
        "valid",
        "additional-field",
        "missing-occurred-at",
        "unknown-major",
        "offset-not-zulu",
        "invalid-calendar-date",
        "fractional-trailing-zero",
        "trailing-newline-id",
        "wrong-owner-pair",
        "uppercase-payload-hash",
    },
    "approval.submission.reconciliation/v1": {
        "valid-not-submitted",
        "valid-submitted",
        "unknown-finding",
        "wrong-role",
        "missing-evidence",
        "unknown-field",
    },
    "approval.submission.recovery/v1": {
        "valid",
        "wrong-next-attempt",
        "missing-human-approval",
        "wrong-role",
        "same-attempt-zero",
        "unknown-field",
    },
    "approval.submission.state/v1": {
        "valid-pending",
        "valid-acknowledged",
        "valid-unknown",
        "unknown-state",
        "pending-with-predecessor",
        "ack-without-predecessor",
        "unknown-field",
    },
}


def _format_checker() -> FormatChecker:
    checker = FormatChecker()

    def exact_rfc3339_datetime(value: object) -> bool:
        if not isinstance(value, str) or not value.endswith("Z"):
            return False
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
        return parsed.tzinfo is not None and parsed.utcoffset() == timezone.utc.utcoffset(parsed)

    checker.checkers = dict(checker.checkers)
    checker.checkers["date-time"] = (exact_rfc3339_datetime, (ValueError,))
    return checker


def _load(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8"))


def _validator(schema_path: Path) -> Draft202012Validator:
    schema = _load(schema_path)
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=_format_checker())


def _apply_case(corpus: dict[str, object], case: dict[str, object]) -> dict[str, object]:
    instance = copy.deepcopy(corpus["base"])
    assert isinstance(instance, dict)
    patch = case["patch"]
    assert isinstance(patch, dict)
    instance.update(patch)
    remove = case["remove"]
    assert isinstance(remove, list)
    for field in remove:
        instance.pop(field, None)
    return instance


class ProvidedContractSchemaTests(unittest.TestCase):
    """Exactly sixteen fail-closed tests; candidate policy binds this floor."""

    def test_01_both_schemas_are_draft_2020_12(self) -> None:
        for _, schema_path, _ in CONTRACTS:
            with self.subTest(schema=schema_path.name):
                schema = _load(schema_path)
                self.assertEqual("https://json-schema.org/draft/2020-12/schema", schema["$schema"])
                Draft202012Validator.check_schema(schema)

    def test_02_promotion_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[0])

    def test_03_receipt_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[1])

    def test_04_promotion_required_fields_are_exact(self) -> None:
        schema = _load(CONTRACTS[0][1])
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(set(schema["properties"]), set(schema["required"]))

    def test_05_receipt_required_fields_are_exact(self) -> None:
        schema = _load(CONTRACTS[1][1])
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(set(schema["properties"]), set(schema["required"]))

    def test_06_promotion_revision_is_int64_bounded(self) -> None:
        validator = _validator(CONTRACTS[0][1])
        corpus = _load(CONTRACTS[0][2])
        valid = copy.deepcopy(corpus["base"])
        valid["expected_runtime_revision"] = 9223372036854775807
        self.assertTrue(validator.is_valid(valid))
        valid["expected_runtime_revision"] = 9223372036854775808
        self.assertFalse(validator.is_valid(valid))

    def test_07_promotion_timestamp_is_canonical_zulu(self) -> None:
        validator = _validator(CONTRACTS[0][1])
        corpus = _load(CONTRACTS[0][2])
        valid = copy.deepcopy(corpus["base"])
        valid["occurred_at"] = "2026-07-14T00:00:00.12Z"
        self.assertTrue(validator.is_valid(valid))
        valid["occurred_at"] = "2026-07-14T00:00:00.1200Z"
        self.assertFalse(validator.is_valid(valid))

    def test_08_receipt_timestamp_is_canonical_zulu(self) -> None:
        validator = _validator(CONTRACTS[1][1])
        corpus = _load(CONTRACTS[1][2])
        valid = copy.deepcopy(corpus["base"])
        valid["occurred_at"] = "2026-07-14T00:00:00.1Z"
        self.assertTrue(validator.is_valid(valid))
        valid["occurred_at"] = "2026-07-14T00:00:00+00:00"
        self.assertFalse(validator.is_valid(valid))

    def test_09_receipt_source_owner_pairs_are_closed(self) -> None:
        validator = _validator(CONTRACTS[1][1])
        corpus = _load(CONTRACTS[1][2])
        valid = copy.deepcopy(corpus["base"])
        pairs = {
            "device.registered/v1": "device-registry",
            "platform.account.authorized/v1": "platform-account-registry",
            "identity.binding/v1": "binding",
            "persona.revision/v1": "persona-store",
            "soul.memory.readback/v1": "soul-memory-adapter",
        }
        for contract_id, producer in pairs.items():
            with self.subTest(contract_id=contract_id):
                valid["source_contract_id"] = contract_id
                valid["source_producer_module"] = producer
                self.assertTrue(validator.is_valid(valid))
                valid["source_producer_module"] = "planner"
                self.assertFalse(validator.is_valid(valid))

    def test_10_receipt_identifiers_are_terminal_lowercase_hex(self) -> None:
        validator = _validator(CONTRACTS[1][1])
        corpus = _load(CONTRACTS[1][2])
        invalid = copy.deepcopy(corpus["base"])
        invalid["trace_id"] += "\n"
        self.assertFalse(validator.is_valid(invalid))
        invalid = copy.deepcopy(corpus["base"])
        invalid["source_payload_sha256"] = str(invalid["source_payload_sha256"]).upper()
        self.assertFalse(validator.is_valid(invalid))

    def test_11_promotion_signature_has_canonical_pad_bits(self) -> None:
        validator = _validator(CONTRACTS[0][1])
        corpus = _load(CONTRACTS[0][2])
        invalid = copy.deepcopy(corpus["base"])
        invalid["signature_base64"] = (
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            "AAAAAAAAAAAAAAAAAAAAAB=="
        )
        self.assertFalse(validator.is_valid(invalid))

    def test_12_reconciliation_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[2])

    def test_13_recovery_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[3])

    def test_14_submission_state_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[4])

    def test_15_lifecycle_required_fields_are_exact(self) -> None:
        for _, schema_path, _ in CONTRACTS[2:]:
            with self.subTest(schema=schema_path.name):
                schema = _load(schema_path)
                self.assertFalse(schema["additionalProperties"])
                self.assertEqual(set(schema["properties"]), set(schema["required"]))

    def test_16_lifecycle_ownership_and_authority_are_fixed(self) -> None:
        reconciliation = _load(CONTRACTS[2][1])["properties"]
        recovery = _load(CONTRACTS[3][1])["properties"]
        state = _load(CONTRACTS[4][1])["properties"]
        self.assertEqual("control-plane-host", reconciliation["producer_module"]["const"])
        self.assertEqual("independent-reconciler", reconciliation["authority_role"]["const"])
        self.assertEqual("control-plane-host", recovery["producer_module"]["const"])
        self.assertEqual("human-release-approver", recovery["authority_role"]["const"])
        self.assertEqual("policy-approval", state["producer_module"]["const"])

    def _assert_corpus(
        self,
        contract: tuple[str, Path, Path],
    ) -> None:
        contract_id, schema_path, corpus_path = contract
        validator = _validator(schema_path)
        corpus = _load(corpus_path)
        self.assertEqual(contract_id, corpus["contractId"])
        cases = corpus["cases"]
        assert isinstance(cases, list)
        case_ids = [case["id"] for case in cases if isinstance(case, dict)]
        self.assertEqual(len(cases), len(case_ids))
        self.assertEqual(len(case_ids), len(set(case_ids)))
        self.assertEqual(EXPECTED_CASE_IDS[contract_id], set(case_ids))
        for case in cases:
            assert isinstance(case, dict)
            with self.subTest(contract=contract_id, case=case["id"]):
                observed = validator.is_valid(_apply_case(corpus, case))
                self.assertEqual(case["schemaValid"], observed)


def load_tests(
    loader: unittest.TestLoader,
    tests: unittest.TestSuite,
    pattern: str | None,
) -> unittest.TestSuite:
    del loader, pattern
    observed_floor = tests.countTestCases()
    if observed_floor != 16:
        raise RuntimeError(
            "control-plane-host.contract-schema floor mismatch: "
            f"expected=16 observed={observed_floor}"
        )
    return tests


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ProvidedContractSchemaTests)
    observed_floor = suite.countTestCases()
    if observed_floor != 16:
        raise SystemExit(
            f"control-plane-host.contract-schema floor mismatch: expected=16 observed={observed_floor}"
        )
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    raise SystemExit(0 if result.wasSuccessful() else 1)
