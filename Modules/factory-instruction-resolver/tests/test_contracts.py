import copy
import datetime as dt
import hashlib
import importlib.util
import json
import sys
import unittest
from pathlib import Path
from unittest import mock

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import ValidationError


MODULE_ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "factory_instruction_resolver_contract_subject",
    MODULE_ROOT / "src" / "instruction_resolver.py",
)
SUBJECT = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = SUBJECT
SPEC.loader.exec_module(SUBJECT)

FIXTURE_SPEC = importlib.util.spec_from_file_location(
    "factory_instruction_resolver_unit_fixture",
    MODULE_ROOT / "tests" / "test_instruction_resolver.py",
)
FIXTURE_MODULE = importlib.util.module_from_spec(FIXTURE_SPEC)
assert FIXTURE_SPEC.loader is not None
sys.modules[FIXTURE_SPEC.name] = FIXTURE_MODULE
FIXTURE_SPEC.loader.exec_module(FIXTURE_MODULE)

INTAKE_ROOT = MODULE_ROOT.parent / "factory-upgrade-intake"
INTAKE_SPEC = importlib.util.spec_from_file_location(
    "factory_upgrade_intake_cross_module_subject",
    INTAKE_ROOT / "src" / "upgrade_intake.py",
)
INTAKE = importlib.util.module_from_spec(INTAKE_SPEC)
assert INTAKE_SPEC.loader is not None
sys.modules[INTAKE_SPEC.name] = INTAKE
INTAKE_SPEC.loader.exec_module(INTAKE)


def strict_format_checker():
    checker = FormatChecker()

    @checker.checks("date-time")
    def is_datetime(value):
        try:
            parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        except (AttributeError, ValueError):
            return False
        return parsed.tzinfo is not None

    return checker


def bound_file(path, order):
    return {
        "path": path,
        "order": order,
        "source_state": "tracked",
        "git_blob": "a" * 40,
        "sha256": "b" * 64,
    }


