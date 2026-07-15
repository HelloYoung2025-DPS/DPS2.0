from __future__ import annotations

import copy
import importlib.util
import sys
import unittest
from pathlib import Path


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "rollback_controller.py"
SUBJECT_NAME = "_dps_factory_rollback_controller_unit_subject"


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
AuthorizationRejected = SUBJECT.AuthorizationRejected
ConflictingRollbackRequest = SUBJECT.ConflictingRollbackRequest
CorruptEvidenceStream = SUBJECT.CorruptEvidenceStream
EvidenceWriteFailed = SUBJECT.EvidenceWriteFailed
InvalidRollbackRequest = SUBJECT.InvalidRollbackRequest
InvalidStepReceipt = SUBJECT.InvalidStepReceipt
NON_ROLLBACKABLE_STEPS = SUBJECT.NON_ROLLBACKABLE_STEPS
ROLLBACKABLE_STEPS = SUBJECT.ROLLBACKABLE_STEPS
RollbackController = SUBJECT.RollbackController
RollbackStep = SUBJECT.RollbackStep
StableBomRejected = SUBJECT.StableBomRejected
StepOutcome = SUBJECT.StepOutcome
StepReceipt = SUBJECT.StepReceipt
VerifiedRollbackGrant = SUBJECT.VerifiedRollbackGrant
VerifiedStableBom = SUBJECT.VerifiedStableBom
canonical_bytes = SUBJECT.canonical_bytes
sha256 = SUBJECT.sha256


CURRENT_BOM = "1" * 64
PREVIOUS_BOM = "2" * 64
FIXED_NOW = "2026-07-14T00:00:00Z"


class FakeClock:
    def __init__(self) -> None:
        self.value = 1000.0

    def now(self) -> float:
        return self.value

    def advance(self, seconds: float) -> None:
        self.value += seconds


class FakeLedger:
    def __init__(self) -> None:
        self.streams: dict[str, list[dict]] = {}
        self.fail_once_event_type: str | None = None

    def read_stream(self, stream_id: str):
        return copy.deepcopy(self.streams.get(stream_id, []))

    def append(self, command):
        if self.fail_once_event_type == command["event_type"]:
            self.fail_once_event_type = None
            raise OSError("injected durable append failure")
        stream = self.streams.setdefault(command["stream_id"], [])
        if command["expected_sequence"] != len(stream):
            raise ValueError("optimistic sequence mismatch")
        previous = stream[-1]["event_sha256"] if stream else "0" * 64
        event = {
            "schema_version": "1.0.0",
            "contract_id": "upgrade.event/v1",
            "producer_module": "factory-evidence-ledger",
            "soul_id": command["soul_id"],
            "device_binding_id": command["device_binding_id"],
            "platform_account_id": command["platform_account_id"],
            "trace_id": command["trace_id"],
            "idempotency_key": command["idempotency_key"],
            "occurred_at": command["occurred_at"],
            "privacy_class": "internal",
            "event_id": "event-" + sha256(
                {"stream_id": command["stream_id"], "idempotency_key": command["idempotency_key"]}
            )[:32],
            "stream_id": command["stream_id"],
            "sequence": len(stream) + 1,
            "event_type": command["event_type"],
            "source_module": "factory-rollback-controller",
            "payload": copy.deepcopy(command["payload"]),
            "payload_sha256": command["payload_sha256"],
            "previous_event_sha256": previous,
            "append_status": "APPENDED",
        }
        material = dict(event)
        material.pop("append_status")
        event["event_sha256"] = sha256(material)
        stream.append(event)
        return copy.deepcopy(event)

    def rehash(self, stream_id: str) -> None:
        previous = "0" * 64
        for sequence, event in enumerate(self.streams[stream_id], start=1):
            event["sequence"] = sequence
            event["previous_event_sha256"] = previous
            event["payload_sha256"] = sha256(event["payload"])
            material = dict(event)
            material.pop("append_status")
            material.pop("event_sha256", None)
            event["event_sha256"] = sha256(material)
            previous = event["event_sha256"]


