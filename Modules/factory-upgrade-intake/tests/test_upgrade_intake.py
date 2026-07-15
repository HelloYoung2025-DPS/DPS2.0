import copy
import datetime as dt
import hashlib
import importlib.util
import json
import pathlib
import sys
import unittest


MODULE_ROOT = pathlib.Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "upgrade_intake", MODULE_ROOT / "src" / "upgrade_intake.py"
)
UPGRADE_INTAKE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = UPGRADE_INTAKE
SPEC.loader.exec_module(UPGRADE_INTAKE)

BASELINE = "a" * 40
OWNERSHIP = {
    "alpha-module": ["Modules/alpha-module/**"],
    "beta-module": ["Modules/beta-module/**"],
}


class MutableClock:
    def __init__(self):
        self.value = dt.datetime(2026, 7, 15, 0, 5, tzinfo=dt.timezone.utc)

    def __call__(self):
        return self.value

    def advance(self, minutes):
        self.value += dt.timedelta(minutes=minutes)


class FixtureAuthPort(UPGRADE_INTAKE.AuthVerificationPort):
    def __init__(self, decision=True):
        self.decision = decision
        self.calls = 0

    def verify(self, record):
        self.calls += 1
        return self.decision is True and record.get("verification_material") == {
            "fixture_signature": "signed"
        }


class FixtureManifestPort(UPGRADE_INTAKE.ManifestOwnershipVerificationPort):
    def __init__(self, decision=True):
        self.decision = decision
        self.calls = []

    def verify(self, baseline_commit, snapshot_sha256, receipt_id):
        self.calls.append((baseline_commit, snapshot_sha256, receipt_id))
        return self.decision is True and receipt_id.startswith("manifest:")


def change(kind="add-major", contract_id="alpha.contract", major=2):
    item = {
        "contract_id": contract_id,
        "major": major,
        "baseline_commit": BASELINE,
        "expected_mode": "active",
        "expected_status": "proposed",
        "expected_baseline_state": "absent",
        "change_kind": kind,
        "expected_owner_module": "alpha-module",
        "expected_source": (
            "Modules/alpha-module/contracts/provided/%s.v%d.schema.json"
            % (contract_id, major)
        ),
        "expected_source_sha256": "c" * 64,
        "expected_previous_mode": None,
        "expected_previous_source_sha256": None,
        "quarantine_reason": None,
        "quarantine_evidence_sha256": None,
    }
    if kind == "additive-schema":
        item.update({
            "expected_status": "active",
            "expected_baseline_state": "present",
            "expected_previous_mode": "active",
            "expected_previous_source_sha256": "b" * 64,
        })
    elif kind == "mode-transition":
        item.update({
            "expected_mode": "quarantine-only",
            "expected_status": "deprecated",
            "expected_baseline_state": "present",
            "expected_source_sha256": "b" * 64,
            "expected_previous_mode": "active",
            "expected_previous_source_sha256": "b" * 64,
        })
    elif kind == "introduce-quarantined-major":
        item.update({
            "expected_mode": "quarantine-only",
            "expected_status": "deprecated",
            "expected_baseline_state": "absent",
            "quarantine_reason": "historical-wire-import-no-baseline-major",
        })
        item["quarantine_evidence_sha256"] = (
            UPGRADE_INTAKE.quarantine_import_evidence_sha256(item)
        )
    return item


