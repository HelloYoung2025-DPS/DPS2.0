import copy
import hashlib
import importlib.util
import json
import sys
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "evidence_ledger.py"
SUBJECT_NAME = "_dps_factory_evidence_ledger_contract_subject"
FROZEN_PUBLIC_WIRE_SHA256 = {
    "upgrade.event.append.v1.schema.json": "c331c9d58815922c2988e35036327861e91a09e1676531671f1efa198cf75f28",
    "upgrade.event.v1.schema.json": "13b8c73d948f9c573ad4b07c8bdf4799ffe5073cc4b6fa6df6756827e23cee41",
}


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("contract subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != ROOT or source_path.parent != source_root:
        raise ImportError("contract subject escaped the module-owned src directory")
    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        if Path(getattr(existing, "__file__", "")).resolve(strict=True) != source_path:
            raise ImportError("contract subject module name is already bound elsewhere")
        return existing
    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the contract subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()


def command(payload=None, expected_sequence=0, key="append-001"):
    payload = payload if payload is not None else {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"}
    return {
        "schema_version": "1.0.0",
        "contract_id": "upgrade.event.append/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + SUBJECT.sha256({"fixture_key": key}),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "stream_id": "upgrade-001",
        "expected_sequence": expected_sequence,
        "event_type": "STATE_TRANSITIONED",
        "payload": payload,
        "payload_sha256": SUBJECT.sha256(payload),
    }


def load_public(name):
    return json.loads((ROOT / "contracts" / "provided" / name).read_text(encoding="utf-8"))


class EvidenceLedgerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.append_schema = load_public("upgrade.event.append.v1.schema.json")
        cls.event_schema = load_public("upgrade.event.v1.schema.json")
        cls.auth_schema = json.loads(
            (ROOT / "contracts" / "internal" / "append.authorization.v1.schema.json").read_text(encoding="utf-8")
        )
        for schema in (cls.append_schema, cls.event_schema, cls.auth_schema):
            Draft202012Validator.check_schema(schema)

    def validate(self, schema, value):
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)

    def event(self, request):
        authority = SUBJECT.DevelopmentAppendAuthority.for_local_tests()
        ledger = SUBJECT.EvidenceLedger(SUBJECT.InMemoryEvidenceRepository(authority), authority)
        capability = authority.issue(SUBJECT.canonical_bytes(request))
        return ledger.append(capability)

    def test_public_v1_wire_schemas_are_byte_frozen(self):
        for name, expected in FROZEN_PUBLIC_WIRE_SHA256.items():
            actual = hashlib.sha256((ROOT / "contracts" / "provided" / name).read_bytes()).hexdigest()
            self.assertEqual(expected, actual, name)

    def test_positive_append_and_produced_event_validate(self):
        request = command()
        request["device_binding_id"] = "db_" + "3" * 32
        request["platform_account_id"] = "pa_" + "4" * 32
        self.validate(self.append_schema, request)
        self.validate(self.event_schema, self.event(request))

    def test_unknown_major_missing_extra_and_boolean_sequence_fail(self):
        invalid = command()
        invalid["contract_id"] = "upgrade.event.append/v2"
        self.assertRaises(Exception, self.validate, self.append_schema, invalid)
        invalid = command()
        del invalid["trace_id"]
        self.assertRaises(Exception, self.validate, self.append_schema, invalid)
        invalid = command()
        invalid["reported_pass"] = True
        self.assertRaises(Exception, self.validate, self.append_schema, invalid)
        invalid = command()
        invalid["expected_sequence"] = True
        self.assertRaises(Exception, self.validate, self.append_schema, invalid)

    def test_wrong_producer_identity_timestamp_stream_and_privacy_fail(self):
        attacks = []
        invalid = command(); invalid["producer_module"] = "factory-untrusted-writer"; attacks.append(invalid)
        invalid = command(); invalid["device_binding_id"] = "device-without-db-prefix"; attacks.append(invalid)
        invalid = command(); invalid["stream_id"] = "../escape"; attacks.append(invalid)
        invalid = command(); invalid["privacy_class"] = "public"; attacks.append(invalid)
        for invalid in attacks:
            with self.assertRaises(Exception):
                self.validate(self.append_schema, invalid)
        # The byte-frozen v1 schema uses the optional JSON Schema date-time
        # format vocabulary. Runtime validation is the mandatory hard gate.
        invalid = command(); invalid["occurred_at"] = "not-a-date"
        authority = SUBJECT.DevelopmentAppendAuthority.for_local_tests()
        with self.assertRaises(Exception):
            authority.issue(SUBJECT.canonical_bytes(invalid))

    def test_event_enums_types_and_derived_fields_are_closed(self):
        event = self.event(command())
        attacks = {
            "producer_module": "factory-release-controller",
            "source_module": "factory-unregistered-controller",
            "event_id": "event-" + "0" * 32,
            "sequence": True,
            "append_status": "PARTIAL",
            "privacy_class": "public",
        }
        for field, value in attacks.items():
            invalid = copy.deepcopy(event)
            invalid[field] = value
            with self.subTest(field=field):
                if field in {"event_id", "sequence"}:
                    # The JSON schema checks shape/type; deterministic derivation is a runtime replay invariant.
                    if field == "sequence":
                        self.assertRaises(Exception, self.validate, self.event_schema, invalid)
                    else:
                        self.validate(self.event_schema, invalid)
                else:
                    self.assertRaises(Exception, self.validate, self.event_schema, invalid)

    def test_internal_auth_schema_is_exact_and_not_a_public_contract(self):
        now = 1_800_000_000
        value = {
            "schema_version": "dps.factory-evidence-append-auth/v1",
            "issuer": "dps-factory-auth-service",
            "audience": "factory-evidence-ledger",
            "scope": "factory:evidence:append",
            "producer_module": "factory-release-controller",
            "command_sha256": "1" * 64,
            "issued_at": now,
            "expires_at": now + 60,
            "revocation_epoch": 2,
            "nonce": "auth_" + "2" * 32,
            "key_id": "factory-evidence-append-v1",
            "signature": "3" * 64,
        }
        self.validate(self.auth_schema, value)
        value["caller_selected_root"] = "/tmp"
        self.assertRaises(Exception, self.validate, self.auth_schema, value)
        manifest = json.loads((ROOT / "module.yaml").read_text(encoding="utf-8"))
        public_sources = {item["source"] for item in manifest["contracts"]["provided"]}
        self.assertNotIn(
            "Modules/factory-evidence-ledger/contracts/internal/append.authorization.v1.schema.json",
            public_sources,
        )

    def test_migration_002_declares_fixed_roles_protected_functions_and_all_mutation_guards(self):
        sql = (ROOT / "migrations" / "002_authenticated_append_acl.sql").read_text(encoding="utf-8")
        required = (
            "CREATE ROLE dps_factory_evidence_owner NOLOGIN",
            "CREATE ROLE dps_factory_evidence_runtime LOGIN",
            "CREATE ROLE dps_factory_evidence_admin LOGIN",
            "ALTER ROLE dps_factory_evidence_owner",
            "NOBYPASSRLS NOINHERIT",
            "factory evidence roles must not participate in role membership chains",
            "pg_advisory_xact_lock(73031, 20260715)",
            "SECURITY DEFINER",
            "session_user <> 'dps_factory_evidence_runtime'",
            "session_user <> 'dps_factory_evidence_admin'",
            "REVOKE ALL ON ALL TABLES IN SCHEMA factory_evidence FROM dps_factory_evidence_runtime",
            "REVOKE ALL ON ALL TABLES IN SCHEMA factory_evidence FROM dps_factory_evidence_admin",
            "REVOKE ALL ON ALL FUNCTIONS IN SCHEMA factory_evidence FROM dps_factory_evidence_runtime",
            "REVOKE ALL ON ALL FUNCTIONS IN SCHEMA factory_evidence FROM dps_factory_evidence_admin",
            "factory_evidence.append_upgrade_event",
            "factory_evidence.install_append_auth_key",
            "upgrade_event_no_truncate",
            "upgrade_event_quarantine_no_truncate",
            "upgrade_stream_no_truncate",
            "append_auth_key_no_truncate",
            "DEFERRABLE INITIALLY DEFERRED",
            "stream head is inconsistent with ordered events",
            "public.hmac(convert_to(v_auth_material, 'UTF8'), v_key, 'sha256')",
            "jsonb_typeof(v_command -> 'expected_sequence') <> 'number'",
            "jsonb_typeof(p_event_json -> 'sequence') <> 'number'",
            "jsonb_typeof(p_auth_json -> 'issued_at') <> 'number'",
        )
        for token in required:
            self.assertIn(token, sql)
        source = SOURCE_PATH.read_text(encoding="utf-8")
        self.assertNotIn("connection_factory", source)


if __name__ == "__main__":
    unittest.main()
