import copy
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).parents[1]
SHA = "a" * 64
DEVICE_ID = "db_" + "1" * 32
ACCOUNT_ID = "pa_" + "2" * 32
TRACE_ID = "trace_" + "3" * 32
IDEMPOTENCY_KEY = "idem_" + "4" * 64
SOUL_ID = "soul_" + "5" * 64


def load(name):
    return json.loads((ROOT / "contracts" / "provided" / name).read_text(encoding="utf-8"))


def build_request():
    return {
        "schema_version": "1.0.0",
        "contract_id": "artifact.build.request/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": DEVICE_ID,
        "platform_account_id": ACCOUNT_ID,
        "trace_id": TRACE_ID,
        "idempotency_key": IDEMPOTENCY_KEY,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "build_id": "build-001",
        "module_id": "module-one",
        "module_version": "1.0.0",
        "integration_commit": "b" * 40,
        "artifact_path": "Modules/module-one/src/payload.bin",
        "expected_sha256": SHA,
        "merge_decision_id": "merge-" + "c" * 32,
    }


def descriptor():
    return {
        "schema_version": "1.0.0",
        "contract_id": "artifact.descriptor/v1",
        "producer_module": "factory-artifact-builder",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": TRACE_ID,
        "idempotency_key": "idem_" + SHA,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "artifact_id": "artifact-" + "d" * 32,
        "build_id": "build-001",
        "module_id": "module-one",
        "module_version": "1.0.0",
        "integration_commit": "b" * 40,
        "artifact_uri": "sha256:" + SHA,
        "artifact_file": "payload.bin",
        "artifact_sha256": SHA,
        "size_bytes": 12,
        "merge_decision_id": "merge-" + "c" * 32,
        "trusted_merge_policy_sha256": "e" * 64,
        "source_tree_sha256": "f" * 64,
        "agents_sha256": "1" * 64,
        "manifest_sha256": "2" * 64,
        "sbom": {"path": "payload.spdx.json", "sha256": "3" * 64, "media_type": "application/json"},
        "provenance": {"path": "payload.provenance.json", "sha256": "4" * 64, "media_type": "application/json"},
        "signature": {
            "status": "UNSIGNED_AWAITING_EXTERNAL_SIGNER",
            "signer_required": "external-controlled-signer",
        },
    }


class ArtifactContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.request_schema = load("artifact.build.request.v1.schema.json")
        cls.descriptor_schema = load("artifact.descriptor.v1.schema.json")
        for schema in (cls.request_schema, cls.descriptor_schema):
            Draft202012Validator.check_schema(schema)

    def validate(self, schema, instance):
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(instance)

    def test_positive_request_and_descriptor_validate(self):
        self.validate(self.request_schema, build_request())
        self.validate(self.descriptor_schema, descriptor())

    def test_unknown_field_and_unknown_version_fail(self):
        invalid = build_request()
        invalid["required_checks"] = []
        self.assertRaises(Exception, self.validate, self.request_schema, invalid)
        invalid = descriptor()
        invalid["contract_id"] = "artifact.descriptor/v2"
        self.assertRaises(Exception, self.validate, self.descriptor_schema, invalid)
        invalid = build_request()
        del invalid["trace_id"]
        self.assertRaises(Exception, self.validate, self.request_schema, invalid)

    def test_producer_and_identity_boundaries_fail_closed(self):
        invalid = build_request()
        invalid["producer_module"] = "factory-artifact-builder"
        self.assertRaises(Exception, self.validate, self.request_schema, invalid)
        invalid = copy.deepcopy(descriptor())
        invalid["device_binding_id"] = "device-without-db-prefix"
        self.assertRaises(Exception, self.validate, self.descriptor_schema, invalid)

    def test_path_escape_and_forged_signed_descriptor_are_rejected(self):
        invalid = build_request()
        invalid["artifact_path"] = "../outside.bin"
        self.assertRaises(Exception, self.validate, self.request_schema, invalid)
        invalid = descriptor()
        invalid["signature"] = {"status": "SIGNED", "signer_required": "self"}
        self.assertRaises(Exception, self.validate, self.descriptor_schema, invalid)

    def test_opaque_ids_reject_noncanonical_values_and_trailing_newlines(self):
        cases = {
            "soul_id": SOUL_ID,
            "device_binding_id": DEVICE_ID,
            "platform_account_id": ACCOUNT_ID,
            "trace_id": TRACE_ID,
            "idempotency_key": IDEMPOTENCY_KEY,
        }
        for field, valid in cases.items():
            with self.subTest(contract="request", field=field):
                invalid = build_request()
                invalid[field] = valid + "\n"
                self.assertRaises(Exception, self.validate, self.request_schema, invalid)
            with self.subTest(contract="descriptor", field=field):
                invalid = descriptor()
                invalid[field] = valid + "\n"
                self.assertRaises(Exception, self.validate, self.descriptor_schema, invalid)


if __name__ == "__main__":
    unittest.main()
