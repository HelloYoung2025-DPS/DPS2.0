#!/usr/bin/env python3
"""Adversarial tests for the externally anchored legacy C# byte baseline."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest import mock


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
SUBJECT_PATH = (
    REPOSITORY_ROOT
    / "Modules"
    / "legacy-runtime-adapter"
    / "operations"
    / "strangler"
    / "verify_sessionrunner_baseline.py"
)
SPEC = importlib.util.spec_from_file_location("dps_legacy_byte_verifier", SUBJECT_PATH)
SUBJECT = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = SUBJECT
SPEC.loader.exec_module(SUBJECT)


class LegacyByteBaselineTests(unittest.TestCase):
    def _git(self, root: Path, *args: str, input_bytes: bytes | None = None) -> bytes:
        return subprocess.run(
            ["git", "-C", str(root), *args],
            input=input_bytes,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        ).stdout

    def _write_json(self, path: Path, value: object) -> None:
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def _build_repair_record(
        self,
        path: str,
        baseline: tuple[str, bytes],
        working: bytes,
        binding: tuple[str, str],
    ) -> dict[str, object]:
        object_id, baseline_bytes = baseline
        disposition, approval_ref = binding
        return {
            "path": path,
            "baseline_git_blob": object_id,
            "baseline_sha256": hashlib.sha256(baseline_bytes).hexdigest(),
            "baseline_byte_length": len(baseline_bytes),
            "working_sha256": hashlib.sha256(working).hexdigest(),
            "working_byte_length": len(working),
            "disposition": disposition,
            "approval_ref": approval_ref,
        }

    @contextmanager
    def _fixture(self):
        temporary = tempfile.TemporaryDirectory()
        base = Path(temporary.name).resolve()
        root = base / "repo"
        trusted_directory = base / "external-provider"
        root.mkdir()
        trusted_directory.mkdir()
        anchor_path = trusted_directory / "legacy-baseline-anchor.json"
        try:
            repair_bindings = dict(SUBJECT.EXPECTED_APPROVAL_BINDINGS)
            repair_bindings.update(SUBJECT.EXPECTED_CONTAINMENT_BINDINGS)
            files: dict[str, bytes] = {}
            for index, relative in enumerate(sorted(repair_bindings)):
                class_name = "Repair{0:02d}".format(index)
                files[relative] = (
                    "public class {0} {{ }}\r\n".format(class_name).encode("ascii")
                )
            for index in range(67):
                relative = "Core/Stable{0:03d}.cs".format(index)
                files[relative] = (
                    "public class Stable{0:03d} {{ }}\r\n".format(index).encode(
                        "ascii"
                    )
                )
            self.assertEqual(SUBJECT.EXPECTED_LEGACY_CSHARP_COUNT, len(files))
            for relative, content in files.items():
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(content)

            self._git(root, "init", "-q")
            self._git(root, "add", ".")
            self._git(
                root,
                "-c",
                "user.name=DPS Test",
                "-c",
                "user.email=dps-test@example.invalid",
                "commit",
                "-q",
                "-m",
                "baseline",
            )
            commit = self._git(root, "rev-parse", "HEAD^{commit}").decode().strip()
            tree = self._git(root, "rev-parse", commit + "^{tree}").decode().strip()
            parent_line = self._git(
                root, "rev-list", "--parents", "-n", "1", commit
            ).decode().strip()
            parents = parent_line.split()[1:]
            inventory_errors: list[str] = []
            inventory = SUBJECT._baseline_git_inventory(
                root, commit, inventory_errors
            )
            self.assertEqual([], inventory_errors)
            self.assertEqual(SUBJECT.EXPECTED_LEGACY_CSHARP_COUNT, len(inventory))

            repair_records: dict[str, dict[str, object]] = {}
            for relative, binding in repair_bindings.items():
                target = root / relative
                target.write_bytes(
                    target.read_bytes()
                    + ("// independently approved repair: " + relative + "\r\n").encode(
                        "utf-8"
                    )
                )
                repair_records[relative] = self._build_repair_record(
                    relative, inventory[relative], target.read_bytes(), binding
                )

            for relative in SUBJECT.TRUSTED_PROTECTED_PATHS:
                source = REPOSITORY_ROOT / relative
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)

            artifact = root / SUBJECT.ARTIFACT_RELATIVE
            entries = []
            for relative in sorted(inventory):
                object_id, baseline_bytes = inventory[relative]
                working = (root / relative).read_bytes()
                changed = baseline_bytes != working
                approved = repair_records.get(relative)
                entries.append(
                    {
                        "path": relative,
                        "baseline_git_blob": object_id,
                        "baseline_sha256": hashlib.sha256(baseline_bytes).hexdigest(),
                        "baseline_byte_length": len(baseline_bytes),
                        "working_sha256": hashlib.sha256(working).hexdigest(),
                        "working_byte_length": len(working),
                        "disposition": (
                            approved["disposition"]
                            if changed and approved is not None
                            else "BYTE_IDENTICAL"
                        ),
                        "approval_ref": (
                            approved["approval_ref"]
                            if changed and approved is not None
                            else None
                        ),
                    }
                )
            manifest = {
                "schema_version": SUBJECT.LEGACY_BYTES_SCHEMA_VERSION,
                "baseline_commit": commit,
                "inventory_scope": "TRACKED_LEGACY_CSHARP_PLUS_APPROVED_REPAIRS",
                "entry_count": len(entries),
                "entries": entries,
                "limitations": ["static bytes only", "no runtime evidence"],
            }
            self._write_json(artifact / SUBJECT.LEGACY_BYTES_FILE, manifest)

            protected_files = []
            for relative in sorted(SUBJECT.TRUSTED_PROTECTED_PATHS):
                value = (root / relative).read_bytes()
                protected_files.append(
                    {
                        "path": relative,
                        "sha256": hashlib.sha256(value).hexdigest(),
                        "byte_length": len(value),
                    }
                )
            anchor: dict[str, object] = {
                "schema_version": SUBJECT.TRUSTED_ANCHOR_SCHEMA_VERSION,
                "anchor_id": "",
                "provider_id": SUBJECT.TRUSTED_PROVIDER_ID,
                "audience": SUBJECT.TRUSTED_AUDIENCE,
                "issued_at": "2026-07-15T00:00:00Z",
                "baseline_commit": commit,
                "baseline_tree": tree,
                "baseline_parents": parents,
                "inventory_scope": "TRACKED_LEGACY_CSHARP_PLUS_APPROVED_REPAIRS",
                "inventory_count": SUBJECT.EXPECTED_LEGACY_CSHARP_COUNT,
                "inventory_digest_algorithm": "sha256-canonical-json-v1",
                "inventory_sha256": SUBJECT.legacy_inventory_sha256(inventory),
                "approved_repair_count": SUBJECT.EXPECTED_APPROVED_REPAIR_COUNT,
                "approved_repairs": [
                    repair_records[path]
                    for path in sorted(SUBJECT.EXPECTED_APPROVAL_BINDINGS)
                ],
                "containment_repair_count": SUBJECT.EXPECTED_CONTAINMENT_REPAIR_COUNT,
                "containment_repairs": [
                    repair_records[path]
                    for path in sorted(SUBJECT.EXPECTED_CONTAINMENT_BINDINGS)
                ],
                "protected_files": protected_files,
                "test_requirements": [
                    {"path": path, "minimum_test_methods": minimum}
                    for path, minimum in sorted(SUBJECT.EXPECTED_TEST_MINIMUMS.items())
                ],
                "issued_by": SUBJECT.TRUSTED_ISSUER,
                "limitations": [
                    "external provider fixture only",
                    "no runtime or release evidence",
                ],
            }
            anchor["anchor_id"] = SUBJECT.trusted_anchor_id(anchor)
            self._write_json(anchor_path, anchor)
            anchor_path.chmod(0o444)
            trusted_directory.chmod(0o555)
            yield root, artifact, anchor_path, commit
        finally:
            if trusted_directory.exists():
                trusted_directory.chmod(0o755)
            if anchor_path.exists():
                anchor_path.chmod(0o644)
            temporary.cleanup()

    def _load_as_external_provider(
        self, root: Path, anchor_path: Path, errors: list[str]
    ):
        actual_uid = os.geteuid() if hasattr(os, "geteuid") else 1000
        with mock.patch.object(SUBJECT.os, "geteuid", return_value=actual_uid + 1):
            return SUBJECT.load_trusted_anchor(root, anchor_path, errors)

    def _verify(
        self, root: Path, artifact: Path, anchor_path: Path
    ) -> tuple[int, int, list[str]]:
        errors: list[str] = []
        anchor = self._load_as_external_provider(root, anchor_path, errors)
        if anchor is None:
            return 0, 0, errors
        overrides, inventory = SUBJECT.validate_trusted_anchor(root, anchor, errors)
        count, changed = SUBJECT.validate_legacy_byte_baseline(
            root, artifact, anchor, inventory, overrides, errors
        )
        return count, changed, errors

    def _mutate_json(self, path: Path, mutate) -> None:
        value = json.loads(path.read_text(encoding="utf-8"))
        mutate(value)
        self._write_json(path, value)

    def test_exact_79_baseline_and_12_separated_repairs_pass(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            count, changed, errors = self._verify(root, artifact, anchor_path)
        self.assertEqual((79, 12, []), (count, changed, errors))

    def test_missing_external_provider_is_waiting_external(self) -> None:
        with self._fixture() as (root, _, _, _):
            result = SUBJECT.verify_repository(root, None)
        self.assertEqual("WAITING_EXTERNAL", result["status"])
        self.assertFalse(result["ok"])

    def test_same_identity_cannot_self_issue_read_only_anchor(self) -> None:
        with self._fixture() as (root, _, anchor_path, _):
            errors: list[str] = []
            anchor = SUBJECT.load_trusted_anchor(root, anchor_path, errors)
        self.assertIsNone(anchor)
        self.assertTrue(any("different from the verifier" in item for item in errors), errors)

    def test_anchor_identifier_binds_complete_provider_record(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            anchor_path.parent.chmod(0o755)
            anchor_path.chmod(0o644)
            self._mutate_json(
                anchor_path,
                lambda value: value.__setitem__("issued_at", "2026-07-16T00:00:00Z"),
            )
            anchor_path.chmod(0o444)
            anchor_path.parent.chmod(0o555)
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("complete provider record" in item for item in errors), errors)

    def test_manifest_cannot_shrink_inventory_to_five(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            manifest_path = artifact / SUBJECT.LEGACY_BYTES_FILE

            def shrink(value):
                value["entries"] = value["entries"][:5]
                value["entry_count"] = 5

            self._mutate_json(manifest_path, shrink)
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("exactly 79" in item for item in errors), errors)
        self.assertTrue(any("protected path hash differs" in item for item in errors), errors)

    def test_stable_file_deletion_fails_inventory(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            (root / "Core/Stable000.cs").unlink()
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("working path set changed" in item for item in errors), errors)

    def test_uppercase_cs_extension_injection_fails_inventory(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            (root / "ZDProjects/Injected.CS").write_text(
                "class Injected {}\n", encoding="utf-8"
            )
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("Injected.CS" in item for item in errors), errors)

    def test_stable_byte_or_line_ending_mutation_fails(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            (root / "Core/Stable000.cs").write_bytes(b"public class Stable000 { }\n")
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("working_sha256" in item for item in errors), errors)
        self.assertTrue(any("unapproved legacy C# byte change" in item for item in errors), errors)

    def test_manifest_mutation_is_externally_detected(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            manifest_path = artifact / SUBJECT.LEGACY_BYTES_FILE
            self._mutate_json(
                manifest_path,
                lambda value: value["limitations"].append("candidate self approval"),
            )
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("protected path hash differs" in item for item in errors), errors)

    def test_verifier_rule_mutation_is_externally_detected(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            verifier = root / SUBJECT.ARTIFACT_RELATIVE / "verify_sessionrunner_baseline.py"
            verifier.write_text(verifier.read_text() + "\n# weakened\n", encoding="utf-8")
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("protected path hash differs" in item for item in errors), errors)

    def test_approved_repair_policy_mutation_is_externally_detected(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            policy = (
                root
                / SUBJECT.ARTIFACT_RELATIVE
                / "approved-legacy-repairs.policy.v1.json"
            )
            self._mutate_json(
                policy, lambda value: value["rules"].append("allow candidate changes")
            )
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("protected path hash differs" in item for item in errors), errors)

    def test_required_test_or_test_command_mutation_is_externally_detected(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            required_test = root / next(iter(sorted(SUBJECT.EXPECTED_TEST_MINIMUMS)))
            required_test.write_text("# zero tests\n", encoding="utf-8")
            module_manifest = root / "Modules/legacy-runtime-adapter/module.yaml"
            module_manifest.write_text(
                module_manifest.read_text().replace("unittest", "fixed-success"),
                encoding="utf-8",
            )
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("protected path hash differs" in item for item in errors), errors)
        self.assertTrue(any("below minimum" in item for item in errors), errors)

    def test_approved_working_bytes_and_manifest_cannot_self_approve(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            target = root / "Modules/SessionRunner.cs"
            target.write_bytes(target.read_bytes() + b"// injected\r\n")
            manifest_path = artifact / SUBJECT.LEGACY_BYTES_FILE

            def rewrite(value):
                entry = next(
                    item
                    for item in value["entries"]
                    if item["path"] == "Modules/SessionRunner.cs"
                )
                entry["working_sha256"] = hashlib.sha256(target.read_bytes()).hexdigest()
                entry["working_byte_length"] = len(target.read_bytes())

            self._mutate_json(manifest_path, rewrite)
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("external exact approved repair" in item for item in errors), errors)

    def test_symlink_replacement_fails(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            target = root / "Core/Stable000.cs"
            copy = root / "stable-copy.txt"
            copy.write_bytes(target.read_bytes())
            target.unlink()
            target.symlink_to(copy)
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("non-symlink" in item for item in errors), errors)

    def test_symlink_directory_cannot_hide_extra_legacy_csharp(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            outside = root / "outside-scope"
            outside.mkdir()
            (outside / "Hidden.CS").write_text("class Hidden {}\n", encoding="utf-8")
            (root / "Core/linked-scope").symlink_to(outside, target_is_directory=True)
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("symlink or reparse-like" in item for item in errors), errors)

    def test_arbitrary_new_commit_and_manifest_baseline_swap_fails(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            self._git(root, "add", ".")
            self._git(
                root,
                "-c",
                "user.name=DPS Attacker",
                "-c",
                "user.email=attacker@example.invalid",
                "commit",
                "-q",
                "-m",
                "self approved baseline",
            )
            replacement = self._git(root, "rev-parse", "HEAD^{commit}").decode().strip()
            manifest_path = artifact / SUBJECT.LEGACY_BYTES_FILE
            self._mutate_json(
                manifest_path,
                lambda value: value.__setitem__("baseline_commit", replacement),
            )
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("differs from external trusted anchor" in item for item in errors), errors)

    def test_baseline_casefold_collision_is_rejected(self) -> None:
        with self._fixture() as (root, _, _, _):
            blob = self._git(
                root, "hash-object", "-w", "--stdin", input_bytes=b"class Collision {}\n"
            ).decode().strip()
            self._git(
                root,
                "update-index",
                "--add",
                "--cacheinfo",
                "100644," + blob + ",Core/STABLE000.CS",
            )
            self._git(
                root,
                "-c",
                "user.name=DPS Test",
                "-c",
                "user.email=dps-test@example.invalid",
                "commit",
                "-q",
                "-m",
                "casefold collision",
            )
            commit = self._git(root, "rev-parse", "HEAD^{commit}").decode().strip()
            errors: list[str] = []
            SUBJECT._baseline_git_inventory(root, commit, errors)
        self.assertTrue(any("case-insensitive path collision" in item for item in errors), errors)

    def test_case_aliased_legacy_root_in_git_is_rejected(self) -> None:
        with self._fixture() as (root, _, _, _):
            blob = self._git(
                root, "hash-object", "-w", "--stdin", input_bytes=b"class Alias {}\n"
            ).decode().strip()
            self._git(
                root,
                "update-index",
                "--add",
                "--cacheinfo",
                "100644," + blob + ",core/Injected.CS",
            )
            self._git(
                root,
                "update-index",
                "--add",
                "--cacheinfo",
                "100644," + blob + ",Modules/core/Injected.CS",
            )
            self._git(
                root,
                "-c",
                "user.name=DPS Test",
                "-c",
                "user.email=dps-test@example.invalid",
                "commit",
                "-q",
                "-m",
                "case aliased root",
            )
            commit = self._git(root, "rev-parse", "HEAD^{commit}").decode().strip()
            errors: list[str] = []
            SUBJECT._baseline_git_inventory(root, commit, errors)
        self.assertTrue(any("core (expected Core)" in item for item in errors), errors)
        self.assertTrue(
            any(
                "Modules/core (expected Modules/Core)" in item for item in errors
            ),
            errors,
        )

    def test_containment_list_cannot_extend_original_four(self) -> None:
        with self._fixture() as (root, artifact, anchor_path, _):
            anchor_path.parent.chmod(0o755)
            anchor_path.chmod(0o644)

            def extend(value):
                value["approved_repairs"].append(value["containment_repairs"][0])
                value["approved_repair_count"] = 5
                value["anchor_id"] = SUBJECT.trusted_anchor_id(value)

            self._mutate_json(anchor_path, extend)
            anchor_path.chmod(0o444)
            anchor_path.parent.chmod(0o555)
            _, _, errors = self._verify(root, artifact, anchor_path)
        self.assertTrue(any("exactly four" in item for item in errors), errors)


class LegacyGoldenTraceIdentityTests(unittest.TestCase):
    def setUp(self) -> None:
        self.trace_path = (
            SUBJECT_PATH.parent
            / "golden-traces"
            / "split-session-nominal.synthetic.v1.json"
        )
        self.trace = json.loads(self.trace_path.read_text(encoding="utf-8"))
        self.snapshot_id = self.trace["source_snapshot_id"]

    def _errors(self, trace: dict) -> list[str]:
        errors: list[str] = []
        SUBJECT.validate_trace(trace, self.trace_path, self.snapshot_id, set(), errors)
        return errors

    def test_canonical_opaque_fixture_passes(self) -> None:
        self.assertEqual([], self._errors(self.trace))

    def test_legacy_and_terminal_newline_identifiers_fail_closed(self) -> None:
        mutations = {
            "trace_id": "golden.synthetic.split-session-nominal.v1",
            "soul_id": self.trace["identity_scope"]["soul_id"] + "\n",
            "device_binding_id": "synthetic-device-alpha",
            "platform_account_id": self.trace["identity_scope"][
                "platform_account_id"
            ].upper(),
        }
        for field, invalid in mutations.items():
            with self.subTest(field=field):
                candidate = json.loads(json.dumps(self.trace))
                if field == "trace_id":
                    candidate[field] = invalid
                else:
                    candidate["identity_scope"][field] = invalid
                self.assertTrue(self._errors(candidate))


if __name__ == "__main__":
    unittest.main()
