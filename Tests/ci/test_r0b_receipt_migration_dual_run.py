#!/usr/bin/env python3
"""RebuildPlan section 4.2.3 old/new dual-run for the R0-B instruction receipt migration.

R0-B removes the redundant ``agents.resolver`` field from the module manifest
schema and from all 34 manifests.  Section 4.2.3 requires the old validator and
an attack corpus to be frozen and dual-run against the old and the new receipt
rules.  A schema change *is* a validation-rule change and the receipt binds
manifest bytes, so this batch is emphatically not exempt from 4.2.3.

Everything here is deterministic, offline and model-free:

* the old validator input is the frozen schema under ``Tests/ci/fixtures/
  r0b_instruction_receipt_migration/baseline/``, captured from the pre-migration
  baseline commit and never regenerated;
* the new validator input is the live repository schema and the live manifests,
  so a re-introduced ``resolver`` field breaks this test;
* both sides are judged by ``phase0.validate_json_schema`` -- the same validator
  the Phase 0 gate itself runs -- rather than by a general purpose JSON Schema
  library, so the dual-run reflects real gate behaviour.
"""

from __future__ import annotations

import ast
import copy
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any, Dict, List, Mapping
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = ROOT / "Tools" / "ci"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "r0b_instruction_receipt_migration"
BASELINE = FIXTURES / "baseline"
LIVE_SCHEMA_PATH = ROOT / "governance" / "schemas" / "module-manifest.schema.json"
# v1/v2 major coexistence (RebuildPlan 4.2(5)): the live v2 schema is the current
# module-manifest.schema.json; the retained v1 schema keeps historical / rollback
# manifests interpretable.
LIVE_V2_SCHEMA_PATH = LIVE_SCHEMA_PATH
LIVE_V1_SCHEMA_PATH = ROOT / "governance" / "schemas" / "module-manifest.v1.schema.json"
VERIFICATION_DIRECTORY = ROOT / "Tools" / "verification"
EXPECTED_MODULE_COUNT = 34

_ORIGINAL_IMPORT_PATH = list(sys.path)
try:
    if str(CI_DIRECTORY) not in sys.path:
        sys.path.insert(0, str(CI_DIRECTORY))
    if str(VERIFICATION_DIRECTORY) not in sys.path:
        sys.path.insert(0, str(VERIFICATION_DIRECTORY))

    import phase0  # noqa: E402
    from phase0 import (  # noqa: E402
        Phase0Error,
        resolve_instruction_receipt,
        sha256_bytes,
        validate_instruction_receipt,
        validate_json_schema,
    )
    from external_gate import SUPPORTED_MODULE_MANIFEST_MAJORS  # noqa: E402
finally:
    sys.path = _ORIGINAL_IMPORT_PATH


def _load_json(path: Path) -> Dict[str, Any]:
    """Read a manifest or schema exactly the way phase0 reads manifests."""

    return json.loads(path.read_text(encoding="utf-8-sig"))


# Finding 2 (ambient git): the baseline anchors below read git.  A bare "git" is
# PATH-selectable, so a candidate that prepends a directory to PATH could
# substitute a hostile git that fabricates baseline blob bytes and defeat the
# anchoring.  Reuse the repository's already-established trusted-git boundary --
# run_candidate_gate.py locks git to the absolute /usr/bin/git (root:wheel, so
# candidate code in the repo or its .venv cannot replace it) and refuses it if it
# is missing, a symlink, or non-executable.  The anchors adopt the same locked
# executable, making them fail-closed against PATH poisoning rather than trusting
# a bare name.  The rest of the Phase 0 gate (run_phase0_gate.py, phase0.py and
# the §11 verifier) still calls bare "git"; unifying that on the locked executable
# is a repo-wide trusted-runner concern, called out as its own batch in the PR.
LOCKED_GIT = Path("/usr/bin/git")


def _trusted_git_executable() -> str:
    if (
        not LOCKED_GIT.is_file()
        or LOCKED_GIT.is_symlink()
        or not os.access(str(LOCKED_GIT), os.X_OK)
    ):
        raise AssertionError("locked /usr/bin/git is missing or unsafe")
    return str(LOCKED_GIT)


# Finding 3 (baseline authority): the anchors below must not learn *which* commit
# is the baseline from a file the candidate change can rewrite.  ``provenance.json``
# lives in the same commit as the fixtures it describes, so a change that rewrote
# both the fixtures and the recorded baseline_commit would still anchor to itself.
# The baseline commit is therefore taken from the runner-injected
# ``DPS_BASELINE_COMMIT`` -- the channel static-ci.yml already supplies and
# run_phase0_gate.resolve_baseline already reads -- and ``provenance.json`` is
# demoted from authority to a declaration that must agree with it exactly.  With
# no injection there is no baseline authority at all, so the anchors fail closed
# instead of silently self-certifying.
BASELINE_COMMIT_ENV = "DPS_BASELINE_COMMIT"
_FULL_COMMIT_SHA = re.compile(r"\A[0-9a-f]{40}\Z")


def _run_trusted_git(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [_trusted_git_executable(), *args],
        cwd=str(ROOT),
        capture_output=True,
        check=False,
    )


def _assert_well_formed_commit(raw: Any) -> str:
    if raw is None:
        raise AssertionError(
            "{0} is not set; the baseline commit has no external authority and the "
            "dual-run anchors cannot be trusted -- the runner must inject it".format(
                BASELINE_COMMIT_ENV
            )
        )
    value = str(raw).strip()
    if not _FULL_COMMIT_SHA.match(value):
        raise AssertionError(
            "{0}={1!r} is not a full 40-hex commit id; revisions such as HEAD or "
            "refs are candidate-influenceable and are refused".format(BASELINE_COMMIT_ENV, raw)
        )
    return value