def valid_instruction_receipt_v2():
    changes = []
    baseline_commit = "d" * 40
    claims_sha = hashlib.sha256(
        json.dumps(
            {
                "baseline_commit": baseline_commit,
                "public_contract_changes": changes,
            },
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    ).hexdigest()
    baseline_facts = []
    baseline_facts_sha = hashlib.sha256(
        json.dumps(
            {
                "baseline_commit": baseline_commit,
                "verified_baseline_contract_facts": baseline_facts,
            },
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    ).hexdigest()
    return {
        "schema_version": "dps.instruction-receipt/v2",
        "contract_id": "instruction.receipt/v2",
        "producer_module": "factory-instruction-resolver",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "2" * 64,
        "occurred_at": "2026-07-15T00:00:00Z",
        "privacy_class": "internal",
        "receipt_id": "instruction:" + "c" * 32,
        "intent_id": "intent:contract-0002",
        "auth_context_id": "auth-context-0002",
        "agent_identity": "module-implementer-1",
        "agent_role": "module-implementer",
        "baseline_commit": baseline_commit,
        "resolved_at": "2026-07-15T00:01:00Z",
        "scope": ["factory-instruction-resolver"],
        "source_intent_contract": {
            "contract_id": "upgrade.intent",
            "major": 2,
            "mode": "active",
        },
        "source_intake_payload_sha256": "0" * 64,
        "source_intake_peer_module": "factory-upgrade-intake",
        "source_intake_audience": "dps.factory-instruction-resolver",
        "source_intake_trust_receipt_id": "trust:intake-0002",
        "source_intake_trust_nonce": "nonce_" + "6" * 32,
        "source_intake_trust_issued_at": "2026-07-15T00:00:30Z",
        "source_intake_verified_at": "2026-07-15T00:01:00Z",
        "source_intake_trust_expires_at": "2026-07-15T00:30:00Z",
        "source_requester_auth_expires_at": "2026-07-15T01:00:00Z",
        "source_manifest_ownership_expires_at": "2026-07-15T01:00:00Z",
        "source_approval_expires_at": None,
        "source_upgrade_intent_sha256": "1" * 64,
        "source_approval_subject_sha256": "2" * 64,
        "source_requester_auth_context_sha256": "3" * 64,
        "source_requester_auth_receipt_id": "auth:receipt-0002",
        "source_requester_auth_nonce": "nonce_" + "4" * 32,
        "source_manifest_ownership_sha256": "5" * 64,
        "source_manifest_ownership_receipt_id": "manifest:ownership-0002",
        "requested_risk_tier": "R1",
        "requested_stage": "development",
        "requested_target_modules": ["factory-instruction-resolver"],
        "authorized_write_paths": [
            "Modules/factory-instruction-resolver/src/instruction_resolver.py"
        ],
        "source_authorization_status": "not-required",
        "source_contract_change_claims_status": "UNVERIFIED_EXPECTATIONS",
        "source_contract_change_claims_sha256": claims_sha,
        "baseline_verification_required": True,
        "bound_contract_change_expectations": changes,
        "verified_baseline_contract_facts": baseline_facts,
        "verified_baseline_contract_facts_sha256": baseline_facts_sha,
        "changeset_contract_verification_required": True,
        "contract_declarations": [
            {
                "contract_id": "instruction.receipt",
                "major": 2,
                "mode": "active",
                "source": "Modules/factory-instruction-resolver/contracts/provided/instruction.receipt.v2.schema.json",
                "status": "proposed",
                "owner_module": "factory-instruction-resolver",
                "declaring_module": "factory-instruction-resolver",
                "declaration_kind": "provided",
            }
        ],
        "instructions": [
            bound_file("AGENTS.md", 0),
            bound_file("Modules/factory-instruction-resolver/AGENTS.md", 1),
        ],
        "manifests": [
            bound_file("Modules/factory-instruction-resolver/module.yaml", 0)
        ],
        "contracts": [
            bound_file(
                "Modules/factory-instruction-resolver/contracts/provided/instruction.receipt.v2.schema.json",
                0,
            )
        ],
        "governance": [
            bound_file("governance/modules/dependency-graph.yaml", 0),
            bound_file("governance/modules/compatibility.yaml", 1),
            bound_file("governance/policies/compatibility-policy.yaml", 2),
        ],
        "tests": [
            bound_file(
                "Modules/factory-instruction-resolver/tests/test_instruction_resolver.py",
                0,
            )
        ],
        "operations": [
            bound_file("Modules/factory-instruction-resolver/operations/README.md", 0)
        ],
        "diff_fingerprint": "e" * 64,
        "status": "BOUND",
        "invalidated_reason": None,
    }


def public_change(kind="add-major"):
    current = "f" * 64
    value = {
        "contract_id": "alpha.event",
        "major": 2,
        "baseline_commit": "d" * 40,
        "expected_mode": "active",
        "expected_status": "proposed",
        "expected_baseline_state": "absent",
        "change_kind": kind,
        "expected_owner_module": "alpha",
        "expected_source": "Modules/alpha/contracts/provided/alpha.event.v2.schema.json",
        "expected_source_sha256": current,
        "expected_previous_mode": None,
        "expected_previous_source_sha256": None,
        "quarantine_reason": None,
        "quarantine_evidence_sha256": None,
    }
    if kind == "additive-schema":
        value["expected_baseline_state"] = "present"
        value["expected_previous_mode"] = "active"
        value["expected_previous_source_sha256"] = "0" * 64
    elif kind == "mode-transition":
        value["expected_mode"] = "quarantine-only"
        value["expected_status"] = "deprecated"
        value["expected_baseline_state"] = "present"
        value["expected_previous_mode"] = "active"
        value["expected_previous_source_sha256"] = current
    elif kind == "introduce-quarantined-major":
        value["expected_mode"] = "quarantine-only"
        value["expected_status"] = "deprecated"
        value["quarantine_reason"] = "historical-wire-import-no-baseline-major"
        value["quarantine_evidence_sha256"] = hashlib.sha256(
            b"DPS\x00dps.upgrade-intent/v2/quarantine-import-evidence\x00"
            + json.dumps(
                {
                    "baseline_commit": value["baseline_commit"],
                    "contract_id": value["contract_id"],
                    "major": value["major"],
                    "expected_source": value["expected_source"],
                    "expected_source_sha256": value["expected_source_sha256"],
                    "quarantine_reason": value["quarantine_reason"],
                },
                sort_keys=True,
                separators=(",", ":"),
            ).encode("utf-8")
        ).hexdigest()
    return value


class InstructionReceiptContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.v2_schema = json.loads(
            (
                MODULE_ROOT
                / "contracts"
                / "provided"
                / "instruction.receipt.v2.schema.json"
            ).read_text(encoding="utf-8")
        )
        cls.v1_schema = json.loads(
            (
                MODULE_ROOT
                / "contracts"
                / "provided"
                / "instruction.receipt.v1.schema.json"
            ).read_text(encoding="utf-8")
        )
        Draft202012Validator.check_schema(cls.v2_schema)
        Draft202012Validator.check_schema(cls.v1_schema)
        cls.validator = Draft202012Validator(
            cls.v2_schema, format_checker=strict_format_checker()
        )

    def test_complete_bound_v2_receipt_validates(self):
        self.validator.validate(valid_instruction_receipt_v2())

    def test_production_resolver_output_validates(self):
        fixture = FIXTURE_MODULE.InstructionResolverTests(methodName="runTest")
        fixture.setUp()
        try:
            produced = fixture._resolve()
            self.validator.validate(produced.canonical_receipt())
        finally:
            fixture.tearDown()

    def test_runtime_stale_is_schema_valid_and_preserves_bound_identity(self):
        fixture = FIXTURE_MODULE.InstructionResolverTests(methodName="runTest")
        fixture.setUp()
        try:
            bound = fixture._resolve()
            fixture._write("Modules/planner/AGENTS.md", "changed instructions")
            ok, reason, stale = fixture._validate(bound)
            self.assertFalse(ok)
            self.assertEqual("bound content or diff scope changed", reason)
            self.assertIsNotNone(stale)
            self.validator.validate(stale.canonical_receipt())
            self.assertEqual("STALE", stale["status"])
            self.assertEqual(bound["receipt_id"], stale["receipt_id"])
            for field in (
                "source_intake_payload_sha256",
                "source_intake_trust_receipt_id",
                "source_intake_trust_nonce",
                "source_upgrade_intent_sha256",
                "source_requester_auth_context_sha256",
                "source_manifest_ownership_sha256",
            ):
                self.assertEqual(bound[field], stale[field], field)
        finally:
            fixture.tearDown()

    def test_runtime_rejects_impossible_calendar_without_format_dependency(self):
        fixture = FIXTURE_MODULE.InstructionResolverTests(methodName="runTest")
        fixture.setUp()
        try:
            bound = fixture._resolve().canonical_receipt()
            schema_without_format = Draft202012Validator(self.v2_schema)
            for impossible in (
                "2026-02-30T00:00:00Z",
                "2026-02-31T00:00:00Z",
            ):
                invalid = copy.deepcopy(bound)
                invalid["occurred_at"] = impossible
                material = dict(invalid)
                material.pop("receipt_id")
                invalid["receipt_id"] = "instruction:" + hashlib.sha256(
                    SUBJECT._canonical_bytes(material)
                ).hexdigest()[:32]
                self.assertEqual([], list(schema_without_format.iter_errors(invalid)))
                with mock.patch.object(
                    SUBJECT, "_RECEIPT_V2_VALIDATOR", schema_without_format
                ), self.assertRaisesRegex(
                    SUBJECT.ResolutionError, "valid UTC timestamp"
                ):
                    SUBJECT._strict_receipt_v2_object(
                        invalid, require_bound=True
                    )

            equality = copy.deepcopy(bound)
            equality["source_intake_trust_expires_at"] = equality["resolved_at"]
            material = dict(equality)
            material.pop("receipt_id")
            equality["receipt_id"] = "instruction:" + hashlib.sha256(
                SUBJECT._canonical_bytes(material)
            ).hexdigest()[:32]
            self.assertEqual([], list(schema_without_format.iter_errors(equality)))
            with mock.patch.object(
                SUBJECT, "_RECEIPT_V2_VALIDATOR", schema_without_format
            ), self.assertRaisesRegex(
                SUBJECT.ResolutionError, "inconsistent or expired"
            ):
                SUBJECT._strict_receipt_v2_object(equality, require_bound=True)
        finally:
            fixture.tearDown()

    def test_real_intake_normalized_wire_flows_through_sealed_resolver(self):
        class AuthPort(INTAKE.AuthVerificationPort):
            def verify(self, record):
                return record.get("verification_material") == {"fixture": "signed"}

        class ManifestPort(INTAKE.ManifestOwnershipVerificationPort):
            def verify(self, baseline_commit, snapshot_sha256, receipt_id):
                return receipt_id == "manifest:cross-module-0002"

        now = dt.datetime(2026, 7, 15, 1, 0, tzinfo=dt.timezone.utc)
        auth_authority = INTAKE.ProcessBoundAuthAuthority(
            AuthPort(), clock=lambda: now
        )
        manifest_authority = INTAKE.ProcessBoundManifestAuthority(ManifestPort())
        fixture = FIXTURE_MODULE.InstructionResolverTests(methodName="runTest")
        fixture.setUp()
        try:
            auth = auth_authority.verify(
                {
                    "context_id": "authctx:cross-module-0002",
                    "subject": "factory-module-implementer",
                    "role": "module-implementer",
                    "audience": "dps.factory-upgrade-intake",
                    "issued_at": "2026-07-15T00:30:00Z",
                    "expires_at": "2026-07-15T02:00:00Z",
                    "nonce": "nonce_" + "b" * 32,
                    "receipt_id": "auth:cross-module-0002",
                    "approvals": [],
                    "verification_material": {"fixture": "signed"},
                }
            )
            ownership = manifest_authority.verify(
                fixture.baseline,
                {"planner": ["Modules/planner/**"]},
                "manifest:cross-module-0002",
            )
            intent = fixture._intent()
            intent.update(
                {
                    "auth_context_id": auth.context_id,
                    "requester_auth_context_sha256": auth.requester_context_sha256,
                    "requester_auth_receipt_id": auth.receipt_id,
                    "requester_auth_nonce": auth.nonce,
                    "manifest_ownership_sha256": ownership.snapshot_sha256,
                    "manifest_ownership_receipt_id": ownership.receipt_id,
                    "requester": {"identity": auth.subject, "role": auth.role},
                }
            )
            intent["public_contract_changes_sha256"] = (
                INTAKE.public_contract_changes_sha256(
                    intent["public_contract_changes"],
                    intent["target_modules"],
                    ownership,
                    intent["baseline_commit"],
                    manifest_authority,
                )
            )
            intent["approval_subject_sha256"] = INTAKE.approval_subject_sha256(
                intent
            )
            intent["upgrade_intent_sha256"] = INTAKE.upgrade_intent_sha256(intent)
            contract_material = {
                "baseline_commit": intent["baseline_commit"],
                "manifest_ownership_sha256": intent[
                    "manifest_ownership_sha256"
                ],
                "public_contract_changes": intent["public_contract_changes"],
            }
            approval_material = {
                key: value
                for key, value in intent.items()
                if key
                not in {
                    "authorization",
                    "approval_subject_sha256",
                    "upgrade_intent_sha256",
                }
            }
            full_material = {
                key: value
                for key, value in intent.items()
                if key != "upgrade_intent_sha256"
            }
            self.assertEqual(
                intent["public_contract_changes_sha256"],
                SUBJECT._domain_sha256(
                    "dps.upgrade-intent/v2/public-contract-changes",
                    contract_material,
                ),
            )
            self.assertEqual(
                intent["approval_subject_sha256"],
                SUBJECT._domain_sha256(
                    "dps.upgrade-intent/v2/approval-subject", approval_material
                ),
            )
            self.assertEqual(
                intent["upgrade_intent_sha256"],
                SUBJECT._domain_sha256(
                    "dps.upgrade-intent/v2/full-intent", full_material
                ),
            )
            quarantine_item = {
                "baseline_commit": fixture.baseline,
                "contract_id": "historical.wire",
                "major": 1,
                "expected_source": "Modules/planner/contracts/provided/historical.wire.v1.schema.json",
                "expected_source_sha256": "c" * 64,
                "quarantine_reason": "historical-wire-import-no-baseline-major",
            }
            self.assertEqual(
                INTAKE.quarantine_import_evidence_sha256(quarantine_item),
                SUBJECT._domain_sha256(
                    "dps.upgrade-intent/v2/quarantine-import-evidence",
                    quarantine_item,
                ),
            )
            raw = INTAKE.encode_upgrade_intent_v2(
                intent,
                auth,
                ownership,
                auth_authority,
                manifest_authority,
            )
            intake_schema = json.loads(
                (INTAKE_ROOT / "contracts/provided/upgrade.intent.v2.schema.json").read_text(
                    encoding="utf-8"
                )
            )
            Draft202012Validator(
                intake_schema, format_checker=strict_format_checker()
            ).validate(json.loads(raw))
            self.assertEqual(30, len(INTAKE._TOP_LEVEL_FIELDS))
            self.assertEqual(INTAKE._TOP_LEVEL_FIELDS, SUBJECT._UPGRADE_INTENT_V2_FIELDS)
            self.assertEqual(8, len(INTAKE._AUTH_FIELDS))
            self.assertEqual(INTAKE._AUTH_FIELDS, SUBJECT._AUTHORIZATION_FIELDS)
            self.assertEqual(14, len(INTAKE._PUBLIC_CONTRACT_CHANGE_FIELDS))
            self.assertEqual(
                INTAKE._PUBLIC_CONTRACT_CHANGE_FIELDS, SUBJECT._PUBLIC_CHANGE_FIELDS
            )

            payload_sha = hashlib.sha256(raw).hexdigest()
            attestation = fixture.verifier_port.create_process_bound_attestation(
                raw,
                trust_receipt_id="trust:" + payload_sha[:32],
                trust_nonce="nonce_" + payload_sha[:32],
                issued_at="2026-07-15T00:30:00Z",
                expires_at="2026-07-15T01:30:00Z",
                requester_auth_expires_at=auth.expires_at,
                manifest_ownership_expires_at="2026-07-15T02:00:00Z",
                approval_expires_at=None,
            )
            capability = fixture.trust_authority.verify_and_seal(raw, attestation)
            receipt = fixture.resolver.resolve(
                capability,
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
            )
            self.validator.validate(receipt.canonical_receipt())
            self.assertEqual(intent["target_modules"], receipt["requested_target_modules"])
            self.assertEqual(intent["requested_paths"], receipt["authorized_write_paths"])
            self.assertEqual(payload_sha, receipt["source_intake_payload_sha256"])

            field_drift = copy.deepcopy(intent)
            field_drift["requested_stage"] = "shadow"
            drift_raw = json.dumps(
                field_drift,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
            drift_sha = hashlib.sha256(drift_raw).hexdigest()
            drift_capability = fixture.trust_authority.verify_and_seal(
                drift_raw,
                fixture.verifier_port.create_process_bound_attestation(
                    drift_raw,
                    trust_receipt_id="trust:" + drift_sha[:32],
                    trust_nonce="nonce_" + drift_sha[:32],
                    issued_at="2026-07-15T00:30:00Z",
                    expires_at="2026-07-15T01:30:00Z",
                    requester_auth_expires_at=auth.expires_at,
                    manifest_ownership_expires_at="2026-07-15T02:00:00Z",
                    approval_expires_at=None,
                ),
            )
            with self.assertRaisesRegex(
                FIXTURE_MODULE.RESOLVER.ResolutionError,
                "approval subject digest mismatch",
            ):
                fixture.resolver.resolve(
                    drift_capability,
                    agent_identity="factory-module-implementer",
                    agent_role="module-implementer",
                )

            wrong_domain = copy.deepcopy(intent)
            wrong_domain["public_contract_changes_sha256"] = hashlib.sha256(
                json.dumps(
                    contract_material,
                    ensure_ascii=False,
                    separators=(",", ":"),
                    sort_keys=True,
                ).encode("utf-8")
            ).hexdigest()
            wrong_domain["approval_subject_sha256"] = (
                INTAKE.approval_subject_sha256(wrong_domain)
            )
            wrong_domain["upgrade_intent_sha256"] = INTAKE.upgrade_intent_sha256(
                wrong_domain
            )
            wrong_raw = json.dumps(
                wrong_domain,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
            wrong_sha = hashlib.sha256(wrong_raw).hexdigest()
            wrong_capability = fixture.trust_authority.verify_and_seal(
                wrong_raw,
                fixture.verifier_port.create_process_bound_attestation(
                    wrong_raw,
                    trust_receipt_id="trust:" + wrong_sha[:32],
                    trust_nonce="nonce_" + wrong_sha[:32],
                    issued_at="2026-07-15T00:30:00Z",
                    expires_at="2026-07-15T01:30:00Z",
                    requester_auth_expires_at=auth.expires_at,
                    manifest_ownership_expires_at="2026-07-15T02:00:00Z",
                    approval_expires_at=None,
                ),
            )
            with self.assertRaisesRegex(
                FIXTURE_MODULE.RESOLVER.ResolutionError,
                "public_contract_changes_sha256 mismatch",
            ):
                fixture.resolver.resolve(
                    wrong_capability,
                    agent_identity="factory-module-implementer",
                    agent_role="module-implementer",
                )

            wrong_full = copy.deepcopy(intent)
            wrong_full_material = {
                key: value
                for key, value in wrong_full.items()
                if key != "upgrade_intent_sha256"
            }
            wrong_full["upgrade_intent_sha256"] = hashlib.sha256(
                json.dumps(
                    wrong_full_material,
                    ensure_ascii=False,
                    separators=(",", ":"),
                    sort_keys=True,
                ).encode("utf-8")
            ).hexdigest()
            wrong_full_raw = json.dumps(
                wrong_full,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
            wrong_full_sha = hashlib.sha256(wrong_full_raw).hexdigest()
            wrong_full_capability = fixture.trust_authority.verify_and_seal(
                wrong_full_raw,
                fixture.verifier_port.create_process_bound_attestation(
                    wrong_full_raw,
                    trust_receipt_id="trust:" + wrong_full_sha[:32],
                    trust_nonce="nonce_" + wrong_full_sha[:32],
                    issued_at="2026-07-15T00:30:00Z",
                    expires_at="2026-07-15T01:30:00Z",
                    requester_auth_expires_at=auth.expires_at,
                    manifest_ownership_expires_at="2026-07-15T02:00:00Z",
                    approval_expires_at=None,
                ),
            )
            with self.assertRaisesRegex(
                FIXTURE_MODULE.RESOLVER.ResolutionError,
                "full upgrade intent digest mismatch",
            ):
                fixture.resolver.resolve(
                    wrong_full_capability,
                    agent_identity="factory-module-implementer",
                    agent_role="module-implementer",
                )

            wrong_quarantine = copy.deepcopy(intent)
            quarantine_change = {
                "contract_id": quarantine_item["contract_id"],
                "major": quarantine_item["major"],
                "baseline_commit": quarantine_item["baseline_commit"],
                "expected_mode": "quarantine-only",
                "expected_status": "deprecated",
                "expected_baseline_state": "absent",
                "change_kind": "introduce-quarantined-major",
                "expected_owner_module": "planner",
                "expected_source": quarantine_item["expected_source"],
                "expected_source_sha256": quarantine_item[
                    "expected_source_sha256"
                ],
                "expected_previous_mode": None,
                "expected_previous_source_sha256": None,
                "quarantine_reason": quarantine_item["quarantine_reason"],
                "quarantine_evidence_sha256": hashlib.sha256(
                    json.dumps(
                        quarantine_item,
                        ensure_ascii=False,
                        separators=(",", ":"),
                        sort_keys=True,
                    ).encode("utf-8")
                ).hexdigest(),
            }
            self.assertNotEqual(
                INTAKE.quarantine_import_evidence_sha256(quarantine_change),
                quarantine_change["quarantine_evidence_sha256"],
            )
            wrong_quarantine["requested_paths"] = sorted(
                set(wrong_quarantine["requested_paths"])
                | {quarantine_change["expected_source"]}
            )
            wrong_quarantine["public_contract_changes"] = [quarantine_change]
            wrong_quarantine["public_contract_changes_sha256"] = (
                SUBJECT._domain_sha256(
                    "dps.upgrade-intent/v2/public-contract-changes",
                    {
                        "baseline_commit": wrong_quarantine["baseline_commit"],
                        "manifest_ownership_sha256": wrong_quarantine[
                            "manifest_ownership_sha256"
                        ],
                        "public_contract_changes": [quarantine_change],
                    },
                )
            )
            wrong_quarantine["approval_subject_sha256"] = (
                INTAKE.approval_subject_sha256(wrong_quarantine)
            )
            wrong_quarantine["upgrade_intent_sha256"] = (
                INTAKE.upgrade_intent_sha256(wrong_quarantine)
            )
            wrong_quarantine_raw = json.dumps(
                wrong_quarantine,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
            wrong_quarantine_sha = hashlib.sha256(
                wrong_quarantine_raw
            ).hexdigest()
            wrong_quarantine_capability = (
                fixture.trust_authority.verify_and_seal(
                    wrong_quarantine_raw,
                    fixture.verifier_port.create_process_bound_attestation(
                        wrong_quarantine_raw,
                        trust_receipt_id="trust:" + wrong_quarantine_sha[:32],
                        trust_nonce="nonce_" + wrong_quarantine_sha[:32],
                        issued_at="2026-07-15T00:30:00Z",
                        expires_at="2026-07-15T01:30:00Z",
                        requester_auth_expires_at=auth.expires_at,
                        manifest_ownership_expires_at="2026-07-15T02:00:00Z",
                        approval_expires_at=None,
                    ),
                )
            )
            with self.assertRaisesRegex(
                FIXTURE_MODULE.RESOLVER.ResolutionError,
                "exact absent-baseline quarantine proof",
            ):
                fixture.resolver.resolve(
                    wrong_quarantine_capability,
                    agent_identity="factory-module-implementer",
                    agent_role="module-implementer",
                )
        finally:
            fixture.tearDown()

    def test_each_change_kind_production_receipt_validates_against_schema(self):
        for kind in (
            "add-major",
            "additive-schema",
            "mode-transition",
            "introduce-quarantined-major",
        ):
            with self.subTest(kind=kind):
                fixture = FIXTURE_MODULE.InstructionResolverTests(methodName="runTest")
                fixture.setUp()
                try:
                    if kind == "add-major":
                        change = fixture._change(
                            "future.event", 1, "active", kind, owner="planner"
                        )
                    elif kind == "additive-schema":
                        source = fixture._contract_source(
                            "planner", "action.proposal", 2
                        )
                        fixture._write(
                            source,
                            '{"title":"action.proposal/v2","additive":true}\n',
                        )
                        change = fixture._change(
                            "action.proposal",
                            2,
                            "active",
                            kind,
                            previous_mode="active",
                        )
                    elif kind == "mode-transition":
                        manifest = fixture._manifest("planner")
                        manifest["contracts"]["provided"][0]["mode"] = (
                            "quarantine-only"
                        )
                        manifest["contracts"]["provided"][0]["status"] = "deprecated"
                        fixture._write_manifest("planner", manifest)
                        change = fixture._change(
                            "action.proposal",
                            1,
                            "quarantine-only",
                            kind,
                            previous_mode="active",
                        )
                    else:
                        change = fixture._change(
                            "historical.wire",
                            1,
                            "quarantine-only",
                            kind,
                            owner="planner",
                        )
                    produced = fixture._resolve(
                        fixture._intent(changes=[change])
                    )
                    self.validator.validate(produced.canonical_receipt())
                finally:
                    fixture.tearDown()

    def test_v1_schema_bytes_remain_historical_and_runtime_is_not_v1(self):
        self.assertEqual(
            "1b6dfffd7469ceafcfb21dba357d13ba13f63d51d00b9d77c304853abf07b4d3",
            hashlib.sha256(
                (
                    MODULE_ROOT
                    / "contracts"
                    / "provided"
                    / "instruction.receipt.v1.schema.json"
                ).read_bytes()
            ).hexdigest(),
        )
        self.assertEqual(
            "dps.instruction-receipt/v1",
            self.v1_schema["properties"]["schema_version"]["const"],
        )
        self.assertEqual(
            "dps.instruction-receipt/v2",
            self.v2_schema["properties"]["schema_version"]["const"],
        )

    def test_unknown_major_producer_and_extra_field_fail_closed(self):
        invalid = valid_instruction_receipt_v2()
        invalid["contract_id"] = "instruction.receipt/v3"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        invalid["producer_module"] = "factory-upgrade-intake"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        invalid["implementation_passed"] = True
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        del invalid["source_upgrade_intent_sha256"]
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

    def test_missing_binding_unsafe_path_and_unknown_mode_fail_closed(self):
        invalid = valid_instruction_receipt_v2()
        del invalid["source_contract_change_claims_sha256"]
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = copy.deepcopy(valid_instruction_receipt_v2())
        invalid["instructions"][1]["path"] = "../outside/AGENTS.md"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = copy.deepcopy(valid_instruction_receipt_v2())
        invalid["contract_declarations"][0]["mode"] = "legacy"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

    def test_repository_path_aliases_rejected_by_contract_schema(self):
        for unsafe in (
            "Modules\\alpha\\AGENTS.md",
            ".git/config",
            "Modules//alpha/AGENTS.md",
            "Modules/alpha/",
        ):
            invalid = copy.deepcopy(valid_instruction_receipt_v2())
            invalid["instructions"][1]["path"] = unsafe
            with self.assertRaises(ValidationError, msg=unsafe):
                self.validator.validate(invalid)

    def test_public_change_kinds_enforce_shape_and_monotonic_modes(self):
        for kind in (
            "add-major",
            "additive-schema",
            "mode-transition",
            "introduce-quarantined-major",
        ):
            value = valid_instruction_receipt_v2()
            value["bound_contract_change_expectations"] = [public_change(kind)]
            self.validator.validate(value)

        invalid = valid_instruction_receipt_v2()
        change = public_change("mode-transition")
        change["expected_previous_mode"] = "quarantine-only"
        change["expected_mode"] = "active"
        invalid["bound_contract_change_expectations"] = [change]
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        change = public_change("mode-transition")
        change["expected_status"] = "active"
        invalid["bound_contract_change_expectations"] = [change]
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        change = public_change("additive-schema")
        change["expected_status"] = "retired"
        invalid["bound_contract_change_expectations"] = [change]
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

    def test_future_expectations_cannot_be_named_or_marked_verified(self):
        invalid = valid_instruction_receipt_v2()
        invalid["verified_contract_changes"] = []
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

    def test_status_reason_and_authorized_write_scope_fail_closed(self):
        invalid = valid_instruction_receipt_v2()
        invalid["invalidated_reason"] = "pretend stale"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        invalid["status"] = "STALE"
        invalid["invalidated_reason"] = None
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        stale = valid_instruction_receipt_v2()
        stale["status"] = "STALE"
        stale["invalidated_reason"] = "bound bytes changed"
        self.validator.validate(stale)

        for path in (
            "../outside.py",
            "Modules/factory-instruction-resolver/src/*.py",
            ".hidden/state",
        ):
            invalid = valid_instruction_receipt_v2()
            invalid["authorized_write_paths"] = [path]
            with self.assertRaises(ValidationError, msg=path):
                self.validator.validate(invalid)

        invalid = valid_instruction_receipt_v2()
        invalid["source_contract_change_claims_status"] = "VERIFIED"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

    def test_contract_declaration_requires_exact_major_mode_owner_and_source(self):
        for field in ("major", "mode", "owner_module", "source", "status"):
            invalid = copy.deepcopy(valid_instruction_receipt_v2())
            del invalid["contract_declarations"][0][field]
            with self.assertRaises(ValidationError, msg=field):
                self.validator.validate(invalid)

        invalid = copy.deepcopy(valid_instruction_receipt_v2())
        invalid["contract_declarations"][0]["mode"] = "compat-read"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)


if __name__ == "__main__":
    unittest.main()
