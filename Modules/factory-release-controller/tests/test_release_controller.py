import copy
import importlib.util
import json
import sys
import unittest
from pathlib import Path


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "release_controller.py"
SUBJECT_NAME = "_dps_factory_release_controller_unit_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("unit subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != MODULE_ROOT or source_path.parent != source_root:
        raise ImportError("unit subject escaped the module-owned src directory")

    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        existing_path = Path(getattr(existing, "__file__", "")).resolve(strict=True)
        if existing_path != source_path:
            raise ImportError("unit subject module name is already bound elsewhere")
        return existing

    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the unit subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()
CorruptReleaseStream = SUBJECT.CorruptReleaseStream
DurableAppendError = SUBJECT.DurableAppendError
IdempotencyConflict = SUBJECT.IdempotencyConflict
IllegalTransition = SUBJECT.IllegalTransition
InvalidReleaseCommand = SUBJECT.InvalidReleaseCommand
ReleaseController = SUBJECT.ReleaseController
QuarantinedReleaseCommand = SUBJECT.QuarantinedReleaseCommand
ReleaseError = SUBJECT.ReleaseError
AuthenticatedLedgerHead = SUBJECT._AuthenticatedLedgerHead
TrustedTransitionFacts = SUBJECT.TrustedTransitionFacts
UnauthorizedTransition = SUBJECT.UnauthorizedTransition
sha256 = SUBJECT.sha256


MAIN_STATES = [
    "REQUESTED", "SCOPE_RESOLVED", "INSTRUCTIONS_BOUND",
    "BASELINE_VERIFIED", "CONTRACT_FROZEN", "IMPLEMENTING",
    "CHANGESET_FROZEN", "CANDIDATE_BUILT", "CANDIDATE_VERIFIED",
    "BOM_SIGNED", "SHADOW", "CANARY", "ROLLING", "SOAKING",
    "COMPLETED",
]


def trusted_anchor_for(events, upgrade_id="upgrade-001"):
    sequence = len(events)
    event_sha256 = events[-1]["event_sha256"] if events else "0" * 64
    material = {
        "source_module": "factory-evidence-ledger",
        "upgrade_id": upgrade_id,
        "sequence": sequence,
        "event_sha256": event_sha256,
    }
    return AuthenticatedLedgerHead(
        anchor_id="anchor_" + sha256(material)[:32],
        **material,
    )


def rehash_ledger_event(event):
    event["payload_sha256"] = sha256(event["payload"])
    event["event_sha256"] = sha256({
        key: value for key, value in event.items()
        if key not in {"event_sha256", "append_status"}
    })


def candidate_report():
    return {
        "result": "PASS",
        "validation_kind": "CANDIDATE_BOM_STATIC",
        "verification_ceiling": "INTEGRATION_VERIFIED",
        "schema_sha256": "1" * 64,
        "trust_policy_id": "release-policy-001",
        "bom_id": "candidate-bom-001",
        "bom_sha256": "2" * 64,
        "integration_commit": "3" * 40,
        "artifact_set_sha256": "4" * 64,
        "bom_signer": "controller-service",
        "artifact_signers": ["controller-service"],
        "evidence_signers": ["independent-evidence-issuer"],
        "release_approver": "human-release-approver",
        "simulation_only": False,
        "canary_verified": False,
        "scale_verified": False,
    }


def command(from_state, to_state, key=None):
    return {
        "schema_version": "2.0.0",
        "contract_id": "rollout.command/v2",
        "producer_module": "factory-control-plane-host",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + sha256({
            "fixture_key": key or f"transition-{from_state.lower()}-{to_state.lower()}"
        }),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "upgrade_id": "upgrade-001",
        "from_state": from_state,
        "to_state": to_state,
        "transition_evidence": {
            "evidence_refs": [f"receipt:evidence:state-{to_state.lower()}"]
        },
    }


def legacy_v1_command(from_state="REQUESTED", to_state="SCOPE_RESOLVED"):
    return {
        "schema_version": "1.0.0",
        "contract_id": "rollout.command/v1",
        "producer_module": "factory-upgrade-intake",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + sha256({"legacy": from_state + to_state}),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "upgrade_id": "upgrade-001",
        "from_state": from_state,
        "to_state": to_state,
        "risk_tier": "R3",
        "transition_evidence": {"evidence_refs": [f"evidence:{to_state.lower()}"]},
    }