def _assert_commit_is_ancestor_of_head(commit: str) -> None:
    resolved = _run_trusted_git("rev-parse", "--verify", "--quiet", commit + "^{commit}")
    if resolved.returncode != 0:
        raise AssertionError(
            "injected baseline commit {0} does not exist in this repository".format(commit)
        )
    ancestry = _run_trusted_git("merge-base", "--is-ancestor", commit, "HEAD")
    if ancestry.returncode != 0:
        raise AssertionError(
            "injected baseline commit {0} is not an ancestor of HEAD; the frozen "
            "corpus would not be this branch's pre-migration state".format(commit)
        )


def _assert_provenance_declares(commit: str) -> None:
    declared = _load_json(FIXTURES / "provenance.json").get("baseline_commit")
    if declared != commit:
        raise AssertionError(
            "provenance.json declares baseline_commit {0!r} but the runner injected "
            "{1!r}; the frozen fixtures do not describe the commit the gate anchors "
            "to".format(declared, commit)
        )


def _require_injected_baseline_commit() -> str:
    commit = _assert_well_formed_commit(os.environ.get(BASELINE_COMMIT_ENV))
    _assert_commit_is_ancestor_of_head(commit)
    _assert_provenance_declares(commit)
    return commit


def _baseline_manifest_paths() -> List[Path]:
    return sorted((BASELINE / "manifests").glob("*.module.yaml"))


def _current_manifest_paths() -> List[Path]:
    return sorted((ROOT / "Modules").glob("*/module.yaml"))


def _module_id_of_baseline(path: Path) -> str:
    return path.name[: -len(".module.yaml")]


def _apply_mutation(manifest: Mapping[str, Any], mutation: Mapping[str, Any]) -> Dict[str, Any]:
    """Return a copy of ``manifest`` with the mutation applied to its agents block."""

    mutated = copy.deepcopy(dict(manifest))
    agents = dict(mutated.get("agents", {}))
    for key in mutation.get("remove", []):
        agents.pop(key, None)
    agents.update(mutation.get("set", {}))
    mutated["agents"] = agents
    return mutated


class FrozenCorpusIntegrityTest(unittest.TestCase):
    """The frozen corpus is evidence, so its bytes are pinned by digest."""

    def setUp(self) -> None:
        self.provenance = _load_json(FIXTURES / "provenance.json")

    def test_provenance_pins_the_pre_migration_baseline_commit(self) -> None:
        self.assertEqual(
            self.provenance["corpus"],
            "dps.r0b-instruction-receipt-migration-corpus/v1",
        )
        self.assertEqual(self.provenance["module_count"], EXPECTED_MODULE_COUNT)
        self.assertRegex(self.provenance["baseline_commit"], r"^[0-9a-f]{40}$")
        # One schema plus one manifest per registered module.
        self.assertEqual(len(self.provenance["files"]), EXPECTED_MODULE_COUNT + 1)

    def test_every_frozen_file_matches_its_recorded_digest(self) -> None:
        recorded = self.provenance["files"]
        on_disk = {
            "governance/schemas/module-manifest.schema.json": BASELINE
            / "module-manifest.schema.json"
        }
        for path in _baseline_manifest_paths():
            on_disk["Modules/{0}/module.yaml".format(_module_id_of_baseline(path))] = path

        self.assertEqual(sorted(on_disk), sorted(recorded))
        mismatched = [
            key
            for key, path in sorted(on_disk.items())
            if sha256_bytes(path.read_bytes()) != recorded[key]["sha256"]
        ]
        self.assertEqual([], mismatched, "frozen dual-run corpus was altered")

    def test_frozen_baseline_covers_exactly_the_registered_modules(self) -> None:
        frozen = {_module_id_of_baseline(path) for path in _baseline_manifest_paths()}
        live = {path.parent.name for path in _current_manifest_paths()}
        self.assertEqual(EXPECTED_MODULE_COUNT, len(frozen))
        self.assertEqual(live, frozen)


class SchemaRuleChangeTest(unittest.TestCase):
    """Pin the exact rule delta so the dual-run result cannot be explained away."""

    def setUp(self) -> None:
        self.old = _load_json(BASELINE / "module-manifest.schema.json")
        self.new = _load_json(LIVE_SCHEMA_PATH)

    def test_resolver_is_the_only_removed_agents_rule(self) -> None:
        old_agents = self.old["properties"]["agents"]
        new_agents = self.new["properties"]["agents"]

        self.assertIn("resolver", old_agents["required"])
        self.assertNotIn("resolver", new_agents["required"])
        self.assertEqual(
            {"const": "factory-instruction-resolver"},
            old_agents["properties"]["resolver"],
        )
        self.assertNotIn("resolver", new_agents["properties"])
        self.assertEqual(
            sorted(set(old_agents["required"]) - {"resolver"}),
            sorted(new_agents["required"]),
        )
        self.assertEqual(
            sorted(set(old_agents["properties"]) - {"resolver"}),
            sorted(new_agents["properties"]),
        )

    def test_both_schemas_keep_the_agents_block_fail_closed(self) -> None:
        for label, schema in (("old", self.old), ("new", self.new)):
            with self.subTest(schema=label):
                agents = schema["properties"]["agents"]
                self.assertFalse(agents["additionalProperties"])
                # RebuildPlan 4.2.2: receiptRequired=true survives the migration.
                self.assertEqual({"const": True}, agents["properties"]["receiptRequired"])
                self.assertIn("receiptRequired", agents["required"])


