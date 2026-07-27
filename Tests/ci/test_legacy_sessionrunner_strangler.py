import copy
import hashlib
import importlib.util
import json
import os
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

TRUSTED_ANCHOR = os.environ.get(VERIFIER.TRUSTED_ANCHOR_ENV)
ARTIFACT_ROOT = ROOT / VERIFIER.ARTIFACT_RELATIVE
TRACE_DIRECTORY = ARTIFACT_ROOT / "golden-traces"


class SessionRunnerStranglerBaselineTests(unittest.TestCase):
    """Fail-closed characterisation tests for the SessionRunner strangler baseline.

    ``verify_repository`` validates the externally issued anchor *first* and
    returns on the first anchor error (verify_sessionrunner_baseline.py:1296-1298).
    Anchor validation byte-hashes every path in ``TRUSTED_PROTECTED_PATHS``
    (:76-91, :726-737), which is exactly the set of fixtures the tamper cases
    below mutate.  Any tamper is therefore reported as "protected path hash
    differs from external anchor" and the semantic validators never run.  The
    tamper cases consequently drive the validator that *owns* each invariant,
    and each pairs the tamper with a clean control so an emptied validator
    cannot read as green.
    """

    # ---- fixture loaders -------------------------------------------------

    def snapshot(self):
        return json.loads(
            (ARTIFACT_ROOT / VERIFIER.SNAPSHOT_FILE).read_text(encoding="utf-8")
        )

    def source_bytes(self):
        return (ROOT / VERIFIER.SOURCE_RELATIVE).read_bytes()

    def trace(self, name):
        path = TRACE_DIRECTORY / name
        return path, json.loads(path.read_text(encoding="utf-8"))

    def trace_schema(self):
        return json.loads(
            (ARTIFACT_ROOT / VERIFIER.TRACE_SCHEMA_FILE).read_text(encoding="utf-8")
        )

    def snapshot_anchor(self, snapshot):
        """Minimal anchor stub carrying only what ``validate_snapshot`` reads.

        Binding it to the snapshot's own ``baseline_commit`` keeps the unrelated
        anchor-binding check quiet so each assertion isolates its own tamper.
        """
        return {"baseline_commit": snapshot["baseline_commit"]}

    # ---- end-to-end baseline --------------------------------------------

    @unittest.skipUnless(
        TRUSTED_ANCHOR,
        "requires an independently issued read-only anchor via "
        + VERIFIER.TRUSTED_ANCHOR_ENV
        + "; by design verify_repository() returns WAITING_EXTERNAL without one "
        "(verify_sessionrunner_baseline.py:1275-1285), so PASS is unreachable "
        "in an environment that has no externally issued anchor",
    )
    def test_current_baseline_passes_without_runtime_claim(self):
        result = VERIFIER.verify_repository(ROOT, Path(TRUSTED_ANCHOR))
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual("STATIC_CHARACTERIZATION_ONLY", result["scope"])
        self.assertEqual(2, result["synthetic_trace_count"])
        self.assertTrue(any("No Windows" in item for item in result["limitations"]))

    def test_missing_trusted_anchor_fails_closed(self):
        result = VERIFIER.verify_repository(ROOT, None)
        self.assertFalse(result["ok"])
        self.assertEqual("WAITING_EXTERNAL", result["status"])
        self.assertTrue(
            any("trusted anchor is required" in error for error in result["errors"]),
            result["errors"],
        )

    # ---- tamper cases ----------------------------------------------------

    def test_signature_hash_tamper_fails_closed(self):
        snapshot = self.snapshot()
        source_bytes = self.source_bytes()
        anchor = self.snapshot_anchor(snapshot)

        control = []
        VERIFIER.validate_snapshot(
            copy.deepcopy(snapshot), source_bytes, anchor, control
        )
        self.assertEqual([], control, "untampered snapshot must validate cleanly")

        tampered = copy.deepcopy(snapshot)
        tampered["methods"][0]["signature_sha256"] = "0" * 64
        errors = []
        VERIFIER.validate_snapshot(tampered, source_bytes, anchor, errors)
        self.assertTrue(errors)
        self.assertTrue(
            any("signature_sha256" in error for error in errors),
            errors,
        )

    def test_duplicate_declaration_fails_even_if_source_hash_is_rebound(self):
        snapshot = self.snapshot()
        source_bytes = self.source_bytes() + (
            b"\npublic static string Run(object projectObj, object instanceObj)\n"
        )
        source_hash = hashlib.sha256(source_bytes).hexdigest()
        blob_header = b"blob " + str(len(source_bytes)).encode("ascii") + b"\0"
        tampered = copy.deepcopy(snapshot)
        tampered["snapshot_id"] = "legacy-sessionrunner:" + source_hash
        tampered["captured_from"].update(
            {
                "git_blob": hashlib.sha1(blob_header + source_bytes).hexdigest(),
                "sha256": source_hash,
                "byte_length": len(source_bytes),
            }
        )

        errors = []
        VERIFIER.validate_snapshot(
            tampered, source_bytes, self.snapshot_anchor(snapshot), errors
        )
        self.assertTrue(errors)
        self.assertTrue(
            any("Run declaration must occur exactly once" in error for error in errors),
            errors,
        )

    def test_synthetic_trace_cannot_be_relabelled_as_runtime_evidence(self):
        path, trace = self.trace("device-unavailable.synthetic.v1.json")
        snapshot_id = trace["source_snapshot_id"]

        control = []
        VERIFIER.validate_trace(
            copy.deepcopy(trace), path, snapshot_id, set(), control
        )
        self.assertEqual([], control, "untampered golden trace must validate cleanly")

        tampered = copy.deepcopy(trace)
        tampered["runtime_observed"] = True
        tampered["evidence_eligibility"] = ["DEVICE_VERIFIED"]
        errors = []
        VERIFIER.validate_trace(tampered, path, snapshot_id, set(), errors)
        self.assertTrue(errors)
        self.assertTrue(
            any("cannot claim runtime observation" in error for error in errors),
            errors,
        )
        self.assertTrue(
            any("cannot satisfy a verification level" in error for error in errors),
            errors,
        )

    def test_schema_weakening_fails_closed(self):
        schema = self.trace_schema()
        label = "trace schema"
        const = "dps.legacy-sessionrunner-golden-trace/v1"

        control = []
        VERIFIER.validate_schema_document(
            copy.deepcopy(schema), label, const, control
        )
        self.assertEqual([], control, "untampered trace schema must validate cleanly")

        tampered = copy.deepcopy(schema)
        tampered["additionalProperties"] = True
        errors = []
        VERIFIER.validate_schema_document(tampered, label, const, errors)
        self.assertTrue(errors)
        self.assertTrue(
            any("reject unknown top-level fields" in error for error in errors),
            errors,
        )

    @unittest.skipUnless(
        TRUSTED_ANCHOR,
        "requires the externally issued anchor record via "
        + VERIFIER.TRUSTED_ANCHOR_ENV
        + "; validate_trusted_anchor rejects any anchor that is not a complete "
        "provider record, so no synthetic stub can reach the protected-path check",
    )
    def test_protected_path_tamper_is_caught_by_the_anchor_layer(self):
        """The anchor layer is what actually guards these fixtures on disk.

        Pinned so the preemption documented in this class's docstring cannot
        silently stop holding.
        """
        anchor = json.loads(Path(TRUSTED_ANCHOR).read_text(encoding="utf-8"))
        target = (
            "Modules/legacy-runtime-adapter/operations/strangler/"
            + VERIFIER.SNAPSHOT_FILE
        )
        tampered = copy.deepcopy(anchor)
        rebound = [
            record for record in tampered["protected_files"] if record["path"] == target
        ]
        self.assertEqual(1, len(rebound), tampered["protected_files"])
        rebound[0]["sha256"] = "0" * 64

        errors = []
        VERIFIER.validate_trusted_anchor(ROOT, tampered, errors)
        self.assertTrue(
            any(
                "protected path hash differs from external anchor" in error
                for error in errors
            ),
            errors,
        )


if __name__ == "__main__":
    unittest.main()