class UpgradeIntakeTests(unittest.TestCase):
    def setUp(self):
        self.clock = MutableClock()
        self.auth_port = FixtureAuthPort()
        self.auth_authority = UPGRADE_INTAKE.ProcessBoundAuthAuthority(
            self.auth_port, clock=self.clock
        )
        self.manifest_port = FixtureManifestPort()
        self.manifest_authority = UPGRADE_INTAKE.ProcessBoundManifestAuthority(
            self.manifest_port
        )
        self.ownership = self.manifest_authority.verify(
            BASELINE, OWNERSHIP, "manifest:receipt0001"
        )
        self.auth = self.issue_auth()

    def auth_record(self, approvals=None):
        return {
            "context_id": "authctx:0001",
            "subject": "agent-implementer",
            "role": "module-implementer",
            "audience": "dps.factory-upgrade-intake",
            "issued_at": "2026-07-15T00:00:00Z",
            "expires_at": "2026-07-15T00:10:00Z",
            "nonce": "nonce_" + "1" * 32,
            "receipt_id": "auth:receipt0001",
            "approvals": approvals or [],
            "verification_material": {"fixture_signature": "signed"},
        }

    def issue_auth(self, approvals=None):
        return self.auth_authority.verify(self.auth_record(approvals))

    def bind_hashes(self, intent, ownership=None, manifest_authority=None):
        ownership = ownership or self.ownership
        manifest_authority = manifest_authority or self.manifest_authority
        intent["target_modules"] = sorted(intent["target_modules"])
        intent["requested_paths"] = sorted(intent["requested_paths"])
        intent["public_contract_changes"] = sorted(
            intent["public_contract_changes"],
            key=UPGRADE_INTAKE._contract_change_sort_key,
        )
        if intent["authorization"]["status"] == "approved":
            intent["authorization"]["approval_scope"] = sorted(
                intent["authorization"]["approval_scope"]
            )
        intent["public_contract_changes_sha256"] = (
            UPGRADE_INTAKE.public_contract_changes_sha256(
                intent["public_contract_changes"],
                intent["target_modules"],
                ownership,
                intent["baseline_commit"],
                manifest_authority,
            )
        )
        intent["approval_subject_sha256"] = (
            UPGRADE_INTAKE.approval_subject_sha256(intent)
        )
        intent["upgrade_intent_sha256"] = (
            UPGRADE_INTAKE.upgrade_intent_sha256(intent)
        )
        return intent

    def valid_intent(
        self,
        *,
        auth=None,
        changes=None,
        requested_risk_tier="R1",
        requested_stage="development",
        authorization=None,
        ownership=None,
        manifest_authority=None,
    ):
        auth = auth or self.auth
        ownership = ownership or self.ownership
        changes = list(changes or [])
        paths = ["Modules/alpha-module/src/domain.py"]
        paths.extend(item["expected_source"] for item in changes)
        intent = {
            "schema_version": "dps.upgrade-intent/v2",
            "contract_id": "upgrade.intent/v2",
            "producer_module": "factory-upgrade-intake",
            "soul_id": None,
            "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "2" * 32,
            "idempotency_key": "idem_" + "3" * 64,
            "occurred_at": "2026-07-15T00:04:00Z",
            "privacy_class": "internal",
            "intent_id": "intent:0001",
            "auth_context_id": auth.context_id,
            "requester_auth_context_sha256": auth.requester_context_sha256,
            "requester_auth_receipt_id": auth.receipt_id,
            "requester_auth_nonce": auth.nonce,
            "baseline_commit": ownership.baseline_commit,
            "manifest_ownership_sha256": ownership.snapshot_sha256,
            "manifest_ownership_receipt_id": ownership.receipt_id,
            "target_modules": ["alpha-module"],
            "requested_paths": paths,
            "public_contract_changes": changes,
            "public_contract_changes_sha256": "0" * 64,
            "contract_change_claims_status": "UNVERIFIED_EXPECTATIONS",
            "baseline_verification_required": True,
            "approval_subject_sha256": "0" * 64,
            "upgrade_intent_sha256": "0" * 64,
            "requested_risk_tier": requested_risk_tier,
            "requested_stage": requested_stage,
            "requester": {
                "identity": auth.subject,
                "role": auth.role,
            },
            "authorization": authorization or {
                "status": "not-required",
                "approved_by": None,
                "approver_role": "not-applicable",
                "approval_scope": [],
                "approval_receipt_id": None,
                "approval_nonce": None,
                "approved_at": None,
                "approval_expires_at": None,
            },
        }
        return self.bind_hashes(intent, ownership, manifest_authority)

    def approved_intent(self):
        authorization = {
            "status": "approved",
            "approved_by": "human-release-approver-1",
            "approver_role": "human-release-approver",
            "approval_scope": ["canary"],
            "approval_receipt_id": "approval:receipt0001",
            "approval_nonce": "nonce_" + "9" * 32,
            "approved_at": "2026-07-15T00:01:00Z",
            "approval_expires_at": "2026-07-15T00:09:00Z",
        }
        draft = self.valid_intent(
            requested_risk_tier="R3",
            requested_stage="canary",
            authorization=authorization,
        )
        approval = {
            "approver_subject": authorization["approved_by"],
            "approver_role": authorization["approver_role"],
            "scopes": authorization["approval_scope"],
            "approval_subject_sha256": draft["approval_subject_sha256"],
            "intent_id": draft["intent_id"],
            "baseline_commit": draft["baseline_commit"],
            "requested_risk_tier": draft["requested_risk_tier"],
            "requested_stage": draft["requested_stage"],
            "audience": "dps.factory-upgrade-intake",
            "issued_at": authorization["approved_at"],
            "expires_at": authorization["approval_expires_at"],
            "nonce": authorization["approval_nonce"],
            "receipt_id": authorization["approval_receipt_id"],
        }
        approved_auth = self.issue_auth([approval])
        self.assertEqual(
            self.auth.requester_context_sha256,
            approved_auth.requester_context_sha256,
        )
        return self.bind_hashes(draft), approved_auth

    def validate(self, intent, auth=None, ownership=None, auth_authority=None,
                 manifest_authority=None):
        return UPGRADE_INTAKE.validate_upgrade_intent(
            intent,
            auth or self.auth,
            ownership or self.ownership,
            auth_authority or self.auth_authority,
            manifest_authority or self.manifest_authority,
        )

    def test_valid_intent_is_canonical_and_codec_round_trips(self):
        intent = self.valid_intent(changes=[change()])
        intent["requested_paths"].reverse()
        intent["target_modules"] = ["alpha-module"]
        self.bind_hashes(intent)
        result = self.validate(intent)
        encoded = UPGRADE_INTAKE.encode_upgrade_intent_v2(
            intent, self.auth, self.ownership,
            self.auth_authority, self.manifest_authority,
        )
        decoded = UPGRADE_INTAKE.decode_upgrade_intent_v2(
            encoded, self.auth, self.ownership,
            self.auth_authority, self.manifest_authority,
        )
        self.assertEqual(result, decoded)
        self.assertEqual(encoded, json.dumps(
            result, ensure_ascii=False, allow_nan=False,
            separators=(",", ":"), sort_keys=True,
        ).encode("utf-8"))

    def test_authority_instance_and_snapshot_are_not_caller_assertions(self):
        intent = self.valid_intent()
        other_auth_authority = UPGRADE_INTAKE.ProcessBoundAuthAuthority(
            FixtureAuthPort(), clock=self.clock
        )
        other_manifest_authority = UPGRADE_INTAKE.ProcessBoundManifestAuthority(
            FixtureManifestPort()
        )
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "supplied authority"):
            self.validate(intent, auth_authority=other_auth_authority)
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "supplied authority"):
            self.validate(intent, manifest_authority=other_manifest_authority)
        with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
            UPGRADE_INTAKE.ProcessBoundAuthAuthority(lambda _record: True)
        with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
            UPGRADE_INTAKE.ProcessBoundManifestAuthority(lambda *_args: True)

    def test_external_verification_false_and_expired_context_fail_closed(self):
        rejecting_auth = UPGRADE_INTAKE.ProcessBoundAuthAuthority(
            FixtureAuthPort(False), clock=self.clock
        )
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "externally verified"):
            rejecting_auth.verify(self.auth_record())
        rejecting_manifest = UPGRADE_INTAKE.ProcessBoundManifestAuthority(
            FixtureManifestPort(False)
        )
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "externally verified"):
            rejecting_manifest.verify(BASELINE, OWNERSHIP, "manifest:receipt0001")
        intent = self.valid_intent()
        self.clock.advance(6)
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "expired"):
            self.validate(intent)

    def test_approved_intent_is_bound_and_exact_duplicate_is_deterministic(self):
        intent, approved_auth = self.approved_intent()
        first = self.validate(intent, auth=approved_auth)
        second = self.validate(copy.deepcopy(intent), auth=approved_auth)
        self.assertEqual(first, second)
        self.assertEqual(intent["upgrade_intent_sha256"], first["upgrade_intent_sha256"])

    def test_old_approval_cannot_authorize_rehashed_different_intent(self):
        original, approved_auth = self.approved_intent()
        mutations = {
            "intent-id": lambda value: value.__setitem__("intent_id", "intent:0002"),
            "path": lambda value: value["requested_paths"].append(
                "Modules/alpha-module/src/other.py"
            ),
            "risk": lambda value: value.__setitem__("requested_risk_tier", "R2"),
            "stage": lambda value: value.__setitem__("requested_stage", "rolling"),
            "scope": lambda value: value["authorization"].__setitem__(
                "approval_scope", ["canary", "rolling"]
            ),
            "receipt": lambda value: value["authorization"].__setitem__(
                "approval_receipt_id", "approval:receipt0002"
            ),
            "nonce": lambda value: value["authorization"].__setitem__(
                "approval_nonce", "nonce_" + "8" * 32
            ),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name):
                attacked = copy.deepcopy(original)
                mutate(attacked)
                self.bind_hashes(attacked)
                with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                    self.validate(attacked, auth=approved_auth)

    def test_approval_and_context_expiry_are_rechecked_at_validation_time(self):
        intent, approved_auth = self.approved_intent()
        self.clock.value = dt.datetime(2026, 7, 15, 0, 9, 30, tzinfo=dt.timezone.utc)
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "approval"):
            self.validate(intent, auth=approved_auth)

    def test_self_approval_pending_rejected_and_r4_are_not_routable(self):
        cases = []
        r4 = self.valid_intent()
        r4["requested_risk_tier"] = "R4"
        cases.append(r4)
        for status in ("pending", "rejected"):
            candidate = self.valid_intent()
            candidate["authorization"]["status"] = status
            cases.append(candidate)
        approved, approved_auth = self.approved_intent()
        approved["authorization"]["approved_by"] = approved["requester"]["identity"]
        self.bind_hashes(approved)
        for candidate in cases:
            self.bind_hashes(candidate)
            with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                self.validate(candidate)
        with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
            self.validate(approved, auth=approved_auth)

    def test_all_four_contract_change_kinds_and_retirement_transitions(self):
        positives = [change("add-major"), change("additive-schema", major=1),
                     change("mode-transition", major=1),
                     change("introduce-quarantined-major", major=1)]
        for item in positives:
            with self.subTest(kind=item["change_kind"]):
                self.validate(self.valid_intent(changes=[item]))
        for previous in ("active", "quarantine-only"):
            retired = change("mode-transition", major=1)
            retired.update({
                "expected_previous_mode": previous,
                "expected_mode": "retired",
                "expected_status": "retired",
            })
            self.validate(self.valid_intent(changes=[retired]))

    def test_contract_change_invariants_fail_closed(self):
        invalid = []
        compat = change()
        compat["expected_mode"] = "compat-read"
        invalid.append(compat)
        bad_retired = change("mode-transition", major=1)
        bad_retired.update({"expected_mode": "retired", "expected_status": "deprecated"})
        invalid.append(bad_retired)
        bad_additive = change("additive-schema", major=1)
        bad_additive["expected_previous_source_sha256"] = bad_additive["expected_source_sha256"]
        invalid.append(bad_additive)
        bad_quarantine = change("introduce-quarantined-major", major=1)
        bad_quarantine["quarantine_evidence_sha256"] = "0" * 64
        invalid.append(bad_quarantine)
        bad_owner = change()
        bad_owner["expected_source"] = "Modules/beta-module/contracts/provided/x.json"
        invalid.append(bad_owner)
        for item in invalid:
            with self.subTest(item=item):
                with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                    self.valid_intent(changes=[item]) if item is bad_owner else self.validate(
                        self.valid_intent(changes=[item])
                    )

    def test_duplicate_conflict_order_and_snapshot_are_digest_bound(self):
        one = change(contract_id="zeta.contract")
        two = change(contract_id="alpha.contract")
        forward = self.valid_intent(changes=[one, two])
        reverse = self.valid_intent(changes=[two, one])
        self.assertEqual(
            forward["public_contract_changes_sha256"],
            reverse["public_contract_changes_sha256"],
        )
        duplicate = self.valid_intent()
        duplicate["public_contract_changes"] = [one, dict(one)]
        duplicate["requested_paths"].append(one["expected_source"])
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "duplicate"):
            self.bind_hashes(duplicate)
        expanded = dict(OWNERSHIP)
        expanded["gamma-module"] = ["Modules/gamma-module/**"]
        other_snapshot = self.manifest_authority.verify(
            BASELINE, expanded, "manifest:receipt0002"
        )
        other = self.valid_intent(
            ownership=other_snapshot, manifest_authority=self.manifest_authority
        )
        self.assertNotEqual(
            forward["public_contract_changes_sha256"],
            other["public_contract_changes_sha256"],
        )

    def test_contract_source_must_be_in_requested_paths(self):
        intent = self.valid_intent(changes=[change()])
        intent["requested_paths"].remove(change()["expected_source"])
        self.bind_hashes(intent)
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "requested_paths"):
            self.validate(intent)

    def test_path_alias_glob_scope_and_ownership_attacks_fail_closed(self):
        attacks = [
            "/absolute.py", "Modules/alpha-module/../escape.py",
            "Modules/alpha-module/./alias.py", "Modules/alpha-module//x.py",
            "Modules/alpha-module/.hidden/x.py", "Modules\\alpha-module\\x.py",
            "Modules/alpha-module/x.py/", "Modules/alpha-module/x.py\n",
            "Modules/alpha-module/*.py", "Modules/alpha-module/?.py",
            "Modules/alpha-module/[x].py",
        ]
        for path in attacks:
            with self.subTest(path=repr(path)):
                intent = self.valid_intent()
                intent["requested_paths"] = [path]
                with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                    self.validate(intent)
        overlapping = {
            "alpha-module": ["Modules/alpha-module/**"],
            "beta-module": ["Modules/**"],
        }
        snapshot = self.manifest_authority.verify(
            BASELINE, overlapping, "manifest:receipt0003"
        )
        intent = self.valid_intent(
            ownership=snapshot, manifest_authority=self.manifest_authority
        )
        with self.assertRaisesRegex(UPGRADE_INTAKE.IntentValidationError, "exactly one"):
            self.validate(intent, ownership=snapshot)

    def test_wrong_json_types_are_domain_errors_not_python_type_errors(self):
        cases = [
            ("requested_risk_tier", []),
            ("requested_stage", {}),
        ]
        for field, value in cases:
            with self.subTest(field=field):
                intent = self.valid_intent()
                intent[field] = value
                with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                    self.validate(intent)
        for field, value in (
            ("expected_mode", []), ("expected_status", {}),
            ("expected_baseline_state", []), ("change_kind", {}),
        ):
            with self.subTest(change_field=field):
                item = change()
                item[field] = value
                intent = self.valid_intent()
                intent["public_contract_changes"] = [item]
                intent["requested_paths"].append(item["expected_source"])
                with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                    self.validate(intent)

    def test_strict_wire_parser_rejects_ambiguous_or_resource_attack_json(self):
        attacks = [
            b"",
            b"\xef\xbb\xbf{}",
            b"\xff",
            b"[]",
            b'{"x":1.5}',
            b'{"x":NaN}',
            b'{"x":12345678901}',
            b'{"x":1} trailing',
            b'{"schema_version":"dps.upgrade-intent/v2",'
            b'"schema_version":"dps.upgrade-intent/v2"}',
            (b'{"x":' * 65) + b"0" + (b"}" * 65),
            b"{" + (b'\"x\":\"' + b"a" * (256 * 1024) + b'\"}'),
        ]
        for payload in attacks:
            with self.subTest(size=len(payload)):
                with self.assertRaises(UPGRADE_INTAKE.IntentValidationError):
                    UPGRADE_INTAKE.decode_upgrade_intent_v2(
                        payload, self.auth, self.ownership,
                        self.auth_authority, self.manifest_authority,
                    )

    def test_v1_can_only_produce_fixed_quarantine_metadata(self):
        payload = json.dumps({
            "schema_version": "dps.upgrade-intent/v1",
            "contract_id": "upgrade.intent/v1",
            "producer_module": "factory-upgrade-intake",
            "requested_paths": ["/danger"],
            "authorization": {"status": "approved"},
            "shell": "rm -rf /",
        }, separators=(",", ":"), sort_keys=True).encode("utf-8")
        first = UPGRADE_INTAKE.quarantine_upgrade_intent_v1(payload)
        second = UPGRADE_INTAKE.quarantine_upgrade_intent_v1(payload)
        self.assertEqual(first, second)
        self.assertEqual("QUARANTINED", first.disposition)
        self.assertEqual("quarantine-only", first.contract_mode)
        self.assertEqual(hashlib.sha256(payload).hexdigest(), first.payload_sha256)
        self.assertFalse(hasattr(UPGRADE_INTAKE, "encode_upgrade_intent_v1"))

    def test_same_idempotency_key_different_digest_is_externally_guarded(self):
        first = self.valid_intent()
        second = copy.deepcopy(first)
        second["requested_paths"].append("Modules/alpha-module/src/other.py")
        self.bind_hashes(second)
        self.validate(first)
        self.validate(second)
        self.assertEqual(first["idempotency_key"], second["idempotency_key"])
        self.assertNotEqual(
            first["upgrade_intent_sha256"], second["upgrade_intent_sha256"]
        )


if __name__ == "__main__":
    unittest.main()