class DualRunAcceptanceMatrixTest(unittest.TestCase):
    """The four-cell old/new acceptance matrix required by RebuildPlan 4.2.3."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.old_schema = _load_json(BASELINE / "module-manifest.schema.json")
        cls.new_schema = _load_json(LIVE_SCHEMA_PATH)
        cls.old_manifests = {
            _module_id_of_baseline(path): _load_json(path)
            for path in _baseline_manifest_paths()
        }
        cls.new_manifests = {
            path.parent.name: _load_json(path) for path in _current_manifest_paths()
        }

    def test_old_schema_accepts_every_baseline_manifest(self) -> None:
        rejected = {
            module_id: validate_json_schema(manifest, self.old_schema)
            for module_id, manifest in sorted(self.old_manifests.items())
            if validate_json_schema(manifest, self.old_schema)
        }
        self.assertEqual(EXPECTED_MODULE_COUNT, len(self.old_manifests))
        self.assertEqual({}, rejected, "old schema must accept the pre-migration corpus")

    def test_new_schema_accepts_every_current_manifest(self) -> None:
        rejected = {
            module_id: validate_json_schema(manifest, self.new_schema)
            for module_id, manifest in sorted(self.new_manifests.items())
            if validate_json_schema(manifest, self.new_schema)
        }
        self.assertEqual(EXPECTED_MODULE_COUNT, len(self.new_manifests))
        self.assertEqual({}, rejected, "new schema must accept the migrated corpus")

    def test_old_schema_rejects_all_34_current_manifests(self) -> None:
        """Half of the mutual-rejection fact: no migrated manifest passes the old rules."""

        accepted: List[str] = []
        wrong_reason: List[str] = []
        for module_id, manifest in sorted(self.new_manifests.items()):
            errors = validate_json_schema(manifest, self.old_schema)
            if not errors:
                accepted.append(module_id)
                continue
            if not any("$.agents: missing required property resolver" == e for e in errors):
                wrong_reason.append("{0}: {1}".format(module_id, errors))
        self.assertEqual(EXPECTED_MODULE_COUNT, len(self.new_manifests))
        self.assertEqual([], accepted, "old schema must reject every migrated manifest")
        self.assertEqual([], wrong_reason)

    def test_new_schema_rejects_all_34_baseline_manifests(self) -> None:
        """The other half: no pre-migration manifest passes the new rules."""

        accepted: List[str] = []
        wrong_reason: List[str] = []
        for module_id, manifest in sorted(self.old_manifests.items()):
            errors = validate_json_schema(manifest, self.new_schema)
            if not errors:
                accepted.append(module_id)
                continue
            if not any("$.agents: unexpected property resolver" == e for e in errors):
                wrong_reason.append("{0}: {1}".format(module_id, errors))
        self.assertEqual(EXPECTED_MODULE_COUNT, len(self.old_manifests))
        self.assertEqual([], accepted, "new schema must reject every pre-migration manifest")
        self.assertEqual([], wrong_reason)


class NegativeCorpusTest(unittest.TestCase):
    """Frozen attack corpus, judged independently by the old and the new schema."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.corpus = _load_json(FIXTURES / "negative-samples.json")
        cls.old_schema = _load_json(BASELINE / "module-manifest.schema.json")
        cls.new_schema = _load_json(LIVE_SCHEMA_PATH)
        base = cls.corpus["base_module"]
        cls.old_base = _load_json(BASELINE / "manifests" / "{0}.module.yaml".format(base))
        cls.new_base = _load_json(ROOT / "Modules" / base / "module.yaml")

    def test_corpus_covers_the_required_attack_classes(self) -> None:
        identifiers = {sample["id"] for sample in self.corpus["samples"]}
        self.assertLessEqual(
            {
                "unknown-field",
                "legacy-resolver",
                "missing-receiptRequired",
                "false-receiptRequired",
            },
            identifiers,
        )

    def test_unmutated_bases_are_positive_samples(self) -> None:
        """Guards against a negative sample passing only because its base was broken."""

        self.assertEqual([], validate_json_schema(self.old_base, self.old_schema))
        self.assertEqual([], validate_json_schema(self.new_base, self.new_schema))

    def test_every_negative_sample_matches_its_recorded_verdict(self) -> None:
        for sample in self.corpus["samples"]:
            mutation = sample["mutation"]
            needle = sample["expected_error_substring"]
            for label, base, schema, expectation in (
                ("old", self.old_base, self.old_schema, sample["old_schema_expectation"]),
                ("new", self.new_base, self.new_schema, sample["new_schema_expectation"]),
            ):
                with self.subTest(sample=sample["id"], schema=label):
                    errors = validate_json_schema(_apply_mutation(base, mutation), schema)
                    if expectation == "reject":
                        self.assertNotEqual([], errors)
                        self.assertTrue(
                            any(needle in error for error in errors),
                            "{0}/{1} rejected for the wrong reason: {2}".format(
                                sample["id"], label, errors
                            ),
                        )
                    else:
                        self.assertEqual(
                            [],
                            errors,
                            "{0}/{1} was expected to be accepted".format(sample["id"], label),
                        )

    def test_reintroducing_the_factory_resolver_breaks_the_new_gate(self) -> None:
        """The regression this batch exists to prevent, asserted on its own."""

        mutated = _apply_mutation(
            self.new_base, {"set": {"resolver": "factory-instruction-resolver"}}
        )
        self.assertIn(
            "$.agents: unexpected property resolver",
            validate_json_schema(mutated, self.new_schema),
        )


