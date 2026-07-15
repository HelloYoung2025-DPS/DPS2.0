import importlib.util
import json
import os
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "evidence_ledger.py"
SUBJECT_NAME = "_dps_factory_evidence_ledger_crash_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("integration subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != MODULE_ROOT or source_path.parent != source_root:
        raise ImportError("integration subject escaped the module-owned src directory")
    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        if Path(getattr(existing, "__file__", "")).resolve(strict=True) != source_path:
            raise ImportError("integration subject module name is already bound elsewhere")
        return existing
    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the integration subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()
CorruptEventStream = SUBJECT.CorruptEventStream
DevelopmentAppendAuthority = SUBJECT.DevelopmentAppendAuthority
EvidenceLedger = SUBJECT.EvidenceLedger
FileEvidenceRepository = SUBJECT.FileEvidenceRepository
IdempotencyConflict = SUBJECT.IdempotencyConflict
SequenceConflict = SUBJECT.SequenceConflict
UnsafeFileFixture = SUBJECT.UnsafeFileFixture
canonical_bytes = SUBJECT.canonical_bytes
sha256 = SUBJECT.sha256


def command(*, stream_id="upgrade-crash-001", key="append-001", expected_sequence=0, payload=None):
    payload = payload if payload is not None else {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"}
    return {
        "schema_version": "1.0.0",
        "contract_id": "upgrade.event.append/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + sha256({"key": key}),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "stream_id": stream_id,
        "expected_sequence": expected_sequence,
        "event_type": "STATE_TRANSITIONED",
        "payload": payload,
        "payload_sha256": sha256(payload),
    }


def fixture(path):
    authority = DevelopmentAppendAuthority.for_local_tests()
    return authority, EvidenceLedger(FileEvidenceRepository(path, authority), authority)


def append(ledger, authority, value):
    return ledger.append(authority.issue(canonical_bytes(value)))


class CrashRecoveryIntegrationTests(unittest.TestCase):
    def test_real_process_kill_after_fsync_recovers_event(self):
        with tempfile.TemporaryDirectory() as directory:
            ledger_path = Path(directory).resolve(strict=True) / "upgrade-events.jsonl"
            child = r'''
import importlib.util, os, signal, sys
from pathlib import Path
source_path = Path(sys.argv[2]).resolve(strict=True)
name = "_dps_factory_evidence_ledger_crash_child_subject"
spec = importlib.util.spec_from_file_location(name, source_path)
subject = importlib.util.module_from_spec(spec); sys.modules[name] = subject; spec.loader.exec_module(subject)
authority = subject.DevelopmentAppendAuthority.for_local_tests()
ledger = subject.EvidenceLedger(subject.FileEvidenceRepository(sys.argv[1], authority), authority)
payload = {"from_state":"REQUESTED","to_state":"SCOPE_RESOLVED"}
command = {
 "schema_version":"1.0.0","contract_id":"upgrade.event.append/v1",
 "producer_module":"factory-release-controller","soul_id":None,"device_binding_id":None,
 "platform_account_id":None,"trace_id":"trace_" + "1" * 32,
 "idempotency_key":"idem_" + subject.sha256({"key":"append-001"}),
 "occurred_at":"2026-07-14T00:00:00Z","privacy_class":"internal",
 "stream_id":"upgrade-crash-001","expected_sequence":0,"event_type":"STATE_TRANSITIONED",
 "payload":payload,"payload_sha256":subject.sha256(payload)
}
ledger.append(authority.issue(subject.canonical_bytes(command)))
os.kill(os.getpid(), signal.SIGKILL)
'''
            result = subprocess.run(
                [sys.executable, "-I", "-c", child, str(ledger_path), str(SOURCE_PATH)],
                check=False,
                timeout=20,
            )
            self.assertEqual(-signal.SIGKILL, result.returncode)
            _, recovered = fixture(ledger_path)
            events = recovered.read_stream("upgrade-crash-001")
            self.assertEqual(1, len(events))
            self.assertEqual("SCOPE_RESOLVED", events[0]["payload"]["to_state"])

    def test_conflict_quarantine_is_durable_redacted_and_idempotent(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory).resolve(strict=True) / "events.jsonl"
            authority, ledger = fixture(path)
            append(ledger, authority, command(stream_id="upgrade-quarantine-001"))
            conflict = command(stream_id="upgrade-quarantine-001", payload={"different": True})
            for _ in range(2):
                with self.assertRaises(IdempotencyConflict):
                    append(ledger, authority, conflict)
            _, recovered = fixture(path)
            records = recovered.read_quarantine("upgrade-quarantine-001")
            self.assertEqual(1, len(records))
            self.assertNotIn("payload", records[0])
            quarantine_path = path.with_name(path.name + ".quarantine.jsonl")
            tampered = dict(records[0])
            tampered["existing_command_sha256"] = "0" * 64
            quarantine_path.write_bytes(canonical_bytes(tampered) + b"\n")
            with self.assertRaises(CorruptEventStream):
                recovered.read_quarantine("upgrade-quarantine-001")

    def test_two_concurrent_writers_are_serialized_and_only_one_sequence_zero_wins(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory).resolve(strict=True) / "events.jsonl"
            authority = DevelopmentAppendAuthority.for_local_tests()
            outcomes = []
            barrier = threading.Barrier(2)

            def worker(key):
                ledger = EvidenceLedger(FileEvidenceRepository(path, authority), authority)
                barrier.wait()
                try:
                    append(ledger, authority, command(stream_id="upgrade-concurrent-001", key=key))
                    outcomes.append("APPENDED")
                except SequenceConflict:
                    outcomes.append("SEQUENCE_CONFLICT")

            threads = [threading.Thread(target=worker, args=(key,)) for key in ("one", "two")]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join(timeout=10)
            self.assertEqual(["APPENDED", "SEQUENCE_CONFLICT"], sorted(outcomes))
            _, recovered = fixture(path)
            self.assertEqual(1, len(recovered.read_stream("upgrade-concurrent-001")))

    def test_symlink_and_hardlink_fixture_attacks_fail_before_append(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(strict=True)
            target = root / "target.jsonl"
            target.write_bytes(b"")
            symlink = root / "symlink.jsonl"
            symlink.symlink_to(target)
            hardlink = root / "hardlink.jsonl"
            os.link(target, hardlink)
            for path in (symlink, hardlink, target):
                authority = DevelopmentAppendAuthority.for_local_tests()
                ledger = EvidenceLedger(FileEvidenceRepository(path, authority), authority)
                with self.subTest(path=path.name):
                    with self.assertRaises(UnsafeFileFixture):
                        append(ledger, authority, command())
            self.assertEqual(b"", target.read_bytes())

    def test_partial_line_is_quarantined_without_repair_or_append(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory).resolve(strict=True) / "events.jsonl"
            partial = b'{"command_sha256":"partial"'
            path.write_bytes(partial)
            path.chmod(0o600)
            authority, ledger = fixture(path)
            with self.assertRaises(CorruptEventStream):
                ledger.read_stream("upgrade-crash-001")
            self.assertEqual(partial, path.read_bytes())
            records = ledger.read_quarantine("storage-corruption")
            self.assertEqual(1, len(records))
            self.assertIn("partial final line", records[0]["reason"])
            self.assertNotIn("command_wire", records[0])

    def test_duplicate_member_and_tampered_hash_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory).resolve(strict=True) / "events.jsonl"
            authority, ledger = fixture(path)
            append(ledger, authority, command())
            line = path.read_bytes().rstrip(b"\n")
            path.write_bytes(line[:-1] + b',"command_sha256":"' + b"0" * 64 + b'"}\n')
            with self.assertRaises(CorruptEventStream):
                ledger.read_stream("upgrade-crash-001")

    def test_short_writes_are_completed_and_locked_path_replacement_is_detected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(strict=True)
            path = root / "events.jsonl"
            authority = DevelopmentAppendAuthority.for_local_tests()
            repository = FileEvidenceRepository(path, authority)
            ledger = EvidenceLedger(repository, authority)
            real_write = os.write

            def short_write(fd, data):
                size = max(1, len(data) // 3)
                return real_write(fd, data[:size])

            with patch.object(SUBJECT.os, "write", side_effect=short_write):
                append(ledger, authority, command(stream_id="upgrade-short-write"))
            self.assertEqual(1, len(ledger.read_stream("upgrade-short-write")))

            moved = root / "moved.jsonl"
            with self.assertRaisesRegex(UnsafeFileFixture, "identity changed"):
                with repository._locked_file(path, create=False, exclusive=True):
                    path.rename(moved)
                    path.write_bytes(b"")
                    path.chmod(0o600)


if __name__ == "__main__":
    unittest.main()
