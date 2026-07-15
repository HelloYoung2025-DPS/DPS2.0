from __future__ import annotations

import copy
import json
import unittest
from datetime import datetime, timezone
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


MODULE_ROOT = Path(__file__).resolve().parents[1]
PROVIDED = MODULE_ROOT / "contracts/provided"
CONTRACTS = (
    (
        "approval.decision/v1",
        PROVIDED / "approval.decision.v1.schema.json",
        PROVIDED / "approval.decision.v1.corpus.json",
    ),
    (
        "action.execution.promotion/v1",
        PROVIDED / "action.execution.promotion.v1.schema.json",
        PROVIDED / "action.execution.promotion.v1.corpus.json",
    ),
    (
        "approval.execution.fence/v1",
        PROVIDED / "approval.execution.fence.v1.schema.json",
        PROVIDED / "approval.execution.fence.v1.corpus.json",
    ),
    (
        "approval.submission.intent/v1",
        PROVIDED / "approval.submission.intent.v1.schema.json",
        PROVIDED / "approval.submission.intent.v1.corpus.json",
    ),
    (
        "approval.submission.acknowledgement/v1",
        PROVIDED / "approval.submission.acknowledgement.v1.schema.json",
        PROVIDED / "approval.submission.acknowledgement.v1.corpus.json",
    ),
    (
        "approval.submission.reconciliation/v1",
        PROVIDED / "approval.submission.reconciliation.v1.schema.json",
        PROVIDED / "approval.submission.reconciliation.v1.corpus.json",
    ),
    (
        "approval.submission.recovery/v1",
        PROVIDED / "approval.submission.recovery.v1.schema.json",
        PROVIDED / "approval.submission.recovery.v1.corpus.json",
    ),
    (
        "approval.submission.state/v1",
        PROVIDED / "approval.submission.state.v1.schema.json",
        PROVIDED / "approval.submission.state.v1.corpus.json",
    ),
)

EXPECTED_CASE_IDS = {
    "action.execution.promotion/v1": frozenset(
        {
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
        }
    ),
    "approval.decision/v1": frozenset(
        {
            "valid-denied",
            "valid-approved-side-effect",
            "unknown-field",
            "missing-platform-authorization-field",
            "schema-version-major-only",
            "schema-version-leading-zero",
            "approval-uuid-uppercase",
            "approval-uuid-zero",
            "proposal-uuid-braced",
            "device-id-newline",
            "occurred-offset",
            "occurred-trailing-zero-fraction",
            "occurred-too-precise",
            "occurred-invalid-calendar",
            "occurred-year-zero",
            "valid-canonical-fraction",
            "policy-version-leading-zero",
            "policy-id-newline",
            "observe-unexpected-parameter",
            "denied-without-reason",
            "approved-shadow",
            "approved-side-effect-without-platform-authorization",
        }
    ),
    "approval.execution.fence/v1": frozenset(
        {
            "valid",
            "valid-canonical-fraction",
            "valid-int64-maximum",
            "unknown-field",
            "missing-privacy-class",
            "legacy-acquired-at-field",
            "schema-version-major-only",
            "schema-version-leading-zero",
            "fence-uuid-uppercase",
            "fence-uuid-zero",
            "proposal-uuid-braced",
            "trace-newline",
            "status-zero",
            "runtime-int64-overflow",
            "occurred-offset",
            "occurred-trailing-zero-fraction",
            "valid-until-invalid-calendar",
            "occurred-year-zero",
            "valid-until-equals-occurred",
            "valid-until-before-occurred",
            "lifetime-over-two-seconds",
            "approval-newline",
            "approval-carriage-return",
            "approval-space",
            "approval-uppercase",
            "approval-short",
            "approval-long",
            "runtime-newline",
            "runtime-carriage-return",
            "runtime-space",
            "runtime-uppercase",
            "runtime-short",
            "runtime-long",
            "release-newline",
            "release-carriage-return",
            "release-space",
            "release-uppercase",
            "release-short",
            "release-long",
        }
    ),
    "approval.submission.intent/v1": frozenset(
        {"valid", "unknown-field", "missing-attempt-id", "attempt-zero", "bom-generation-zero", "uppercase-request-hash"}
    ),
    "approval.submission.acknowledgement/v1": frozenset(
        {"valid", "unknown-field", "missing-pending-state", "attempt-four", "empty-native-submission", "submitted-request-short"}
    ),
    "approval.submission.reconciliation/v1": frozenset(
        {"valid-not-submitted", "valid-submitted", "unknown-finding", "wrong-role", "missing-evidence", "unknown-field"}
    ),
    "approval.submission.recovery/v1": frozenset(
        {"valid", "wrong-next-attempt", "missing-human-approval", "wrong-role", "same-attempt-zero", "unknown-field"}
    ),
    "approval.submission.state/v1": frozenset(
        {"valid-pending", "valid-acknowledged", "valid-unknown", "unknown-state", "pending-with-predecessor", "ack-without-predecessor", "unknown-field"}
    ),
}