# What the attack corpus must *prove* is pinned here in code, not read from the
# JSON file the corpus lives in.  ``negative-samples.json`` carries its own
# expectations, so a rewrite could neuter a mutation into a no-op, flip a verdict
# to "accept" and still satisfy a test that trusts the file -- the dual-run would
# keep reporting green while proving nothing.  These pinned verdicts, and the exact
# validator error each one must raise, make that rewrite fail instead.
REQUIRED_ATTACK_CLASSES: Dict[str, Dict[str, Any]] = {
    "unknown-field": {
        "old": ("reject", "$.agents: unexpected property resolverHint"),
        "new": ("reject", "$.agents: unexpected property resolverHint"),
    },
    "legacy-resolver": {
        "old": ("reject", "$.agents.resolver: value does not match const"),
        "new": ("reject", "$.agents: unexpected property resolver"),
    },
    # The migration direction itself: legal before R0-B, rejected after it.
    "legacy-resolver-reintroduced": {
        "old": ("accept", None),
        "new": ("reject", "$.agents: unexpected property resolver"),
    },
    "missing-receiptRequired": {
        "old": ("reject", "$.agents: missing required property receiptRequired"),
        "new": ("reject", "$.agents: missing required property receiptRequired"),
    },
    "false-receiptRequired": {
        "old": ("reject", "$.agents.receiptRequired: value does not match const"),
        "new": ("reject", "$.agents.receiptRequired: value does not match const"),
    },
}


class AttackCorpusIsLoadBearingTest(unittest.TestCase):
    """Lock the dual-run attack corpus itself, not merely the file that holds it."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.corpus = _load_json(FIXTURES / "negative-samples.json")
        cls.old_schema = _load_json(BASELINE / "module-manifest.schema.json")
        cls.new_schema = _load_json(LIVE_SCHEMA_PATH)
        base = cls.corpus["base_module"]
        cls.old_base = _load_json(BASELINE / "manifests" / "{0}.module.yaml".format(base))
        cls.new_base = _load_json(ROOT / "Modules" / base / "module.yaml")
        cls.samples = {sample["id"]: sample for sample in cls.corpus["samples"]}

    def test_corpus_declares_exactly_the_pinned_attack_classes(self) -> None:
        self.assertEqual(sorted(REQUIRED_ATTACK_CLASSES), sorted(self.samples))

    def test_declared_expectations_match_the_pinned_verdicts(self) -> None:
        """A weakened expectation in the corpus file must fail, not be believed."""

        for sample_id, pinned in sorted(REQUIRED_ATTACK_CLASSES.items()):
            sample = self.samples[sample_id]
            with self.subTest(sample=sample_id):
                self.assertEqual(pinned["old"][0], sample["old_schema_expectation"])
                self.assertEqual(pinned["new"][0], sample["new_schema_expectation"])

    def test_every_rejecting_sample_actually_mutates_its_base(self) -> None:
        """A no-op mutation would 'pass' by validating an untouched manifest.

        Only the rejecting side must differ from its base.  An accepting sample may
        legitimately coincide with it -- ``legacy-resolver-reintroduced`` restores
        exactly what the pre-migration manifest already declared, which is the point
        of that sample.
        """

        for sample_id, pinned in sorted(REQUIRED_ATTACK_CLASSES.items()):
            mutation = self.samples[sample_id]["mutation"]
            for label, base in (("old", self.old_base), ("new", self.new_base)):
                if pinned[label][0] != "reject":
                    continue
                with self.subTest(sample=sample_id, base=label):
                    self.assertNotEqual(
                        base["agents"],
                        _apply_mutation(base, mutation)["agents"],
                        "mutation is a no-op, so this sample proves nothing",
                    )

    def test_pinned_attack_verdicts_hold_against_both_schemas(self) -> None:
        """The dual-run proper, judged only against the code-pinned expectations."""

        for sample_id, pinned in sorted(REQUIRED_ATTACK_CLASSES.items()):
            mutation = self.samples[sample_id]["mutation"]
            for label, base, schema in (
                ("old", self.old_base, self.old_schema),
                ("new", self.new_base, self.new_schema),
            ):
                verdict, expected_error = pinned[label]
                with self.subTest(sample=sample_id, schema=label):
                    errors = validate_json_schema(_apply_mutation(base, mutation), schema)
                    if verdict == "reject":
                        self.assertIn(expected_error, errors)
                    else:
                        self.assertEqual([], errors)


def _git(root: Path, *arguments: str) -> str:
    result = subprocess.run(
        [_trusted_git_executable(), *arguments],
        cwd=str(root),
        capture_output=True,
        text=True,
        check=True,
    )
    return result.stdout.strip()


_SYNTHETIC_AGENTS = """---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: {module_id}
manifest: ./module.yaml
applies_to: .
---

# {module_id}

