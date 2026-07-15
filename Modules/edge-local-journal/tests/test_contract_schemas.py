import copy
import hashlib
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


CONTRACT_ROOT = Path(__file__).resolve().parents[1] / "contracts" / "provided"


class EdgeJournalContractSchemaTests(unittest.TestCase):
    def test_provider_owned_corpora_are_exact_and_fail_closed(self) -> None:
        total = 0
        for corpus_name in (
            "edge.journal.append.v1.corpus.json",
            "edge.journal.receipt.v1.corpus.json",
            "edge.journal.drain.attestation.v1.corpus.json",
        ):
            corpus_path = CONTRACT_ROOT / corpus_name
            corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
            self.assertEqual("dps.contract-corpus/v1", corpus["corpus_version"])
            schema = json.loads(
                (CONTRACT_ROOT / corpus["schema_file"]).read_text(encoding="utf-8")
            )
            Draft202012Validator.check_schema(schema)
            validator = Draft202012Validator(schema, format_checker=FormatChecker())
            case_ids = [case["id"] for case in corpus["cases"]]
            self.assertEqual(len(case_ids), len(set(case_ids)))
            total += len(case_ids)
            for case in corpus["cases"]:
                with self.subTest(corpus=corpus_name, case=case["id"]):
                    instance = copy.deepcopy(corpus["base_instance"])
                    instance.update(case["patch"])
                    errors = list(validator.iter_errors(instance))
                    self.assertEqual(case["expected"] == "FAIL", bool(errors))
        self.assertEqual(48, total)

    def test_receipt_sequence_preserves_raw_int64_boundaries(self) -> None:
        corpus = json.loads(
            (CONTRACT_ROOT / "edge.journal.receipt.v1.corpus.json").read_text(
                encoding="utf-8"
            )
        )
        values = {case["id"]: case["patch"].get("sequence") for case in corpus["cases"]}
        self.assertEqual(9223372036854775807, values["sequence-int64-max"])
        self.assertEqual(9223372036854775808, values["sequence-int64-overflow"])
        self.assertNotEqual(
            float(values["sequence-int64-max"]), values["sequence-int64-max"]
        )

    def test_security_fields_have_exact_lengths_and_absolute_ends(self) -> None:
        expectations = {
            "edge.journal.append.v1.schema.json": ("payload_sha256",),
            "edge.journal.receipt.v1.schema.json": (
                "payload_sha256",
                "previous_checksum",
                "entry_checksum",
            ),
            "edge.journal.drain.attestation.v1.schema.json": (
                "entry_checksum",
                "entry_payload_sha256",
                "journal_file_sha256",
                "journal_file_identity_sha256",
                "journal_head_checksum",
                "entry_set_sha256",
                "state_artifact_set_sha256",
                "worker_artifact_sha256",
                "journal_artifact_sha256",
                "release_bom_sha256",
                "protected_policy_sha256",
                "worker_receipt_wire_sha256",
                "journal_receipt_sha256",
                "statement_sha256",
            ),
        }
        for schema_name, fields in expectations.items():
            schema = json.loads((CONTRACT_ROOT / schema_name).read_text(encoding="utf-8"))
            for field in fields:
                with self.subTest(schema=schema_name, field=field):
                    node = schema["properties"][field]
                    if "$ref" in node:
                        node = schema["$defs"][node["$ref"].rsplit("/", 1)[-1]]
                    self.assertEqual((64, 64), (node["minLength"], node["maxLength"]))
                    self.assertTrue(node["pattern"].endswith("$(?![\\s\\S])"))

    def test_command_and_entry_ids_are_safe_ascii_tokens(self) -> None:
        expected = {
            "command_id": (128, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$(?![\\s\\S])"),
            "entry_id": (160, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,159}$(?![\\s\\S])"),
        }
        for schema_name in (
            "edge.journal.append.v1.schema.json",
            "edge.journal.receipt.v1.schema.json",
        ):
            schema = json.loads((CONTRACT_ROOT / schema_name).read_text(encoding="utf-8"))
            for field, (maximum, pattern) in expected.items():
                with self.subTest(schema=schema_name, field=field):
                    node = schema["properties"][field]
                    self.assertEqual((1, maximum), (node["minLength"], node["maxLength"]))
                    self.assertEqual(pattern, node["pattern"])

    def test_checksum_profile_is_domain_separated_and_length_prefixed(self) -> None:
        profile = json.loads(
            (CONTRACT_ROOT / "edge.journal.checksum.v1.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual("edge.journal.checksum/v1", profile["profile_id"])
        self.assertEqual("dps.length-prefixed-utf8/v1", profile["encoding"])
        self.assertEqual("big-endian", profile["byte_order"])
        self.assertEqual(
            [
                "domain_byte_length:u32",
                "domain:utf8",
                "field_count:u32",
                "repeat(field_byte_length:u32,field:utf8)",
            ],
            profile["layout"],
        )
        identity = profile["domains"]["identity_sha256"]
        entry = profile["domains"]["entry_checksum"]
        self.assertNotEqual(identity["domain"], entry["domain"])
        self.assertEqual(15, len(identity["fields"]))
        self.assertEqual(18, len(entry["fields"]))
        self.assertEqual("checksum_encoding", entry["fields"][-2])
        self.assertEqual(
            "dps.length-prefixed-utf8/v1",
            profile["journal_line_discriminator"]["value"],
        )
        self.assertEqual(
            "reject", profile["journal_line_discriminator"]["missing_or_unknown"]
        )

    def test_drain_auth_profile_has_independent_worker_wire_and_golden_bytes(self) -> None:
        profile = json.loads(
            (CONTRACT_ROOT / "edge.journal.drain.attestation.v1.auth.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertNotIn("supervisor_compatibility", profile)
        correlation = profile["independent_worker_wire_correlation"]
        self.assertEqual(
            "windows-edge-supervisor", correlation["worker_contract_owner"]
        )
        self.assertEqual(
            "edge.worker.drain.receipt/v1", correlation["worker_contract_id"]
        )
        self.assertEqual(
            "worker_receipt_wire_sha256", correlation["opaque_field"]
        )
        self.assertIn("never parses", correlation["journal_rule"])
        self.assertIn("independently verifies", correlation["supervisor_rule"])
        self.assertIn("rejects equal normalized", correlation["key_separation_rule"])
        self.assertEqual(49, len(profile["rich_owner_statement"]["fields"]))
        self.assertEqual(
            ["statement_sha256", "signature"],
            profile["rich_owner_statement"]["proof_fields_outside_statement"],
        )
        self.assertEqual(
            {
                "message_hash": "SHA-256",
                "mask_generation_function": "MGF1",
                "mgf1_hash": "SHA-256",
                "salt_length_bytes": 32,
                "trailer_field": 1,
            },
            profile["rsa_pss_parameters"],
        )
        vector = profile["golden_framing_vector"]
        encoded = bytes.fromhex(vector["encoded_utf8_hex"])
        expected = vector["domain"].encode("utf-8") + b"\n"
        for value in vector["fields"]:
            field = value.encode("utf-8")
            expected += str(len(field)).encode("ascii") + b":" + field + b";"
        self.assertEqual(expected, encoded)
        self.assertEqual(vector["sha256"], hashlib.sha256(encoded).hexdigest())


if __name__ == "__main__":
    unittest.main()