def _format_checker() -> FormatChecker:
    checker = FormatChecker()

    def exact_rfc3339_datetime(value: object) -> bool:
        if not isinstance(value, str) or not value.endswith("Z"):
            return False
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
        return (
            parsed.tzinfo is not None
            and parsed.utcoffset() == timezone.utc.utcoffset(parsed)
        )

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


class PolicyApprovalProvidedSchemaTests(unittest.TestCase):
    def test_native_stop_contract_sources_are_not_duplicated(self) -> None:
        forbidden = (
            "native.stop.proof.v1.schema.json",
            "native.stop.proof.v1.corpus.json",
            "native.stop.proof.v2.schema.json",
            "native.stop.proof.v2.corpus.json",
            "native.stop.challenge.v1.schema.json",
            "native.stop.challenge.v1.corpus.json",
        )
        for name in forbidden:
            with self.subTest(path=name):
                self.assertFalse((PROVIDED / name).exists())
        contract_project = (
            PROVIDED
            / "Dps.PolicyApproval.Contracts"
            / "Dps.PolicyApproval.Contracts.csproj"
        ).read_text(encoding="utf-8")
        self.assertNotIn("native.stop", contract_project)

    def test_all_owned_schemas_are_draft_2020_12(self) -> None:
        for _, schema_path, _ in CONTRACTS:
            with self.subTest(schema=schema_path.name):
                schema = _load(schema_path)
                self.assertEqual(
                    "https://json-schema.org/draft/2020-12/schema",
                    schema["$schema"],
                )
                Draft202012Validator.check_schema(schema)

    def test_promotion_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[1])

    def test_decision_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[0])

    def test_fence_shared_corpus(self) -> None:
        self._assert_corpus(CONTRACTS[2])

    def test_submission_lifecycle_shared_corpora(self) -> None:
        for contract in CONTRACTS[3:]:
            self._assert_corpus(contract)

    def _assert_corpus(self, contract: tuple[str, Path, Path]) -> None:
        contract_id, schema_path, corpus_path = contract
        validator = _validator(schema_path)
        corpus = _load(corpus_path)
        self.assertEqual(contract_id, corpus["contractId"])
        cases = corpus["cases"]
        assert isinstance(cases, list)
        case_ids = [case["id"] for case in cases]
        expected_case_ids = EXPECTED_CASE_IDS[contract_id]
        self.assertEqual(len(expected_case_ids), len(case_ids))
        self.assertEqual(len(case_ids), len(set(case_ids)), "duplicate corpus case ID")
        self.assertEqual(expected_case_ids, frozenset(case_ids))
        for case in cases:
            assert isinstance(case, dict)
            with self.subTest(contract=contract_id, case=case["id"]):
                observed = validator.is_valid(_apply_case(corpus, case))
                self.assertEqual(case["schemaValid"], observed)


if __name__ == "__main__":
    unittest.main()