Read the root AGENTS.md, this file and module.yaml before writing.
Contracts, compatibility, tests, canary and rollout, rollback and
communication rules are all owned here.
"""


def _synthetic_manifest(module_id: str, policy_version: str = "1.0.0") -> Dict[str, Any]:
    return {
        "schemaVersion": "dps.module/v1",
        "module": {"id": module_id},
        "paths": {
            "actualRoot": "Modules/" + module_id,
            "canonicalRoot": "modules/" + module_id,
            "owned": ["Modules/{0}/**".format(module_id)],
            "excluded": [],
            "runtimeData": [],
        },
        "contracts": {"provided": [], "consumed": []},
        "dependencies": [],
        "agents": {
            "spec": "dps.agents/v1",
            "policyVersion": policy_version,
            "instructionsFile": "AGENTS.md",
            "manifestFile": "module.yaml",
            "receiptRequired": True,
        },
    }


class ReceiptManifestBindingTest(unittest.TestCase):
    """Prove the receipt binds manifest bytes, on a disposable synthetic repository.

    The real ``Modules/*/module.yaml`` files are protected legacy anchors, so the
    byte-mutation half of this proof runs against a throwaway git repository built
    here.  The code under test is the production ``resolve_instruction_receipt`` /
    ``validate_instruction_receipt`` pair, unpatched.
    """

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.repo = Path(self._temporary.name) / "repo"
        (self.repo / "Modules").mkdir(parents=True)
        (self.repo / "AGENTS.md").write_text(
            "# Root instructions\n\nManifest, contracts, compatibility, tests,\n"
            "canary, rollout, rollback and communication rules live here.\n",
            encoding="utf-8",
        )
        for module_id in ("alpha-module", "beta-module"):
            module_root = self.repo / "Modules" / module_id
            for directory in ("src", "contracts/provided", "contracts/consumed", "tests", "migrations", "operations"):
                (module_root / directory).mkdir(parents=True)
                (module_root / directory / ".keep").write_text("", encoding="utf-8")
            (module_root / "CHANGELOG.md").write_text("# Changelog\n", encoding="utf-8")
            (module_root / "AGENTS.md").write_text(
                _SYNTHETIC_AGENTS.format(module_id=module_id), encoding="utf-8"
            )
            (module_root / "module.yaml").write_text(
                json.dumps(_synthetic_manifest(module_id), indent=2) + "\n",
                encoding="utf-8",
            )
        _git(self.repo, "init", "--quiet", "--initial-branch=main")
        _git(self.repo, "-c", "user.name=dps", "-c", "user.email=dps@example.invalid", "add", "-A")
        _git(
            self.repo,
            "-c",
            "user.name=dps",
            "-c",
            "user.email=dps@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline",
        )
        self.baseline = _git(self.repo, "rev-parse", "HEAD")
        self.manifest_path = self.repo / "Modules" / "alpha-module" / "module.yaml"

    def _receipt(self) -> Dict[str, Any]:
        return resolve_instruction_receipt(
            self.repo,
            self.baseline,
            resolved_at="2026-01-01T00:00:00Z",
        )

    def _bound_manifest(self, receipt: Mapping[str, Any], module_id: str) -> Dict[str, Any]:
        entries = [
            entry
            for entry in receipt["manifests"]
            if entry["path"] == "Modules/{0}/module.yaml".format(module_id)
        ]
        self.assertEqual(1, len(entries))
        return entries[0]

    def test_receipt_is_deterministic_for_unchanged_bytes(self) -> None:
        first = self._receipt()
        second = self._receipt()
        self.assertEqual(first, second)
        self.assertEqual("BOUND", first["status"])
        self.assertEqual("dps.phase0-instruction-receipt/v1", first["schema_version"])

    def test_receipt_scope_covers_every_registered_module(self) -> None:
        receipt = self._receipt()
        self.assertEqual(["alpha-module", "beta-module"], receipt["scope"])
        self.assertEqual(
            ["Modules/alpha-module/module.yaml", "Modules/beta-module/module.yaml"],
            sorted(entry["path"] for entry in receipt["manifests"]),
        )

    def test_manifest_bytes_are_bound_by_sha256(self) -> None:
        receipt = self._receipt()
        entry = self._bound_manifest(receipt, "alpha-module")
        self.assertEqual(
            sha256_bytes(self.manifest_path.read_bytes()), entry["sha256"]
        )
        self.assertEqual("tracked", entry["source_state"])

    def test_changing_a_manifest_changes_the_receipt_digest(self) -> None:
        before = self._receipt()
        mutated = _synthetic_manifest("alpha-module", policy_version="1.0.1")
        self.manifest_path.write_text(json.dumps(mutated, indent=2) + "\n", encoding="utf-8")
        after = self._receipt()

        before_entry = self._bound_manifest(before, "alpha-module")
        after_entry = self._bound_manifest(after, "alpha-module")
        self.assertNotEqual(before_entry["sha256"], after_entry["sha256"])
        self.assertEqual("modified", after_entry["source_state"])
        self.assertNotEqual(before["receipt_id"], after["receipt_id"])
        self.assertNotEqual(before["diff_fingerprint"], after["diff_fingerprint"])
        # An unchanged tree binds every module (an empty receipt is never
        # evidence); once a manifest moves, the scope narrows to its owner.
        self.assertEqual(["alpha-module", "beta-module"], before["scope"])
        self.assertEqual(["alpha-module"], after["scope"])

    def test_stale_receipt_is_rejected_after_a_manifest_edit(self) -> None:
        receipt = self._receipt()
        current, reason, _ = validate_instruction_receipt(self.repo, receipt)
        self.assertTrue(current, reason)

        mutated = _synthetic_manifest("alpha-module", policy_version="1.0.1")
        self.manifest_path.write_text(json.dumps(mutated, indent=2) + "\n", encoding="utf-8")

        still_current, reason, _ = validate_instruction_receipt(self.repo, receipt)
        self.assertFalse(still_current, "staleness detection must fail closed")
        self.assertIn("stale", reason)
        self.assertIn("manifests", reason)

    def test_adding_a_module_manifest_widens_the_bound_scope(self) -> None:
        before = self._receipt()
        self.assertNotIn("gamma-module", before["scope"])
        module_root = self.repo / "Modules" / "gamma-module"
        for directory in ("src", "contracts/provided", "contracts/consumed", "tests", "migrations", "operations"):
            (module_root / directory).mkdir(parents=True)
            (module_root / directory / ".keep").write_text("", encoding="utf-8")
        (module_root / "CHANGELOG.md").write_text("# Changelog\n", encoding="utf-8")
        (module_root / "AGENTS.md").write_text(
            _SYNTHETIC_AGENTS.format(module_id="gamma-module"), encoding="utf-8"
        )
        (module_root / "module.yaml").write_text(
            json.dumps(_synthetic_manifest("gamma-module"), indent=2) + "\n",
            encoding="utf-8",
        )
        after = self._receipt()
        self.assertIn("gamma-module", after["scope"])
        self.assertEqual(
            "untracked",
            self._bound_manifest(after, "gamma-module")["source_state"],
        )
        self.assertNotEqual(before["receipt_id"], after["receipt_id"])

    def test_unknown_required_scope_fails_closed(self) -> None:
        with self.assertRaises(Phase0Error):
            resolve_instruction_receipt(
                self.repo,
                self.baseline,
                resolved_at="2026-01-01T00:00:00Z",
                required_scope=["does-not-exist"],
            )


def _git_blob(commit: str, repo_relpath: str) -> bytes:
    """Raw bytes of ``repo_relpath`` at ``commit`` with no newline munging."""

    result = subprocess.run(
        [
            _trusted_git_executable(),
            "-c",
            "core.autocrlf=false",
            "show",
            "{0}:{1}".format(commit, repo_relpath),
        ],
        cwd=str(ROOT),
        capture_output=True,
        check=True,
    )
    return result.stdout


def _function_source(text: str, func_name: str) -> str:
    """Exact source of a top-level function, extracted structurally so unrelated
    edits elsewhere in the file cannot mask a change to the function itself."""

    tree = ast.parse(text)
    for node in tree.body:
        if isinstance(node, ast.FunctionDef) and node.name == func_name:
            segment = ast.get_source_segment(text, node)
            if segment is not None:
                return segment
    raise AssertionError("function {0} not found".format(func_name))


class BaselineCommitAnchoringTest(unittest.TestCase):
    """RebuildPlan 4.2.3 requires freezing the *old validator*, not just the old
    schema.  The v1/v2 dual-run reuses the current ``validate_json_schema`` for
    both majors.  That reuse is sound only if two facts are proven fail-closed
    rather than assumed:

    * the frozen fixtures really are the pre-migration bytes -- anchored to the
      immutable baseline commit blob, not merely to a digest recorded beside them
      in ``provenance.json``, which an attacker could rewrite in the same change.
      Which commit that is comes from the runner-injected ``DPS_BASELINE_COMMIT``
      and never from the candidate tree; ``provenance.json`` only has to agree
      with it (see ``_require_injected_baseline_commit``);
    * the validator *core* is unchanged since that baseline.  4.2(5) adds
      per-schemaVersion dispatch to ``phase0.py``, so the file as a whole changes
      legitimately; what must stay frozen is ``validate_json_schema`` itself, so
      "current validator" and "old validator" are provably the same code.

    If either stops holding these tests fail, forcing a genuinely isolated
    old/new validator run instead of two schemas compared under one validator.
    """

    def setUp(self) -> None:
        self.baseline_commit = _require_injected_baseline_commit()

    def test_frozen_fixtures_equal_the_baseline_commit_blobs(self) -> None:
        frozen = {
            "governance/schemas/module-manifest.schema.json": BASELINE
            / "module-manifest.schema.json"
        }
        for path in _baseline_manifest_paths():
            frozen["Modules/{0}/module.yaml".format(_module_id_of_baseline(path))] = path

        drift = [
            repo_relpath
            for repo_relpath, fixture in sorted(frozen.items())
            if fixture.read_bytes() != _git_blob(self.baseline_commit, repo_relpath)
        ]
        self.assertEqual(
            [],
            drift,
            "frozen fixture bytes diverge from the baseline commit blobs; the "
            "old-schema side of the dual-run is no longer the real pre-migration corpus",
        )

    def test_receipt_validator_is_unchanged_in_this_batch(self) -> None:
        # 4.2(5) adds per-schemaVersion dispatch to phase0, so the file legitimately
        # changes.  What must stay frozen is the shared validator *core* --
        # validate_json_schema -- reused for both the v1 and v2 sides.  Anchor that
        # function's source to the baseline so a change to the validator itself (as
        # opposed to the surrounding dispatch) fails closed.
        baseline_source = _function_source(
            _git_blob(self.baseline_commit, "Tools/ci/phase0.py").decode("utf-8"),
            "validate_json_schema",
        )
        current_source = _function_source(
            (ROOT / "Tools" / "ci" / "phase0.py").read_text(encoding="utf-8"),
            "validate_json_schema",
        )
        self.assertEqual(
            baseline_source,
            current_source,
            "validate_json_schema (the dual-run validator core) changed since the "
            "baseline; the dual-run may no longer reuse one validator for both majors "
            "and must execute the frozen baseline validator in isolation",
        )


class BaselineAuthorityFailClosedTest(unittest.TestCase):
    """Finding 3: prove the baseline authority is *external* and fails closed.

    The anchoring test above is only as strong as where it learns the baseline
    commit from.  These cases drive ``_require_injected_baseline_commit`` directly
    and assert that every way of removing, weakening or contradicting the runner
    injection raises rather than silently anchoring to something the candidate
    controls.  Without them a future edit could quietly restore the self-anchoring
    ``provenance.json`` read and every anchor above would still report green.
    """

    @staticmethod
    def _with_injection(value: Any):
        environment = dict(os.environ)
        environment.pop(BASELINE_COMMIT_ENV, None)
        if value is not None:
            environment[BASELINE_COMMIT_ENV] = value
        return mock.patch.dict(os.environ, environment, clear=True)

    def _head_commit(self) -> str:
        resolved = _run_trusted_git("rev-parse", "--verify", "HEAD^{commit}")
        self.assertEqual(0, resolved.returncode, resolved.stderr.decode("utf-8", "replace"))
        return resolved.stdout.decode("ascii").strip()

    def _unreferenced_commit(self) -> str:
        """A real commit object that is not on HEAD's ancestry."""

        tree = _run_trusted_git("hash-object", "-t", "tree", "-w", "--stdin")
        self.assertEqual(0, tree.returncode, tree.stderr.decode("utf-8", "replace"))
        created = subprocess.run(
            [
                _trusted_git_executable(),
                "commit-tree",
                tree.stdout.decode("ascii").strip(),
                "-m",
                "r0b baseline-authority ancestry probe",
            ],
            cwd=str(ROOT),
            input=b"",
            capture_output=True,
            check=False,
            env=dict(
                os.environ,
                GIT_AUTHOR_NAME="dps-test",
                GIT_AUTHOR_EMAIL="dps-test@invalid",
                GIT_AUTHOR_DATE="1700000000 +0000",
                GIT_COMMITTER_NAME="dps-test",
                GIT_COMMITTER_EMAIL="dps-test@invalid",
                GIT_COMMITTER_DATE="1700000000 +0000",
            ),
        )
        self.assertEqual(0, created.returncode, created.stderr.decode("utf-8", "replace"))
        return created.stdout.decode("ascii").strip()

    def test_absent_injection_fails_closed(self) -> None:
        with self._with_injection(None):
            with self.assertRaises(AssertionError) as raised:
                _require_injected_baseline_commit()
        self.assertIn(BASELINE_COMMIT_ENV, str(raised.exception))

    def test_empty_injection_fails_closed(self) -> None:
        with self._with_injection("   "):
            with self.assertRaises(AssertionError):
                _require_injected_baseline_commit()

    def test_revision_expressions_are_refused(self) -> None:
        # "HEAD" or a branch name would resolve through candidate-writable refs,
        # so only a full immutable object id is accepted.
        for expression in ("HEAD", "main", "8f63593", "8F63593D4F262EC1496B05300DA75A71B86EAAB4"):
            with self.subTest(expression=expression):
                with self._with_injection(expression):
                    with self.assertRaises(AssertionError):
                        _require_injected_baseline_commit()

    def test_unknown_commit_fails_closed(self) -> None:
        with self._with_injection("0" * 40):
            with self.assertRaises(AssertionError) as raised:
                _require_injected_baseline_commit()
        self.assertIn("does not exist", str(raised.exception))

    def test_commit_outside_head_ancestry_fails_closed(self) -> None:
        stranger = self._unreferenced_commit()
        with self._with_injection(stranger):
            with self.assertRaises(AssertionError) as raised:
                _require_injected_baseline_commit()
        self.assertIn("not an ancestor of HEAD", str(raised.exception))

    def test_provenance_disagreeing_with_the_injection_fails_closed(self) -> None:
        # HEAD is a real ancestor of itself, so this isolates the last check:
        # the frozen corpus must describe exactly the injected baseline.
        head = self._head_commit()
        self.assertNotEqual(
            head,
            _load_json(FIXTURES / "provenance.json")["baseline_commit"],
            "precondition: HEAD must differ from the declared baseline",
        )
        with self._with_injection(head):
            with self.assertRaises(AssertionError) as raised:
                _require_injected_baseline_commit()
        self.assertIn("provenance.json declares", str(raised.exception))

    def test_matching_injection_is_accepted(self) -> None:
        declared = _load_json(FIXTURES / "provenance.json")["baseline_commit"]
        with self._with_injection(declared):
            self.assertEqual(declared, _require_injected_baseline_commit())


class GitAnchoringTrustBoundaryTest(unittest.TestCase):
    """Finding 2: the baseline anchors must not trust a PATH-selectable bare git.
    They adopt the repository's locked-git boundary (run_candidate_gate's
    /usr/bin/git), so a hostile git prepended to PATH cannot fabricate blob bytes.
    """

    def test_anchor_helpers_resolve_the_locked_absolute_git(self) -> None:
        resolved = _trusted_git_executable()
        self.assertEqual(str(LOCKED_GIT), resolved)
        self.assertTrue(Path(resolved).is_absolute())
        self.assertFalse(Path(resolved).is_symlink())

    def test_poisoned_path_cannot_substitute_a_hostile_git(self) -> None:
        # Prepend a directory containing a hostile "git" to PATH.  Because the
        # anchors call the locked /usr/bin/git rather than resolving "git" from
        # PATH, _git_blob still returns the real baseline blob, not the hostile
        # output -- the fail-closed proof, not a mere PATH-dependency note.
        baseline_commit = _require_injected_baseline_commit()
        with tempfile.TemporaryDirectory() as poisoned_dir:
            hostile = Path(poisoned_dir) / "git"
            hostile.write_text("#!/bin/sh\necho HOSTILE-GIT-OUTPUT\n", encoding="utf-8")
            hostile.chmod(0o755)
            original_path = os.environ.get("PATH", "")
            os.environ["PATH"] = poisoned_dir + os.pathsep + original_path
            try:
                blob = _git_blob(
                    baseline_commit,
                    "governance/schemas/module-manifest.schema.json",
                )
            finally:
                os.environ["PATH"] = original_path
        self.assertNotIn(b"HOSTILE-GIT-OUTPUT", blob)
        self.assertIn(b"schemaVersion", blob)

    def test_a_repo_local_planted_git_on_path_is_ignored(self) -> None:
        # Even a hostile git planted inside the repo tree (the most reachable
        # location for candidate code) and prepended to PATH is ignored, because
        # resolution is locked to the absolute path, not PATH order.
        with tempfile.TemporaryDirectory(dir=str(ROOT)) as poisoned_dir:
            hostile = Path(poisoned_dir) / "git"
            hostile.write_text("#!/bin/sh\necho HOSTILE\n", encoding="utf-8")
            hostile.chmod(0o755)
            original_path = os.environ.get("PATH", "")
            os.environ["PATH"] = poisoned_dir + os.pathsep + original_path
            try:
                head = _git(ROOT, "rev-parse", "HEAD")
            finally:
                os.environ["PATH"] = original_path
        self.assertNotIn("HOSTILE", head)
        self.assertRegex(head, r"^[0-9a-f]{40}$")


class MajorCoexistenceDispatchTest(unittest.TestCase):
    """RebuildPlan 4.2(5): dps.module/v1 (resolver-bearing, historical/rollback)
    and dps.module/v2 (resolver removed, current) coexist.  Consumers dispatch per
    manifest ``schemaVersion``; unknown/missing majors fail closed; the historical
    v1 corpus stays interpretable without contaminating the active v2 world.

    Proof-obligation 9 (a change to validator, fixture or major map turns the
    dual-run red) is carried by BaselineCommitAnchoringTest (fixtures anchored to
    the commit blob, validator core frozen) plus the major-const code-pin below.
    """

    @classmethod
    def setUpClass(cls) -> None:
        cls.live_v1 = _load_json(LIVE_V1_SCHEMA_PATH)
        cls.live_v2 = _load_json(LIVE_V2_SCHEMA_PATH)
        cls.baseline_manifests = {
            _module_id_of_baseline(path): _load_json(path)
            for path in _baseline_manifest_paths()
        }
        cls.live_manifests = {
            path.parent.name: _load_json(path) for path in _current_manifest_paths()
        }
        cls.dispatch = phase0.load_manifest_schemas(ROOT)

    def _a_live_manifest(self) -> Dict[str, Any]:
        return copy.deepcopy(next(iter(sorted(self.live_manifests.items())))[1])

    def test_live_schemas_pin_two_distinct_majors(self) -> None:
        # major-map code-pin (proof-obligation 9): reverting either major fails here.
        self.assertEqual("dps.module/v1", self.live_v1["properties"]["schemaVersion"]["const"])
        self.assertEqual("dps.module/v2", self.live_v2["properties"]["schemaVersion"]["const"])
        self.assertIn("resolver", self.live_v1["properties"]["agents"]["properties"])
        self.assertNotIn("resolver", self.live_v2["properties"]["agents"]["properties"])
        self.assertEqual({"dps.module/v1", "dps.module/v2"}, set(self.dispatch))

    def test_v1_schema_accepts_the_frozen_v1_manifests(self) -> None:
        # proof-obligation 1
        rejected = {
            module_id: validate_json_schema(manifest, self.live_v1)
            for module_id, manifest in sorted(self.baseline_manifests.items())
            if validate_json_schema(manifest, self.live_v1)
        }
        self.assertEqual(EXPECTED_MODULE_COUNT, len(self.baseline_manifests))
        self.assertEqual({}, rejected, "live v1 schema must accept the frozen v1 corpus")

    def test_v2_schema_accepts_the_current_v2_manifests(self) -> None:
        # proof-obligation 2
        self.assertEqual(EXPECTED_MODULE_COUNT, len(self.live_manifests))
        wrong_major = {
            module_id: manifest.get("schemaVersion")
            for module_id, manifest in sorted(self.live_manifests.items())
            if manifest.get("schemaVersion") != "dps.module/v2"
        }
        self.assertEqual({}, wrong_major, "every live manifest must declare v2")
        rejected = {
            module_id: validate_json_schema(manifest, self.live_v2)
            for module_id, manifest in sorted(self.live_manifests.items())
            if validate_json_schema(manifest, self.live_v2)
        }
        self.assertEqual({}, rejected, "live v2 schema must accept the current corpus")

    def test_majors_are_not_confused_by_a_shared_version_name(self) -> None:
        # proof-obligation 3: cross-major application is rejected on the version const
        const_error = "$.schemaVersion: value does not match const"
        for module_id, manifest in sorted(self.live_manifests.items()):
            with self.subTest(direction="v2-manifest-under-v1-schema", module=module_id):
                self.assertIn(const_error, validate_json_schema(manifest, self.live_v1))
        for module_id, manifest in sorted(self.baseline_manifests.items()):
            with self.subTest(direction="v1-manifest-under-v2-schema", module=module_id):
                self.assertIn(const_error, validate_json_schema(manifest, self.live_v2))

    def test_phase0_dispatches_a_v1_manifest_to_the_v1_schema(self) -> None:
        # proof-obligation 4: resolver-bearing v1 stays interpretable via the v1 path
        sample = next(iter(sorted(self.baseline_manifests.items())))[1]
        self.assertEqual("dps.module/v1", sample["schemaVersion"])
        self.assertIn("resolver", sample["agents"])
        chosen = self.dispatch[sample["schemaVersion"]]
        self.assertEqual([], validate_json_schema(sample, chosen))

    def test_v2_reintroducing_the_resolver_is_rejected(self) -> None:
        # proof-obligation 5
        sample = self._a_live_manifest()
        sample.setdefault("agents", {})["resolver"] = "factory-instruction-resolver"
        self.assertIn(
            "$.agents: unexpected property resolver",
            validate_json_schema(sample, self.live_v2),
        )

    def test_unknown_or_missing_major_has_no_dispatch_target(self) -> None:
        # proof-obligation 6: fail closed -- no schema for an unknown/missing major
        self.assertNotIn("dps.module/v9", self.dispatch)
        self.assertNotIn(None, self.dispatch)
        missing = self._a_live_manifest()
        del missing["schemaVersion"]
        self.assertNotIn(missing.get("schemaVersion"), self.dispatch)

    def test_phase0_and_f9_agree_on_the_supported_majors(self) -> None:
        # proof-obligation 7
        self.assertEqual(set(self.dispatch), set(SUPPORTED_MODULE_MANIFEST_MAJORS))

    def test_historical_and_active_corpora_are_disjoint_worlds(self) -> None:
        # proof-obligation 8: rollback/historical (v1) never mixes with active (v2)
        self.assertTrue(
            all(
                manifest["schemaVersion"] == "dps.module/v1"
                and "resolver" in manifest["agents"]
                for manifest in self.baseline_manifests.values()
            )
        )
        self.assertTrue(
            all(
                manifest["schemaVersion"] == "dps.module/v2"
                and "resolver" not in manifest.get("agents", {})
                for manifest in self.live_manifests.values()
            )
        )


if __name__ == "__main__":
    unittest.main()