class DurableLedger:
    def __init__(self):
        self.events = []
        self.fail = False

    def append(self, request):
        if self.fail:
            raise RuntimeError("database unavailable")
        previous = self.events[-1]["event_sha256"] if self.events else "0" * 64
        event = {
            "schema_version": "1.0.0",
            "contract_id": "upgrade.event/v1",
            "producer_module": "factory-evidence-ledger",
            "soul_id": request["soul_id"],
            "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"],
            "trace_id": request["trace_id"],
            "idempotency_key": request["idempotency_key"],
            "occurred_at": request["occurred_at"],
            "privacy_class": "internal",
            "event_id": "event-" + sha256({
                "stream_id": request["stream_id"],
                "idempotency_key": request["idempotency_key"],
            })[:32],
            "stream_id": request["stream_id"],
            "sequence": request["expected_sequence"] + 1,
            "event_type": request["event_type"],
            "source_module": request["producer_module"],
            "payload": copy.deepcopy(request["payload"]),
            "payload_sha256": request["payload_sha256"],
            "previous_event_sha256": previous,
            "append_status": "APPENDED",
        }
        event["event_sha256"] = sha256({
            key: value for key, value in event.items()
            if key not in {"event_sha256", "append_status"}
        })
        self.events.append(copy.deepcopy(event))
        return event


class FactsResolver:
    def __init__(self):
        self.overrides = {}

    def __call__(self, upgrade_id, from_state, to_state, evidence_refs):
        report = candidate_report() if MAIN_STATES.index(to_state) >= MAIN_STATES.index("BOM_SIGNED") else None
        values = {
            "upgrade_id": upgrade_id,
            "resolved_evidence_refs": tuple(evidence_refs),
            "receipt_set_sha256": sha256({
                "resolved_evidence_refs": list(evidence_refs),
                "source": "trusted-fixture",
            }),
            "risk_tier": "R3",
            "evidence_kind": "INTEGRATION",
            "verification_level": "INTEGRATION_VERIFIED",
            "simulation_only": False,
            "side_effect_count": 0,
            "kill_switch_armed": MAIN_STATES.index(to_state) >= MAIN_STATES.index("SHADOW"),
            "observed_bom_sha256": report["bom_sha256"] if report else None,
            "observed_artifact_sha256": report["artifact_set_sha256"] if report else None,
            "candidate_validation": report,
        }
        if to_state == "SHADOW":
            values.update({"evidence_kind": "SIMULATION", "simulation_only": True})
        elif to_state == "CANARY":
            values.update({"evidence_kind": "DEVICE", "verification_level": "DEVICE_VERIFIED"})
        elif to_state in {"ROLLING", "SOAKING", "COMPLETED"}:
            values.update({"evidence_kind": "CANARY", "verification_level": "CANARY_VERIFIED"})
        values.update(self.overrides.get(to_state, {}))
        return TrustedTransitionFacts(**values)


