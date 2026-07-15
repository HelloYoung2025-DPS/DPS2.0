import hashlib
import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VERIFIER_PATH = ROOT / (
    "Modules/legacy-runtime-adapter/operations/strangler/"
    "verify_sessionrunner_baseline.py"
)
SPEC = importlib.util.spec_from_file_location(
    "legacy_sessionrunner_strangler_verifier", VERIFIER_PATH
)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("cannot load the SessionRunner strangler verifier")
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


class SessionRunnerStranglerBaselineTests(unittest.TestCase):
    def stage_repository(self):
        temporary = tempfile.TemporaryDirectory(prefix="dps-sessionrunner-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        source = root / "Modules" / "SessionRunner.cs"
        source.parent.mkdir(parents=True)
        shutil.copy2(ROOT / "Modules" / "SessionRunner.cs", source)
        artifacts = root / (
            "Modules/legacy-runtime-adapter/operations/strangler"
        )
        shutil.copytree(
            ROOT / "Modules/legacy-runtime-adapter/operations/strangler",
            artifacts,
        )
        return root

    def test_current_baseline_passes_without_runtime_claim(self):
        result = VERIFIER.verify_repository(ROOT)
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual("STATIC_CHARACTERIZATION_ONLY", result["scope"])
        self.assertEqual(2, result["synthetic_trace_count"])
        self.assertTrue(any("No Windows" in item for item in result["limitations"]))

    def test_signature_hash_tamper_fails_closed(self):
        root = self.stage_repository()
        snapshot_path = root / VERIFIER.ARTIFACT_RELATIVE / VERIFIER.SNAPSHOT_FILE
        snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))
        snapshot["methods"][0]["signature_sha256"] = "0" * 64
        snapshot_path.write_text(
            json.dumps(snapshot, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        result = VERIFIER.verify_repository(root)
        self.assertFalse(result["ok"])
        self.assertTrue(
            any("signature_sha256" in error for error in result["errors"]),
            result["errors"],
        )

    def test_duplicate_declaration_fails_even_if_source_hash_is_rebound(self):
        root = self.stage_repository()
        source_path = root / VERIFIER.SOURCE_RELATIVE
        source_bytes = source_path.read_bytes() + (
            b"\npublic static string Run(object projectObj, object instanceObj)\n"
        )
        source_path.write_bytes(source_bytes)
        snapshot_path = root / VERIFIER.ARTIFACT_RELATIVE / VERIFIER.SNAPSHOT_FILE
        snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))
        source_hash = hashlib.sha256(source_bytes).hexdigest()
        blob_header = b"blob " + str(len(source_bytes)).encode("ascii") + b"\0"
        snapshot["snapshot_id"] = "legacy-sessionrunner:" + source_hash
        snapshot["captured_from"].update(
            {
                "git_blob": hashlib.sha1(blob_header + source_bytes).hexdigest(),
                "sha256": source_hash,
                "byte_length": len(source_bytes),
            }
        )
        snapshot_path.write_text(
            json.dumps(snapshot, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        for trace_path in (
            root / VERIFIER.ARTIFACT_RELATIVE / "golden-traces"
        ).glob("*.json"):
            trace = json.loads(trace_path.read_text(encoding="utf-8"))
            trace["source_snapshot_id"] = snapshot["snapshot_id"]
            trace_path.write_text(
                json.dumps(trace, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
        result = VERIFIER.verify_repository(root)
        self.assertFalse(result["ok"])
        self.assertTrue(
            any("Run declaration must occur exactly once" in error for error in result["errors"]),
            result["errors"],
        )

    def test_synthetic_trace_cannot_be_relabelled_as_runtime_evidence(self):
        root = self.stage_repository()
        trace_path = root / VERIFIER.ARTIFACT_RELATIVE / (
            "golden-traces/device-unavailable.synthetic.v1.json"
        )
        trace = json.loads(trace_path.read_text(encoding="utf-8"))
        trace["runtime_observed"] = True
        trace["evidence_eligibility"] = ["DEVICE_VERIFIED"]
        trace_path.write_text(
            json.dumps(trace, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        result = VERIFIER.verify_repository(root)
        self.assertFalse(result["ok"])
        self.assertTrue(
            any("cannot claim runtime observation" in error for error in result["errors"]),
            result["errors"],
        )
        self.assertTrue(
            any("cannot satisfy a verification level" in error for error in result["errors"]),
            result["errors"],
        )

    def test_schema_weakening_fails_closed(self):
        root = self.stage_repository()
        schema_path = root / VERIFIER.ARTIFACT_RELATIVE / VERIFIER.TRACE_SCHEMA_FILE
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        schema["additionalProperties"] = True
        schema_path.write_text(
            json.dumps(schema, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        result = VERIFIER.verify_repository(root)
        self.assertFalse(result["ok"])
        self.assertTrue(
            any("reject unknown top-level fields" in error for error in result["errors"]),
            result["errors"],
        )


if __name__ == "__main__":
    unittest.main()
