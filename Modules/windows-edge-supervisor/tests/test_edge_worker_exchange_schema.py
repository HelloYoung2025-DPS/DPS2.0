import copy
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


MODULE_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = MODULE_ROOT.parents[1]
CONTRACT_ROOT = MODULE_ROOT / "contracts" / "provided"


def validator_for(schema_path: Path) -> Draft202012Validator:
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=FormatChecker())


def assert_corpus(test: unittest.TestCase, corpus_path: Path) -> int:
    corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
    test.assertEqual("dps.contract-corpus/v1", corpus["corpus_version"])
    schema_path = corpus_path.parent / corpus["schema_file"]
    validator = validator_for(schema_path)
    if "base_file" in corpus:
        base = json.loads((corpus_path.parent / corpus["base_file"]).read_text(encoding="utf-8"))
    else:
        base = corpus["base_instance"]
    case_ids = [case["id"] for case in corpus["cases"]]
    test.assertEqual(len(case_ids), len(set(case_ids)))
    for case in corpus["cases"]:
        with test.subTest(corpus=corpus_path.name, case=case["id"]):
            instance = copy.deepcopy(base)
            instance.update(case["patch"])
            errors = list(validator.iter_errors(instance))
            test.assertEqual(case["expected"] == "FAIL", bool(errors))
    return len(case_ids)


class EdgeWorkerExchangeSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.schema = json.loads(
            (CONTRACT_ROOT / "edge.worker.exchange.v1.schema.json").read_text(encoding="utf-8")
        )
        Draft202012Validator.check_schema(cls.schema)
        cls.validator = Draft202012Validator(cls.schema, format_checker=FormatChecker())

    def test_three_production_exchange_golden_envelopes_validate_and_legacy_drain_fails(self) -> None:
        for kind in ("command", "receipt", "health"):
            with self.subTest(kind=kind):
                self.validator.validate(self._fixture(kind))
        self.assertTrue(list(self.validator.iter_errors(self._fixture("drain"))))

    def test_owner_corpora_cover_worker_directive_and_capability_boundaries(self) -> None:
        total = 0
        for name in (
            "edge.worker.exchange.v1.corpus.json",
            "edge.worker.drain.directive.v1.corpus.json",
            "edge.worker.drain.receipt.v1.corpus.json",
            "edge.bridge.directive.v1.corpus.json",
            "edge.capability.evidence.v1.corpus.json",
        ):
            total += assert_corpus(self, CONTRACT_ROOT / name)
        self.assertEqual(64, total)

    def test_directive_golden_and_capability_signature_shape_are_owned_and_strict(self) -> None:
        directive_schema = json.loads(
            (CONTRACT_ROOT / "edge.bridge.directive.v1.schema.json").read_text(encoding="utf-8")
        )
        self.assertEqual(
            "edge.bridge.directive.v1.auth.json", directive_schema["x-dps-auth-spec"]
        )
        validator_for(CONTRACT_ROOT / "edge.bridge.directive.v1.schema.json").validate(
            json.loads(
                (CONTRACT_ROOT / "edge.bridge.directive.v1.golden.json").read_text(
                    encoding="utf-8"
                )
            )
        )
        capability_schema = json.loads(
            (CONTRACT_ROOT / "edge.capability.evidence.v1.schema.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertNotIn("attestation_verified", capability_schema["properties"])
        self.assertEqual(
            "edge.capability.evidence.v1.attestation.json",
            capability_schema["x-dps-attestation-spec"],
        )
        self.assertIn("attestation_algorithm", capability_schema["required"])
        self.assertIn("attestation_signature", capability_schema["required"])
        self.assertEqual(
            ["RSA_PSS_SHA256", None],
            capability_schema["properties"]["attestation_algorithm"]["enum"],
        )
        drain_schema = json.loads(
            (CONTRACT_ROOT / "edge.worker.drain.receipt.v1.schema.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(
            "edge.worker.drain.receipt.v1.auth.json",
            drain_schema["x-dps-auth-spec"],
        )
        drain_auth = json.loads(
            (CONTRACT_ROOT / drain_schema["x-dps-auth-spec"]).read_text(encoding="utf-8")
        )
        self.assertEqual(
            "edge.worker.drain.directive.v1.auth.json",
            json.loads(
                (CONTRACT_ROOT / "edge.worker.drain.directive.v1.schema.json").read_text(
                    encoding="utf-8"
                )
            )["x-dps-auth-spec"],
        )
        directive_auth = json.loads(
            (CONTRACT_ROOT / "edge.worker.drain.directive.v1.auth.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(23, len(directive_auth["statement"]["fields"]))
        expected_pss = {
            "message_hash": "SHA-256",
            "mask_generation_function": "MGF1",
            "mgf1_hash": "SHA-256",
            "salt_length_bytes": 32,
            "trailer_field": 1,
        }
        self.assertEqual(expected_pss, directive_auth["rsa_pss_parameters"])
        self.assertEqual(2048, directive_auth["minimum_rsa_modulus_bits"])
        self.assertEqual(expected_pss, drain_auth["rsa_pss_parameters"])
        self.assertEqual(2048, drain_auth["minimum_rsa_modulus_bits"])
        self.assertEqual(
            [
                "drain_id", "intake_stopped", "journal_artifact_sha256",
                "protected_policy_sha256", "release_bom_sha256", "remaining_in_flight",
                "routing_epoch", "schema_version", "slot", "worker_artifact_sha256",
                "worker_drained", "worker_receipt_wire_sha256", "worker_version",
            ],
            drain_auth["journal_payload"]["fields_in_exact_wire_order"],
        )
        self.assertEqual(True, drain_schema["properties"]["intake_stopped"]["const"])
        self.assertNotIn("journal_receipt", drain_schema["properties"])
        self.assertNotIn("journal_signature", drain_schema["properties"])

    def test_consumed_zenno_exchange_uses_the_owner_schema_and_corpus(self) -> None:
        owner_root = REPOSITORY_ROOT / "Modules" / "zenno-bridge" / "contracts" / "provided"
        count = assert_corpus(self, owner_root / "edge.bridge.exchange.v1.corpus.json")
        self.assertEqual(9, count)

    def test_receipt_truth_table_rejects_false_success_unknown_and_failed_claims(self) -> None:
        receipt = self._fixture("receipt")
        invalid_updates = (
            {"dispatch_acknowledged": False},
            {"native_status": "FAILED"},
            {"postcondition_verified": False},
            {"retry_allowed": True},
            {
                "result_status": "UNKNOWN_OUTCOME",
                "dispatch_acknowledged": False,
                "native_status": "UNKNOWN_OUTCOME",
                "postcondition_verified": None,
            },
            {
                "result_status": "FAILED",
                "dispatch_acknowledged": None,
                "native_status": "FAILED",
                "postcondition_verified": False,
            },
            {
                "result_status": "IN_PROGRESS",
                "dispatch_acknowledged": None,
                "native_status": None,
                "postcondition_verified": None,
                "duplicate": True,
                "retry_allowed": True,
            },
        )
        for update in invalid_updates:
            with self.subTest(update=update):
                invalid = copy.deepcopy(receipt)
                invalid.update(update)
                self.assertTrue(list(self.validator.iter_errors(invalid)))

    def test_unknown_missing_and_mismatched_fields_fail_closed(self) -> None:
        command = self._fixture("command")
        unknown = copy.deepcopy(command)
        unknown["unexpected"] = True
        missing = copy.deepcopy(command)
        del missing["retry_allowed"]
        mismatched = copy.deepcopy(command)
        mismatched["step_kind"] = "TAP_SELECTOR"
        health_with_hash = self._fixture("health")
        health_with_hash["request_sha256"] = "0" * 64
        legacy_free_text_drain = self._fixture("drain")
        for invalid in (unknown, missing, mismatched, health_with_hash, legacy_free_text_drain):
            self.assertTrue(list(self.validator.iter_errors(invalid)))

    def test_security_patterns_are_exact_and_ecmascript_terminal(self) -> None:
        expectations = {
            "edge.bridge.directive.v1.schema.json": {
                "auth_key_id": (71, 71),
                "auth_nonce": (64, 64),
                "auth_body_sha256": (64, 64),
            },
            "edge.capability.evidence.v1.schema.json": {
                "raw_evidence_sha256": (64, 64),
                "attestation_key_id": (71, 71),
                "peer_auth_key_id": (71, 71),
                "release_bom_sha256": (64, 64),
                "protected_policy_sha256": (64, 64),
                "worker_artifact_sha256": (64, 64),
            },
        }
        for schema_name, fields in expectations.items():
            schema = json.loads((CONTRACT_ROOT / schema_name).read_text(encoding="utf-8"))
            for field, lengths in fields.items():
                with self.subTest(schema=schema_name, field=field):
                    node = schema["properties"][field]
                    self.assertEqual(lengths, (node["minLength"], node["maxLength"]))
                    self.assertTrue(node["pattern"].endswith("$(?![\\s\\S])"))

    @staticmethod
    def _fixture(kind: str) -> dict:
        return json.loads(
            (CONTRACT_ROOT / f"edge.worker.exchange.v1.{kind}.golden.json").read_text(
                encoding="utf-8"
            )
        )


if __name__ == "__main__":
    unittest.main()