class FakeStableBomVerifier:
    def __init__(self) -> None:
        self.override: VerifiedStableBom | object | None = None

    def verify(self, bom_id: str, bom_sha256: str):
        if self.override is not None:
            return self.override
        return VerifiedStableBom(
            bom_id=bom_id,
            bom_sha256=bom_sha256,
            verification_id="verify-stable-0001",
            signer_identity="release-bom-signer",
        )


class FakeAuthority:
    def __init__(self) -> None:
        self.approver_identity = "human-release-approver"
        self.authorization_kind = "HUMAN_R3"
        self.target_override: str | None = None
        self.rollout_override: str | None = None

    def authorize(
        self,
        *,
        rollback_id,
        upgrade_id,
        rollout_event_id,
        request_sha256,
        plan_sha256,
        target_bom_sha256,
        rollback_unit,
        ordered_steps,
    ):
        return VerifiedRollbackGrant(
            authorization_id="approval-rollback-0001",
            authorization_kind=self.authorization_kind,
            approver_identity=self.approver_identity,
            rollback_id=rollback_id,
            upgrade_id=upgrade_id,
            rollout_event_id=self.rollout_override or rollout_event_id,
            request_sha256=request_sha256,
            plan_sha256=plan_sha256,
            target_bom_sha256=self.target_override or target_bom_sha256,
            rollback_unit=rollback_unit,
            allowed_steps=ordered_steps,
        )


class FakeExecutor:
    def __init__(self, clock: FakeClock) -> None:
        self.clock = clock
        self.calls = []
        self.seconds_per_call = 0.0
        self.outcome_overrides: dict[RollbackStep, StepOutcome] = {}
        self.incomplete_drain = False
        self.incomplete_reconcile = False
        self.active_digest_override: dict[RollbackStep, str] = {}
        self.raise_on: RollbackStep | None = None

    def execute(self, instruction):
        self.calls.append(instruction)
        self.clock.advance(self.seconds_per_call)
        if self.raise_on is instruction.step:
            raise RuntimeError("injected executor crash")
        outcome = self.outcome_overrides.get(instruction.step, StepOutcome.PASS)
        active = self.active_digest_override.get(instruction.step)
        if active is None:
            if instruction.step in {RollbackStep.SWITCH_BOM, RollbackStep.VERIFY}:
                active = (
                    instruction.target_bom_sha256
                    if instruction.step is RollbackStep.SWITCH_BOM or not instruction.external_effects
                    else instruction.current_bom_sha256
                )
            else:
                active = instruction.current_bom_sha256
        compensation_ids = (
            ("compensation-evidence-0001",) if instruction.step is RollbackStep.COMPENSATE else ()
        )
        compensated_digest = (
            sha256(list(instruction.external_effects)) if instruction.step is RollbackStep.COMPENSATE else None
        )
        return StepReceipt(
            receipt_id=f"receipt-{instruction.step.value.lower()}",
            step=instruction.step,
            outcome=outcome,
            native_result_verified=outcome is StepOutcome.PASS,
            postcondition_verified=outcome is StepOutcome.PASS,
            drain_complete=instruction.step is RollbackStep.DRAIN and not self.incomplete_drain,
            reconciliation_complete=instruction.step is RollbackStep.RECONCILE and not self.incomplete_reconcile,
            active_bom_sha256=active,
            compensation_evidence_ids=compensation_ids,
            compensated_effects_sha256=compensated_digest,
            reason=None if outcome is StepOutcome.PASS else "INJECTED_NON_PASS_OUTCOME",
        )


def request(*, unit: str = "ROLLBACKABLE", rollback_id: str = "rollback-0001") -> dict:
    effects = [] if unit == "ROLLBACKABLE" else ["message:owned-fixture:0001"]
    return {
        "schema_version": "1.0.0",
        "contract_id": "rollback.request/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + sha256({"rollback_id": rollback_id, "purpose": "request"}),
        "occurred_at": FIXED_NOW,
        "privacy_class": "internal",
        "rollback_id": rollback_id,
        "upgrade_id": "upgrade-0001",
        "rollout_event_id": "rollout-" + "a" * 32,
        "rollback_unit": unit,
        "current_bom_sha256": CURRENT_BOM,
        "previous_stable_bom_id": "stable-bom-0001",
        "previous_stable_bom_sha256": PREVIOUS_BOM,
        "deadline_seconds": 300,
        "external_effects": effects,
        "compensation_plan": None if unit == "ROLLBACKABLE" else "compensation-plan-0001",
    }


class RollbackControllerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.clock = FakeClock()
        self.ledger = FakeLedger()
        self.verifier = FakeStableBomVerifier()
        self.authority = FakeAuthority()
        self.executor = FakeExecutor(self.clock)
        self.controller = RollbackController(
            controller_identity="rollback-controller-01",
            stable_bom_verifier=self.verifier,
            authority=self.authority,
            executor=self.executor,
            evidence_ledger=self.ledger,
            logical_clock=self.clock.now,
            utc_now=lambda: FIXED_NOW,
        )

    def test_exact_rollback_sequence_is_durable_and_idempotent(self):
        value = request()
        result = self.controller.execute(value)
        self.assertEqual("ROLLED_BACK", result["outcome"])
        self.assertEqual([step.value for step in ROLLBACKABLE_STEPS], result["completed_steps"])
        self.assertEqual(PREVIOUS_BOM, result["active_bom_sha256"])
        self.assertTrue(result["verified_postconditions"])
        self.assertEqual(list(ROLLBACKABLE_STEPS), [call.step for call in self.executor.calls])
        events = self.ledger.read_stream("rollback:rollback-0001")
        self.assertEqual("ROLLBACK_RESULT_RECORDED", events[-1]["event_type"])

        call_count = len(self.executor.calls)
        replay = self.controller.execute(copy.deepcopy(value))
        self.assertEqual(result, replay)
        self.assertEqual(call_count, len(self.executor.calls))
        self.assertEqual(len(events), len(self.ledger.read_stream("rollback:rollback-0001")))

    def test_external_effects_are_compensated_and_never_called_rolled_back(self):
        result = self.controller.execute(request(unit="NON_ROLLBACKABLE"))
        self.assertEqual("COMPENSATED", result["outcome"])
        self.assertEqual([step.value for step in NON_ROLLBACKABLE_STEPS], result["completed_steps"])
        self.assertEqual(CURRENT_BOM, result["active_bom_sha256"])
        self.assertEqual(["compensation-evidence-0001"], result["compensation_evidence_ids"])
        self.assertEqual("EXTERNAL_EFFECTS_COMPENSATED_NOT_ROLLED_BACK", result["reason"])
        self.assertNotIn(RollbackStep.SWITCH_BOM, [call.step for call in self.executor.calls])

    def test_request_cannot_self_report_approval_signature_or_role(self):
        for field in ("approved", "signed", "actor_role", "shell_command"):
            with self.subTest(field=field):
                value = request(rollback_id=f"rollback-{field}")
                value[field] = True
                with self.assertRaises(InvalidRollbackRequest):
                    self.controller.execute(value)
        bad = request(rollback_id="rollback-wrong-producer")
        bad["producer_module"] = "attacker"
        with self.assertRaises(InvalidRollbackRequest):
            self.controller.execute(bad)
        hidden_effect = request(rollback_id="rollback-hidden-effect")
        hidden_effect["external_effects"] = ["public-message"]
        with self.assertRaises(InvalidRollbackRequest):
            self.controller.execute(hidden_effect)
        executable_compensation = request(unit="NON_ROLLBACKABLE", rollback_id="rollback-text-plan")
        executable_compensation["compensation_plan"] = "run a shell command now"
        with self.assertRaises(InvalidRollbackRequest):
            self.controller.execute(executable_compensation)
        raw_effect = request(unit="NON_ROLLBACKABLE", rollback_id="rollback-raw-effect")
        raw_effect["external_effects"] = ["a public message containing user text"]
        with self.assertRaises(InvalidRollbackRequest):
            self.controller.execute(raw_effect)

    def test_external_verifier_and_process_bound_authority_are_exact(self):
        self.verifier.override = VerifiedStableBom(
            bom_id="different-bom",
            bom_sha256=PREVIOUS_BOM,
            verification_id="verify-stable-0001",
            signer_identity="release-bom-signer",
        )
        with self.assertRaises(StableBomRejected):
            self.controller.execute(request(rollback_id="rollback-bad-bom"))

        self.verifier.override = None
        self.authority.approver_identity = "rollback-controller-01"
        with self.assertRaises(AuthorizationRejected):
            self.controller.execute(request(rollback_id="rollback-self-approval"))
        self.authority.approver_identity = "human-release-approver"
        self.authority.target_override = "9" * 64
        with self.assertRaises(AuthorizationRejected):
            self.controller.execute(request(rollback_id="rollback-wrong-scope"))
        self.authority.target_override = None
        self.authority.rollout_override = "rollout-" + "f" * 32
        with self.assertRaises(AuthorizationRejected):
            self.controller.execute(request(rollback_id="rollback-wrong-rollout"))

    def test_deadline_is_bounded_and_checked_with_logical_clock(self):
        invalid = request(rollback_id="rollback-deadline-invalid")
        invalid["deadline_seconds"] = 301
        with self.assertRaises(InvalidRollbackRequest):
            self.controller.execute(invalid)

        value = request(rollback_id="rollback-deadline")
        value["deadline_seconds"] = 2
        self.executor.seconds_per_call = 3
        result = self.controller.execute(value)
        self.assertEqual("DEADLINE_EXCEEDED", result["outcome"])
        self.assertEqual([RollbackStep.STOP_ROUTING.value], result["completed_steps"])
        self.assertFalse(result["verified_postconditions"])

    def test_unknown_outcome_is_recorded_and_never_retried(self):
        value = request(rollback_id="rollback-unknown")
        self.executor.outcome_overrides[RollbackStep.DRAIN] = StepOutcome.UNKNOWN_OUTCOME
        result = self.controller.execute(value)
        self.assertEqual("FAILED", result["outcome"])
        self.assertEqual([RollbackStep.STOP_ROUTING.value], result["completed_steps"])
        drain_calls = sum(call.step is RollbackStep.DRAIN for call in self.executor.calls)
        replay = self.controller.execute(value)
        self.assertEqual(result, replay)
        self.assertEqual(drain_calls, sum(call.step is RollbackStep.DRAIN for call in self.executor.calls))

    def test_crash_after_started_event_becomes_unknown_without_retry(self):
        value = request(rollback_id="rollback-crash-window")
        self.executor.raise_on = RollbackStep.DRAIN
        with self.assertRaises(InvalidStepReceipt):
            self.controller.execute(value)
        drain_calls = sum(call.step is RollbackStep.DRAIN for call in self.executor.calls)
        self.executor.raise_on = None
        recovered = self.controller.execute(value)
        self.assertEqual("FAILED", recovered["outcome"])
        self.assertIn("UNKNOWN_OUTCOME_AFTER_INTERRUPTION:DRAIN", recovered["reason"])
        self.assertEqual(drain_calls, sum(call.step is RollbackStep.DRAIN for call in self.executor.calls))

    def test_incomplete_drain_reconcile_or_digest_cannot_pass(self):
        cases = (
            ("rollback-incomplete-drain", "incomplete_drain", True),
            ("rollback-incomplete-reconcile", "incomplete_reconcile", True),
        )
        for rollback_id, attribute, setting in cases:
            with self.subTest(rollback_id=rollback_id):
                clock = FakeClock()
                executor = FakeExecutor(clock)
                setattr(executor, attribute, setting)
                controller = RollbackController(
                    controller_identity="rollback-controller-01",
                    stable_bom_verifier=FakeStableBomVerifier(),
                    authority=FakeAuthority(),
                    executor=executor,
                    evidence_ledger=FakeLedger(),
                    logical_clock=clock.now,
                    utc_now=lambda: FIXED_NOW,
                )
                result = controller.execute(request(rollback_id=rollback_id))
                self.assertEqual("FAILED", result["outcome"])

        self.executor.active_digest_override[RollbackStep.SWITCH_BOM] = "9" * 64
        mismatch = self.controller.execute(request(rollback_id="rollback-digest-mismatch"))
        self.assertEqual("FAILED", mismatch["outcome"])
        self.assertNotEqual("ROLLED_BACK", mismatch["outcome"])

    def test_conflicting_request_is_quarantined_with_hashes_only(self):
        original = request(rollback_id="rollback-conflict")
        self.controller.execute(original)
        conflicting = copy.deepcopy(original)
        conflicting["current_bom_sha256"] = "7" * 64
        with self.assertRaises(ConflictingRollbackRequest):
            self.controller.execute(conflicting)
        stream = self.ledger.read_stream("rollback:rollback-conflict")
        quarantine = stream[-1]
        self.assertEqual("ROLLBACK_CONFLICT_QUARANTINED", quarantine["event_type"])
        self.assertEqual(
            {
                "rollback_id",
                "accepted_request_sha256",
                "conflicting_request_sha256",
                "reason_code",
            },
            set(quarantine["payload"]),
        )
        self.assertNotIn(CURRENT_BOM.encode(), canonical_bytes(quarantine["payload"]))
        count = len(stream)
        with self.assertRaises(ConflictingRollbackRequest):
            self.controller.execute(conflicting)
        self.assertEqual(count, len(self.ledger.read_stream("rollback:rollback-conflict")))

    def test_terminal_append_must_succeed_before_completion(self):
        value = request(rollback_id="rollback-terminal-append")
        self.ledger.fail_once_event_type = "ROLLBACK_RESULT_RECORDED"
        with self.assertRaises(EvidenceWriteFailed):
            self.controller.execute(value)
        calls = len(self.executor.calls)
        stream = self.ledger.read_stream("rollback:rollback-terminal-append")
        self.assertNotEqual("ROLLBACK_RESULT_RECORDED", stream[-1]["event_type"])

        result = self.controller.execute(value)
        self.assertEqual("ROLLED_BACK", result["outcome"])
        self.assertEqual(calls, len(self.executor.calls))
        self.assertEqual(
            "ROLLBACK_RESULT_RECORDED",
            self.ledger.read_stream("rollback:rollback-terminal-append")[-1]["event_type"],
        )

    def test_tampered_evidence_stream_fails_closed(self):
        value = request(rollback_id="rollback-tamper")
        self.controller.execute(value)
        self.ledger.streams["rollback:rollback-tamper"][0]["payload"]["request_sha256"] = "0" * 64
        with self.assertRaises(CorruptEvidenceStream):
            self.controller.execute(value)

    def test_rehashed_false_success_and_reordered_steps_still_fail_closed(self):
        value = request(rollback_id="rollback-false-success")
        self.executor.outcome_overrides[RollbackStep.DRAIN] = StepOutcome.FAIL
        self.controller.execute(value)
        stream_id = "rollback:rollback-false-success"
        terminal = self.ledger.streams[stream_id][-1]["payload"]["result"]
        terminal["outcome"] = "ROLLED_BACK"
        terminal["verified_postconditions"] = True
        terminal["completed_steps"] = [step.value for step in ROLLBACKABLE_STEPS]
        terminal["active_bom_sha256"] = PREVIOUS_BOM
        terminal["reason"] = None
        self.ledger.rehash(stream_id)
        with self.assertRaises(CorruptEvidenceStream):
            self.controller.execute(value)

        clock = FakeClock()
        ledger = FakeLedger()
        executor = FakeExecutor(clock)
        controller = RollbackController(
            controller_identity="rollback-controller-01",
            stable_bom_verifier=FakeStableBomVerifier(),
            authority=FakeAuthority(),
            executor=executor,
            evidence_ledger=ledger,
            logical_clock=clock.now,
            utc_now=lambda: FIXED_NOW,
        )
        ordered_value = request(rollback_id="rollback-reordered")
        controller.execute(ordered_value)
        ordered_stream = ledger.streams["rollback:rollback-reordered"]
        # Swap the STOP_ROUTING and DRAIN start/observation pairs, then rebuild a
        # perfectly continuous hash chain. Semantic order must still reject it.
        ordered_stream[1:5] = ordered_stream[3:5] + ordered_stream[1:3]
        ledger.rehash("rollback:rollback-reordered")
        with self.assertRaises(CorruptEvidenceStream):
            controller.execute(ordered_value)


if __name__ == "__main__":
    unittest.main()