class ReleaseControllerTests(unittest.TestCase):
    def setUp(self):
        self.ledger = DurableLedger()
        self.facts = FactsResolver()
        self.controller = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            self.facts, self.ledger.append,
        )

    def advance_to(self, target):
        for current, following in zip(MAIN_STATES, MAIN_STATES[1:]):
            self.controller.transition(command(current, following))
            if following == target:
                return
        self.fail(f"unknown target {target}")

    def test_full_main_state_path_and_digest_continuity(self):
        self.advance_to("COMPLETED")
        self.assertEqual("COMPLETED", self.controller.state)
        self.assertEqual(len(MAIN_STATES) - 1, self.controller.sequence)
        self.assertEqual("2" * 64, self.controller.locked_bom_sha256)
        self.assertEqual("CANARY", self.ledger.events[-1]["payload"]["evidence_kind"])
        self.assertEqual("rollout.event/v2", self.ledger.events[-1]["payload"]["contract_id"])
        self.assertNotEqual("0" * 64, self.ledger.events[-1]["payload"]["receipt_set_sha256"])

    def test_complete_v1_command_is_quarantine_only_and_writes_nothing(self):
        legacy = legacy_v1_command()
        self.assertEqual(
            "QUARANTINE_ONLY_V1", SUBJECT.classify_rollout_command_major(legacy)
        )
        with self.assertRaisesRegex(QuarantinedReleaseCommand, "quarantine-only"):
            self.controller.transition(legacy)
        self.assertEqual("REQUESTED", self.controller.state)
        self.assertEqual(0, self.controller.sequence)
        self.assertEqual([], self.ledger.events)

    def test_complete_v1_event_is_quarantine_only_and_cannot_replay(self):
        self.controller.transition(command("REQUESTED", "SCOPE_RESOLVED"))
        legacy_ledger_event = copy.deepcopy(self.ledger.events[0])
        legacy_rollout = legacy_ledger_event["payload"]
        legacy_rollout["schema_version"] = "1.0.0"
        legacy_rollout["contract_id"] = "rollout.event/v1"
        legacy_rollout.pop("receipt_set_sha256")
        legacy_ledger_event["payload_sha256"] = sha256(legacy_rollout)
        legacy_ledger_event["event_sha256"] = sha256({
            key: value for key, value in legacy_ledger_event.items()
            if key not in {"event_sha256", "append_status"}
        })
        self.assertEqual(
            "QUARANTINE_ONLY_V1", SUBJECT.classify_rollout_event_major(legacy_rollout)
        )
        with self.assertRaisesRegex(CorruptReleaseStream, "quarantine-only"):
            ReleaseController._recover_after_authenticated_anchor(
                "upgrade-001", "controller-service", ["controller-service"],
                self.facts, self.ledger.append, [legacy_ledger_event],
                trusted_anchor_for([legacy_ledger_event]),
            )

    def test_append_failure_does_not_advance_or_lock_candidate(self):
        self.advance_to("CANDIDATE_VERIFIED")
        before_sequence = self.controller.sequence
        self.ledger.fail = True
        with self.assertRaises(DurableAppendError):
            self.controller.transition(command("CANDIDATE_VERIFIED", "BOM_SIGNED"))
        self.assertEqual("CANDIDATE_VERIFIED", self.controller.state)
        self.assertEqual(before_sequence, self.controller.sequence)
        self.assertIsNone(self.controller.locked_bom_sha256)

    def test_post_authentication_semantic_replay_can_continue(self):
        self.advance_to("BOM_SIGNED")
        anchor = trusted_anchor_for(self.ledger.events)
        recovered = ReleaseController._recover_after_authenticated_anchor(
            "upgrade-001", "controller-service", ["controller-service"],
            self.facts, self.ledger.append, copy.deepcopy(self.ledger.events),
            anchor,
        )
        self.assertEqual("BOM_SIGNED", recovered.state)
        self.assertEqual(self.controller.sequence, recovered.sequence)
        recovered.transition(command("BOM_SIGNED", "SHADOW"))
        self.assertEqual("SHADOW", recovered.state)

    def test_public_recovery_is_disabled_until_external_authentication_exists(self):
        self.controller.transition(command("REQUESTED", "SCOPE_RESOLVED"))
        events = copy.deepcopy(self.ledger.events)

        with self.assertRaisesRegex(CorruptReleaseStream, "WAITING_EXTERNAL"):
            ReleaseController.recover(
                "upgrade-001", "controller-service", ["controller-service"],
                self.facts, self.ledger.append, events,
            )

        wrong_head = AuthenticatedLedgerHead(
            anchor_id="anchor_" + "1" * 32,
            source_module="factory-evidence-ledger",
            upgrade_id="upgrade-001",
            sequence=1,
            event_sha256="9" * 64,
        )
        with self.assertRaisesRegex(CorruptReleaseStream, "commitment"):
            ReleaseController._recover_after_authenticated_anchor(
                "upgrade-001", "controller-service", ["controller-service"],
                self.facts, self.ledger.append, events,
                wrong_head,
            )

    def test_recomputed_hashes_do_not_authorize_tampered_canary_replay(self):
        self.advance_to("CANARY")
        original_events = copy.deepcopy(self.ledger.events)
        external_anchor = trusted_anchor_for(original_events)

        semantic_attacks = (
            ("evidence_kind", "INTEGRATION", "canary rollout evidence"),
            ("verification_level", "INTEGRATION_VERIFIED", "canary rollout evidence"),
            ("side_effect_count", True, "side-effect count"),
            ("side_effect_count", -1, "side-effect count"),
        )
        for field, value, message in semantic_attacks:
            with self.subTest(field=field, value=value):
                tampered = copy.deepcopy(original_events)
                tampered[-1]["payload"][field] = value
                rehash_ledger_event(tampered[-1])
                with self.assertRaisesRegex(ReleaseError, message):
                    ReleaseController._recover_after_authenticated_anchor(
                        "upgrade-001", "controller-service", ["controller-service"],
                        self.facts, self.ledger.append, tampered,
                        external_anchor,
                    )

        locally_self_consistent = copy.deepcopy(original_events)
        locally_self_consistent[-1]["payload"]["side_effect_count"] = 1
        rehash_ledger_event(locally_self_consistent[-1])
        with self.assertRaisesRegex(CorruptReleaseStream, "external anchor"):
            ReleaseController._recover_after_authenticated_anchor(
                "upgrade-001", "controller-service", ["controller-service"],
                self.facts, self.ledger.append, locally_self_consistent,
                external_anchor,
            )

        forged_local_head = trusted_anchor_for(locally_self_consistent)
        self.assertEqual(
            locally_self_consistent[-1]["event_sha256"],
            forged_local_head.event_sha256,
        )
        with self.assertRaisesRegex(CorruptReleaseStream, "WAITING_EXTERNAL"):
            ReleaseController.recover(
                "upgrade-001", "controller-service", ["controller-service"],
                self.facts, self.ledger.append, locally_self_consistent,
            )

    def test_request_cannot_report_role_approval_or_trusted_pass(self):
        attack = command("REQUESTED", "SCOPE_RESOLVED")
        attack["actor_role"] = "release-controller"
        attack["approval"] = True
        attack["required_checks"] = ["all-pass"]
        with self.assertRaises(InvalidReleaseCommand):
            self.controller.transition(attack)
        attack = command("REQUESTED", "SCOPE_RESOLVED")
        attack["transition_evidence"] = {"evidence_refs": [], "result": "PASS"}
        with self.assertRaises(InvalidReleaseCommand):
            self.controller.transition(attack)
        attack = command("REQUESTED", "SCOPE_RESOLVED")
        attack["risk_tier"] = "R0"
        with self.assertRaises(InvalidReleaseCommand):
            self.controller.transition(attack)
        self.assertEqual([], self.ledger.events)

    def test_raw_upgrade_intent_and_masquerading_receipts_write_nothing(self):
        for major in (1, 2):
            with self.subTest(location="top-level", major=major):
                raw_intent = {
                    "schema_version": f"{major}.0.0",
                    "contract_id": f"upgrade.intent/v{major}",
                    "producer_module": "factory-upgrade-intake",
                    "upgrade_id": "upgrade-001",
                }
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(raw_intent)
                self.assertEqual([], self.ledger.events)

            with self.subTest(location="nested", major=major):
                nested = command("REQUESTED", "SCOPE_RESOLVED")
                nested["transition_evidence"]["upgrade_intent"] = {
                    "contract_id": f"upgrade.intent/v{major}",
                    "upgrade_id": "upgrade-001",
                }
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(nested)
                self.assertEqual([], self.ledger.events)

            with self.subTest(location="receipt-object", major=major):
                receipt_object = command("REQUESTED", "SCOPE_RESOLVED")
                receipt_object["transition_evidence"]["evidence_refs"] = [{
                    "contract_id": f"upgrade.intent/v{major}",
                    "upgrade_id": "upgrade-001",
                }]
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(receipt_object)
                self.assertEqual([], self.ledger.events)

        for receipt_kind in (
            "upgrade.intent", "upgrade.intent.v2", "upgrade-intent",
            "upgrade--intent", "dps.upgrade-intent",
        ):
            with self.subTest(receipt_kind=receipt_kind):
                masquerading = command("REQUESTED", "SCOPE_RESOLVED")
                masquerading["transition_evidence"]["evidence_refs"] = [
                    f"receipt:{receipt_kind}:upgrade-001"
                ]
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(masquerading)
                self.assertEqual([], self.ledger.events)

    def test_receipt_reference_normalization_and_separator_attacks_fail_closed(self):
        invalid_references = (
            "receipt:upgrade--intent",
            "receipt:upgrade--intent:upgrade-001",
            "receipt:evidence:bad--identifier",
            "receipt:evidence:bad..identifier",
            "receipt:evidence:bad__identifier",
            "receipt:evidence:-leading-001",
            "receipt:evidence:trailing-001-",
            "receipt:Evidence:state-scope_resolved",
            "receipt:evidence:State-scope-resolved",
            "receipt:evidence:state:scope-resolved",
        )
        for reference in invalid_references:
            with self.subTest(reference=reference):
                invalid = command("REQUESTED", "SCOPE_RESOLVED")
                invalid["transition_evidence"]["evidence_refs"] = [reference]
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(invalid)
                self.assertEqual([], self.ledger.events)

    def test_upgrade_identity_and_time_are_strict_canonical_values(self):
        invalid_upgrade_ids = (
            "Upgrade-001", "upgrade--001", "upgrade..001", "upgrade__001",
            "short", "upgrade-001-", "upgrade:001::next",
        )
        for upgrade_id in invalid_upgrade_ids:
            with self.subTest(upgrade_id=upgrade_id):
                invalid = command("REQUESTED", "SCOPE_RESOLVED")
                invalid["upgrade_id"] = upgrade_id
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(invalid)
                self.assertEqual([], self.ledger.events)

        invalid_times = (
            "2026-07-14T00:00:00+00:00",
            "2026-07-14T00:00:00.000Z",
            "2026-02-30T00:00:00Z",
            "2026-07-14t00:00:00Z",
            "2026-07-14T00:00:00z",
            "2026-07-14T00:00:00Z\n",
        )
        for occurred_at in invalid_times:
            with self.subTest(occurred_at=repr(occurred_at)):
                invalid = command("REQUESTED", "SCOPE_RESOLVED")
                invalid["occurred_at"] = occurred_at
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(invalid)
                self.assertEqual([], self.ledger.events)

    def test_strict_wire_parser_rejects_duplicate_members_and_invalid_json(self):
        compact = json.dumps(
            command("REQUESTED", "SCOPE_RESOLVED"),
            sort_keys=True,
            separators=(",", ":"),
        )
        duplicate_root = compact[:-1] + ',"upgrade_id":"upgrade-001"}'
        duplicate_nested = compact.replace(
            '"transition_evidence":{"evidence_refs":',
            '"transition_evidence":{"evidence_refs":[],"evidence_refs":',
            1,
        )
        invalid_wires = (
            duplicate_root,
            duplicate_nested,
            '{"schema_version":NaN}',
            "[]",
            b'{"schema_version":"2.0.0"}\xff',
        )
        for wire in invalid_wires:
            with self.subTest(wire=repr(wire)[:80]):
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(wire)
                self.assertEqual([], self.ledger.events)

        event = self.controller.transition(compact.encode("utf-8"))
        self.assertEqual("SCOPE_RESOLVED", event["current_state"])

    def test_side_effect_count_requires_an_exact_nonnegative_integer(self):
        for invalid_count in (True, -1, 0.0):
            with self.subTest(side_effect_count=invalid_count):
                self.facts.overrides["SCOPE_RESOLVED"] = {
                    "side_effect_count": invalid_count,
                }
                with self.assertRaisesRegex(UnauthorizedTransition, "side-effect count"):
                    self.controller.transition(command("REQUESTED", "SCOPE_RESOLVED"))
                self.assertEqual([], self.ledger.events)

    def test_receipt_references_are_nonempty_unique_and_bounded_before_append(self):
        invalid_sets = [
            [],
            ["receipt:evidence:duplicate-001", "receipt:evidence:duplicate-001"],
            [f"receipt:evidence:bounded-{index:03d}" for index in range(65)],
        ]
        for references in invalid_sets:
            with self.subTest(reference_count=len(references)):
                invalid = command("REQUESTED", "SCOPE_RESOLVED")
                invalid["transition_evidence"]["evidence_refs"] = references
                with self.assertRaises(InvalidReleaseCommand):
                    self.controller.transition(invalid)
                self.assertEqual([], self.ledger.events)

    def test_trusted_receipt_reference_or_digest_drift_writes_nothing(self):
        self.facts.overrides["SCOPE_RESOLVED"] = {
            "resolved_evidence_refs": ("receipt:evidence:different-001",),
        }
        with self.assertRaisesRegex(UnauthorizedTransition, "references drifted"):
            self.controller.transition(command("REQUESTED", "SCOPE_RESOLVED"))
        self.assertEqual([], self.ledger.events)

        self.facts.overrides["SCOPE_RESOLVED"] = {
            "receipt_set_sha256": "0" * 64,
        }
        with self.assertRaisesRegex(UnauthorizedTransition, "receipt set digest"):
            self.controller.transition(command("REQUESTED", "SCOPE_RESOLVED"))
        self.assertEqual([], self.ledger.events)

    def test_unsigned_or_self_approved_candidate_is_rejected(self):
        self.advance_to("CANDIDATE_VERIFIED")
        self.facts.overrides["BOM_SIGNED"] = {"candidate_validation": None}
        with self.assertRaisesRegex(UnauthorizedTransition, "trusted candidate-validator"):
            self.controller.transition(command("CANDIDATE_VERIFIED", "BOM_SIGNED"))
        self.facts.overrides["BOM_SIGNED"] = {
            "candidate_validation": {**candidate_report(), "release_approver": "controller-service"}
        }
        with self.assertRaisesRegex(UnauthorizedTransition, "distinct"):
            self.controller.transition(command("CANDIDATE_VERIFIED", "BOM_SIGNED"))

    def test_digest_drift_shadow_side_effect_and_unarmed_kill_switch_stop(self):
        self.advance_to("BOM_SIGNED")
        self.facts.overrides["SHADOW"] = {"observed_artifact_sha256": "9" * 64}
        with self.assertRaisesRegex(UnauthorizedTransition, "drifted"):
            self.controller.transition(command("BOM_SIGNED", "SHADOW"))
        self.facts.overrides["SHADOW"] = {"side_effect_count": 1}
        with self.assertRaisesRegex(UnauthorizedTransition, "side effect"):
            self.controller.transition(command("BOM_SIGNED", "SHADOW"))
        self.facts.overrides["SHADOW"] = {"kill_switch_armed": False}
        with self.assertRaisesRegex(UnauthorizedTransition, "kill switch"):
            self.controller.transition(command("BOM_SIGNED", "SHADOW"))

    def test_simulation_cannot_authorize_canary(self):
        self.advance_to("SHADOW")
        self.facts.overrides["CANARY"] = {
            "evidence_kind": "SIMULATION",
            "verification_level": "INTEGRATION_VERIFIED",
            "simulation_only": True,
        }
        with self.assertRaisesRegex(UnauthorizedTransition, "Simulation|simulation"):
            self.controller.transition(command("SHADOW", "CANARY"))
        self.assertEqual("SHADOW", self.controller.state)

    def test_illegal_jump_and_idempotency_conflict_fail_closed(self):
        with self.assertRaises(IllegalTransition):
            self.controller.transition(command("REQUESTED", "COMPLETED"))
        first_command = command("REQUESTED", "SCOPE_RESOLVED", "same-key")
        first = self.controller.transition(first_command)
        self.assertEqual(first, self.controller.transition(first_command))
        conflicting = copy.deepcopy(first_command)
        conflicting["transition_evidence"] = {
            "evidence_refs": ["receipt:evidence:different-001"]
        }
        with self.assertRaises(IdempotencyConflict):
            self.controller.transition(conflicting)


if __name__ == "__main__":
    unittest.main()
