import base64
import copy
import hashlib
import hmac
import importlib.util
import json
import os
import sys
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "evidence_ledger.py"
SUBJECT_NAME = "_dps_factory_evidence_ledger_unit_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("unit subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != MODULE_ROOT or source_path.parent != source_root:
        raise ImportError("unit subject escaped the module-owned src directory")
    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        if Path(getattr(existing, "__file__", "")).resolve(strict=True) != source_path:
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
AppendAuthorizationError = SUBJECT.AppendAuthorizationError
AppendCandidate = SUBJECT.AppendCandidate
CorruptEventStream = SUBJECT.CorruptEventStream
DevelopmentAppendAuthority = SUBJECT.DevelopmentAppendAuthority
EvidenceLedger = SUBJECT.EvidenceLedger
ExternalAppendAuthority = SUBJECT.ExternalAppendAuthority
ExternalAuthorizationRequired = SUBJECT.ExternalAuthorizationRequired
IdempotencyConflict = SUBJECT.IdempotencyConflict
InMemoryEvidenceRepository = SUBJECT.InMemoryEvidenceRepository
SequenceConflict = SUBJECT.SequenceConflict
canonical_bytes = SUBJECT.canonical_bytes
sha256 = SUBJECT.sha256


def command(payload=None, expected_sequence=0, key="append-001", **changes):
    payload = payload if payload is not None else {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"}
    value = {
        "schema_version": "1.0.0",
        "contract_id": "upgrade.event.append/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + sha256({"fixture_key": key}),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "stream_id": "upgrade-001",
        "expected_sequence": expected_sequence,
        "event_type": "STATE_TRANSITIONED",
        "payload": payload,
        "payload_sha256": sha256(payload),
    }
    value.update(changes)
    return value


def append(ledger, authority, value):
    return ledger.append(authority.issue(canonical_bytes(value)))


def external_authorization(raw, key, *, epoch=7, issued_at=None, expires_at=None, **changes):
    now = int(time.time())
    command_value = json.loads(raw)
    unsigned = {
        "schema_version": "dps.factory-evidence-append-auth/v1",
        "issuer": "dps-factory-auth-service",
        "audience": "factory-evidence-ledger",
        "scope": "factory:evidence:append",
        "producer_module": command_value["producer_module"],
        "command_sha256": hashlib.sha256(raw).hexdigest(),
        "issued_at": now if issued_at is None else issued_at,
        "expires_at": now + 60 if expires_at is None else expires_at,
        "revocation_epoch": epoch,
        "nonce": "auth_" + "a" * 32,
        "key_id": "factory-evidence-append-v1",
    }
    unsigned.update(changes)
    order = (
        "schema_version", "issuer", "audience", "scope", "producer_module",
        "command_sha256", "issued_at", "expires_at", "revocation_epoch", "nonce", "key_id",
    )
    material = "|".join(str(unsigned[field]) for field in order).encode("utf-8")
    value = dict(unsigned)
    value["signature"] = hmac.new(key, material, hashlib.sha256).hexdigest()
    return canonical_bytes(value)


class EvidenceLedgerTests(unittest.TestCase):
    def setUp(self):
        self.authority = DevelopmentAppendAuthority.for_local_tests()
        self.repository = InMemoryEvidenceRepository(self.authority)
        self.ledger = EvidenceLedger(self.repository, self.authority)

    def test_authenticated_append_and_idempotent_replay(self):
        capability = self.authority.issue(canonical_bytes(command()))
        first = self.ledger.append(capability)
        second = self.ledger.append(capability)
        self.assertEqual("APPENDED", first["append_status"])
        self.assertEqual("IDEMPOTENT_REPLAY", second["append_status"])
        self.assertEqual(first["event_id"], second["event_id"])
        self.assertEqual(1, len(self.ledger.read_stream("upgrade-001")))

    def test_mapping_json_roundtrip_copy_and_cross_authority_fail(self):
        value = command()
        capability = self.authority.issue(canonical_bytes(value))
        with self.assertRaises(AppendAuthorizationError):
            self.ledger.append(json.loads(canonical_bytes(value)))
        with self.assertRaises(AppendAuthorizationError):
            self.ledger.append(lambda: capability)
        with self.assertRaises(TypeError):
            copy.copy(capability)
        with self.assertRaises(TypeError):
            json.dumps(capability)
        with self.assertRaises(TypeError):
            copy.copy(self.authority)
        other = DevelopmentAppendAuthority.for_local_tests()
        other_ledger = EvidenceLedger(InMemoryEvidenceRepository(other), other)
        with self.assertRaises(AppendAuthorizationError):
            other_ledger.append(capability)

    def test_repository_revalidates_and_rejects_raw_swap(self):
        capability = self.authority.issue(canonical_bytes(command()))
        stored = self.ledger.append(capability)
        swapped = command(key="other")
        candidate = AppendCandidate(
            canonical_bytes(swapped),
            canonical_bytes(stored),
            hashlib.sha256(canonical_bytes(swapped)).hexdigest(),
            0,
            capability,
        )
        with self.assertRaises(AppendAuthorizationError):
            self.repository.append(candidate)
        self.assertEqual(1, len(self.ledger.read_stream("upgrade-001")))

    def test_external_authority_validates_signature_currentness_epoch_and_binding(self):
        key = bytes(range(32))
        environment = {
            "DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64": base64.b64encode(key).decode("ascii"),
            "DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH": "7",
        }
        raw = canonical_bytes(command())
        with patch.dict(os.environ, environment, clear=False):
            authority = ExternalAppendAuthority.from_environment()
        authorization = external_authorization(raw, key)
        capability = authority.verify_and_bind(raw, authorization)
        ledger = EvidenceLedger(InMemoryEvidenceRepository(authority), authority)
        self.assertEqual("APPENDED", ledger.append(capability)["append_status"])
        with self.assertRaises(AppendAuthorizationError):
            authority.verify_and_bind(raw, authorization)
        tampered = bytearray(authorization)
        tampered[-2] = ord("0") if tampered[-2] != ord("0") else ord("1")
        with self.assertRaises(AppendAuthorizationError):
            authority.verify_and_bind(raw, bytes(tampered))
        with self.assertRaises(AppendAuthorizationError):
            authority.verify_and_bind(raw, external_authorization(raw, key, epoch=8))
        now = int(time.time())
        with self.assertRaises(AppendAuthorizationError):
            authority.verify_and_bind(raw, external_authorization(raw, key, issued_at=now - 120, expires_at=now - 60))
        with self.assertRaises(TypeError):
            SUBJECT.PostgresEvidenceRepository.production(lambda: None, authority)
        with self.assertRaises(ExternalAuthorizationRequired):
            SUBJECT.PostgresEvidenceRepository.production("postgresql://fixed", self.authority)

    def test_authority_is_process_bound_and_same_nonce_is_atomic_across_threads(self):
        capability = self.authority.issue(canonical_bytes(command()))
        current_process = os.getpid()
        with patch.object(SUBJECT.os, "getpid", return_value=current_process + 1):
            with self.assertRaisesRegex(AppendAuthorizationError, "process boundary"):
                self.ledger.append(capability)

        key = bytes(range(32))
        environment = {
            "DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64": base64.b64encode(key).decode("ascii"),
            "DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH": "7",
        }
        raw = canonical_bytes(command(stream_id="upgrade-thread-auth"))
        auth = external_authorization(raw, key)
        with patch.dict(os.environ, environment, clear=False):
            authority = ExternalAppendAuthority.from_environment()
        barrier = threading.Barrier(17)
        outcome_lock = threading.Lock()
        successes = []
        failures = []

        def verify_same_nonce():
            barrier.wait()
            try:
                issued = authority.verify_and_bind(raw, auth)
            except AppendAuthorizationError as exc:
                with outcome_lock:
                    failures.append(exc)
            else:
                with outcome_lock:
                    successes.append(issued)

        threads = [threading.Thread(target=verify_same_nonce) for _ in range(16)]
        for thread in threads:
            thread.start()
        barrier.wait()
        for thread in threads:
            thread.join(timeout=10)
        self.assertFalse(any(thread.is_alive() for thread in threads))
        self.assertEqual(1, len(successes))
        self.assertEqual(15, len(failures))

    def test_missing_external_authority_is_waiting_external_and_appends_zero(self):
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(ExternalAuthorizationRequired, "WAITING_EXTERNAL"):
                ExternalAppendAuthority.from_environment()
        self.assertEqual([], self.repository._envelopes)

    def test_same_key_different_payload_is_durably_redacted(self):
        append(self.ledger, self.authority, command())
        with self.assertRaises(IdempotencyConflict):
            append(self.ledger, self.authority, command({"different": True}))
        quarantined = self.ledger.read_quarantine("upgrade-001")
        self.assertEqual(1, len(quarantined))
        self.assertNotIn("payload", quarantined[0])
        self.assertEqual("IDEMPOTENCY_KEY_CONTENT_CONFLICT", quarantined[0]["reason"])

    def test_sequence_conflict_and_deterministic_rebuild(self):
        with self.assertRaises(SequenceConflict):
            append(self.ledger, self.authority, command(expected_sequence=4))
        append(self.ledger, self.authority, command())
        append(
            self.ledger,
            self.authority,
            command({"from_state": "SCOPE_RESOLVED", "to_state": "INSTRUCTIONS_BOUND"}, 1, "append-002"),
        )
        self.assertEqual(
            ["SCOPE_RESOLVED", "INSTRUCTIONS_BOUND"],
            self.ledger.rebuild("upgrade-001", [], lambda state, event: state + [event["payload"]["to_state"]]),
        )

    def test_strict_command_rejects_duplicate_noncanonical_and_bad_scalars(self):
        raw = canonical_bytes(command())
        duplicate = raw[:-1] + b',"stream_id":"upgrade-001"}'
        with self.assertRaises(Exception):
            self.authority.issue(duplicate)
        with self.assertRaises(Exception):
            self.authority.issue(b" " + raw)
        cases = []
        bad = command(expected_sequence=True)
        cases.append(bad)
        cases.append(command(occurred_at="2026-02-30T00:00:00Z"))
        cases.append(command(occurred_at="2026-07-14T08:00:00+08:00"))
        cases.append(command(stream_id="../escape"))
        cases.append(command(privacy_class="public"))
        cases.append(command(event_type="state.transitioned"))
        for bad in cases:
            with self.subTest(bad=bad):
                with self.assertRaises(Exception):
                    self.authority.issue(canonical_bytes(bad))

    def test_payload_limits_depth_count_size_and_nonfinite_fail(self):
        deep = {}
        current = deep
        for _ in range(20):
            current["child"] = {}
            current = current["child"]
        with self.assertRaises(Exception):
            canonical_bytes(command(deep))
        with self.assertRaises(Exception):
            self.authority.issue(canonical_bytes(command({"items": list(range(300))})))
        with self.assertRaises(Exception):
            self.authority.issue(canonical_bytes(command({"large": "x" * 33000})))
        with self.assertRaises(Exception):
            canonical_bytes(command({"not_number": float("nan")}))

    def test_replay_recomputes_all_fields_and_command_wire(self):
        append(self.ledger, self.authority, command())
        protected = (
            "schema_version", "contract_id", "producer_module", "trace_id", "occurred_at",
            "privacy_class", "event_id", "stream_id", "sequence", "event_type", "source_module",
            "payload_sha256", "previous_event_sha256", "event_sha256", "append_status",
        )
        original = copy.deepcopy(self.repository._envelopes[0])
        for field in protected:
            self.repository._envelopes[0] = copy.deepcopy(original)
            event = self.repository._envelopes[0]["event"]
            event[field] = False if field == "sequence" else "tampered"
            with self.subTest(field=field):
                with self.assertRaises(CorruptEventStream):
                    self.ledger.read_stream("upgrade-001")
        self.repository._envelopes[0] = copy.deepcopy(original)
        self.repository._envelopes[0]["command_wire"] = canonical_bytes(command(key="other")).decode("utf-8")
        with self.assertRaises(CorruptEventStream):
            self.ledger.read_stream("upgrade-001")


if __name__ == "__main__":
    unittest.main()
