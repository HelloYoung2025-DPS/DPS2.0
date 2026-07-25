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
EXPECTED_BASELINE_MODULE_COUNT = 34
EXPECTED_LIVE_MODULE_COUNT = 23
RETIRED_FACTORY_MODULES = {
    "factory-artifact-builder",
    "factory-control-plane-host",
    "factory-evidence-ledger",
    "factory-impact-analyzer",
    "factory-instruction-resolver",
    "factory-merge-controller",
    "factory-release-controller",
    "factory-rollback-controller",
    "factory-trusted-runner",
    "factory-upgrade-intake",
    "factory-worktree-manager",
}

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

    # Imported, never redefined: the corpus commit must come from a trust-anchored
    # file so re-pointing it cannot be a quiet edit to this test.
    from run_phase0_gate import R0B_FROZEN_BASELINE_COMMIT as FROZEN_BASELINE_COMMIT  # noqa: E402
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
#
# The external authority is the runner-injected ``DPS_BASELINE_COMMIT`` -- the
# channel static-ci.yml already supplies from GitHub's own view of the base, and
# run_phase0_gate.resolve_baseline already reads.  But that value is a *moving*
# pointer: it is the current base branch tip, not this batch's frozen corpus
# commit, and it legitimately changes every time the base advances.
#
# Two separate things are therefore required of the baseline, and neither alone
# is enough:
#
# * WHICH commit is fixed by ``run_phase0_gate.R0B_FROZEN_BASELINE_COMMIT``.  It is
#   named there, not here, because that file is in CANDIDATE_TRUST_PATHS: its bytes
#   are bound into the candidate trust anchor, so moving the corpus to a different
#   commit invalidates that anchor rather than passing as a quiet data edit.
#   Leaving the choice in candidate-writable code would bind nothing in particular,
#   since ancestry alone is satisfied by *any* older commit -- a rewritten corpus
#   could re-point itself at whichever ancestor made the dual-run easiest to pass.
# * WHETHER that commit is real history is decided outside the candidate tree, by
#   requiring the runner-injected ``DPS_BASELINE_COMMIT`` to descend from it.  A
#   commit the candidate planted on its own branch is an ancestor of HEAD but
#   never of the base the runner names, so the self-anchoring rewrite this check
#   exists to stop still fails closed.
#
# Ancestry rather than equality on that second point is deliberate.
# ``DPS_BASELINE_COMMIT`` is a *moving* pointer -- the current base tip, and on
# push events ``github.event.before`` -- so requiring equality would make these
# anchors pass only while the base happened to sit on the frozen commit and go red
# on the next unrelated merge. A gate that expires is not a gate.
#
# Honest limit: when the caller supplies the baseline itself (a local run, or
# scripts/release.sh, which passes --base HEAD), "descends from the frozen commit"
# is checked against a value the candidate controls.  Local runs were never the
# authority; CI is, and there the base sha comes from GitHub's own view of the PR.
BASELINE_COMMIT_ENV = "DPS_BASELINE_COMMIT"
_FULL_COMMIT_SHA = re.compile(r"\A[0-9a-f]{40}\Z")


def _run_trusted_git(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [_trusted_git_executable(), *args],
        cwd=str(ROOT),
        # No trusted-git call here takes input; DEVNULL makes that explicit and
        # keeps ``hash-object --stdin`` (which wants the EMPTY tree) from waiting
        # forever on an inherited stdin that some runners keep open.
        stdin=subprocess.DEVNULL,
        capture_output=True,
        check=False,
    )


def _assert_well_formed_commit(raw: Any, label: str) -> str:
    if raw is None:
        raise AssertionError(
            "{0} is not set; the baseline commit has no external authority and the "
            "dual-run anchors cannot be trusted -- the runner must inject it".format(label)
        )
    value = str(raw).strip()
    if not _FULL_COMMIT_SHA.match(value):
        raise AssertionError(
            "{0}={1!r} is not a full 40-hex commit id; revisions such as HEAD or "
            "refs are candidate-influenceable and are refused".format(label, raw)
        )
    return value


def _assert_commit_exists(commit: str, label: str) -> None:
    resolved = _run_trusted_git("rev-parse", "--verify", "--quiet", commit + "^{commit}")
    if resolved.returncode != 0:
        raise AssertionError("{0} {1} does not exist in this repository".format(label, commit))


def _assert_is_ancestor(ancestor: str, descendant: str, explanation: str) -> None:
    result = _run_trusted_git("merge-base", "--is-ancestor", ancestor, descendant)
    if result.returncode != 0:
        raise AssertionError(
            "{0} is not an ancestor of {1}: {2}".format(ancestor, descendant, explanation)
        )


def _require_injected_baseline_commit() -> str:
    """The frozen corpus commit, trusted because the runner's base descends from it."""

    injected = _assert_well_formed_commit(os.environ.get(BASELINE_COMMIT_ENV), BASELINE_COMMIT_ENV)
    _assert_commit_exists(injected, "injected baseline")

    frozen = _assert_well_formed_commit(FROZEN_BASELINE_COMMIT, "FROZEN_BASELINE_COMMIT")
    _assert_commit_exists(frozen, "frozen baseline")

    declared = _load_json(FIXTURES / "provenance.json").get("baseline_commit")
    if declared != frozen:
        raise AssertionError(
            "provenance.json declares baseline_commit {0!r}, but this batch is frozen "
            "at {1!r}; the corpus cannot re-point itself at a different commit".format(
                declared, frozen
            )
        )
    _assert_is_ancestor(
        frozen,
        injected,
        "the runner-supplied base does not descend from the frozen baseline, so "
        "nothing outside the candidate tree vouches for it being real pre-migration "
        "history",
    )
    _assert_is_ancestor(
        frozen,
        "HEAD",
        "the frozen baseline is not in this branch's own history",
    )
    return frozen


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
        self.assertEqual(
            self.provenance["module_count"], EXPECTED_BASELINE_MODULE_COUNT
        )
        self.assertRegex(self.provenance["baseline_commit"], r"^[0-9a-f]{40}$")
        # One schema plus one manifest per registered module.
        self.assertEqual(
            len(self.provenance["files"]), EXPECTED_BASELINE_MODULE_COUNT + 1
        )

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

    def test_frozen_baseline_covers_live_and_exactly_the_retired_modules(self) -> None:
        frozen = {_module_id_of_baseline(path) for path in _baseline_manifest_paths()}
        live = {path.parent.name for path in _current_manifest_paths()}
        self.assertEqual(EXPECTED_BASELINE_MODULE_COUNT, len(frozen))
        self.assertEqual(EXPECTED_LIVE_MODULE_COUNT, len(live))
        self.assertEqual(RETIRED_FACTORY_MODULES, frozen.difference(live))
        self.assertEqual(set(), live.difference(frozen))


class SchemaRuleChangeTest(unittest.TestCase):
    """Pin the exact rule delta so the dual-run result cannot be explained away."""

    def setUp(self) -> None:
        self.old = _load_json(BASELINE / "module-manifest.schema.json")
        self.new = _load_json(LIVE_SCHEMA_PATH)

    def test_each_major_keeps_its_own_schema_identity(self) -> None:
        # The URI that named the pre-migration shape must keep naming it.  Letting
        # the incompatible v2 inherit it would mean one identity resolving to two
        # incompatible schemas -- the frozen v1 fixture still carries that URI --
        # so a rollback consumer resolving historical evidence by identity would
        # judge it against the wrong major.  Same rule as the dps.module major bump
        # itself (RebuildPlan 158): a breaking shape takes a new identity.
        frozen_v1_id = _load_json(BASELINE / "module-manifest.schema.json")["$id"]
        self.assertEqual(frozen_v1_id, _load_json(LIVE_V1_SCHEMA_PATH)["$id"])
        self.assertNotEqual(frozen_v1_id, _load_json(LIVE_V2_SCHEMA_PATH)["$id"])
        self.assertEqual(
            "https://dps.local/schemas/module-manifest.v2.schema.json",
            _load_json(LIVE_V2_SCHEMA_PATH)["$id"],
        )

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
                receipt_required = agents["properties"]["receiptRequired"]
                self.assertIs(True, receipt_required["const"])
                self.assertIn("receiptRequired", agents["required"])
                # The two majors deliberately differ here.  v1 is frozen at its
                # published bytes, so it keeps the bare const.  v2 additionally binds
                # ``type`` because const alone is compared with Python equality, under
                # which 1 == True (see test_v2_boolean_consts_refuse_numeric_impostors).
                expected = (
                    {"const": True}
                    if label == "old"
                    else {"type": "boolean", "const": True}
                )
                self.assertEqual(expected, receipt_required)


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
        self.assertEqual(
            EXPECTED_BASELINE_MODULE_COUNT, len(self.old_manifests)
        )
        self.assertEqual({}, rejected, "old schema must accept the pre-migration corpus")

    def test_new_schema_accepts_every_current_manifest(self) -> None:
        rejected = {
            module_id: validate_json_schema(manifest, self.new_schema)
            for module_id, manifest in sorted(self.new_manifests.items())
            if validate_json_schema(manifest, self.new_schema)
        }
        self.assertEqual(EXPECTED_LIVE_MODULE_COUNT, len(self.new_manifests))
        self.assertEqual({}, rejected, "new schema must accept the migrated corpus")

    def test_old_schema_rejects_all_current_manifests(self) -> None:
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
        self.assertEqual(EXPECTED_LIVE_MODULE_COUNT, len(self.new_manifests))
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
        self.assertEqual(
            EXPECTED_BASELINE_MODULE_COUNT, len(self.old_manifests)
        )
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
      The declaration is trusted only because the runner-injected
      ``DPS_BASELINE_COMMIT`` names a base whose history already contains it, so a
      commit planted on this branch cannot serve as the anchor
      (see ``_require_injected_baseline_commit``);
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

    def _parent_commit(self, commit: str) -> str:
        resolved = _run_trusted_git("rev-parse", "--verify", commit + "^{commit}^")
        self.assertEqual(0, resolved.returncode, resolved.stderr.decode("utf-8", "replace"))
        return resolved.stdout.decode("ascii").strip()

    def _child_of(self, ancestor: str) -> str:
        """A commit on HEAD's line that is strictly newer than ``ancestor``."""

        listed = _run_trusted_git("rev-list", "--ancestry-path", "--reverse", ancestor + "..HEAD")
        self.assertEqual(0, listed.returncode, listed.stderr.decode("utf-8", "replace"))
        commits = listed.stdout.decode("ascii").split()
        self.assertTrue(commits, "HEAD must have at least one commit past the frozen baseline")
        return commits[0]

    def _unreferenced_commit(self) -> str:
        """A real commit object that is not on HEAD's ancestry."""

        primary_raw = os.environ.get("GIT_OBJECT_DIRECTORY")
        alternate_raw = os.environ.get("GIT_ALTERNATE_OBJECT_DIRECTORIES")
        self.assertIsNotNone(primary_raw)
        self.assertIsNotNone(alternate_raw)
        primary = Path(str(primary_raw))
        alternate = Path(str(alternate_raw))
        self.assertTrue(primary.is_absolute())
        self.assertTrue(alternate.is_absolute())
        self.assertFalse(primary.is_symlink())
        self.assertFalse(alternate.is_symlink())
        self.assertEqual(primary, primary.resolve(strict=True))
        self.assertEqual(alternate, alternate.resolve(strict=True))
        self.assertEqual(os.getuid(), primary.stat().st_uid)
        self.assertEqual(0o700, primary.stat().st_mode & 0o777)
        with self.assertRaises(ValueError):
            primary.relative_to(ROOT)

        tree = _run_trusted_git("hash-object", "-t", "tree", "-w", "--stdin")
        self.assertEqual(0, tree.returncode, tree.stderr.decode("utf-8", "replace"))
        created = subprocess.run(
            [
                _trusted_git_executable(),
                "commit-tree",
                tree.stdout.decode("ascii").strip(),
                "-m",
                "r0b baseline-authority isolated-object probe",
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
        commit = created.stdout.decode("ascii").strip()
        self.assertRegex(commit, r"^[0-9a-f]{40}$")
        private_object = primary / commit[:2] / commit[2:]
        alternate_object = alternate / commit[:2] / commit[2:]
        self.assertTrue(private_object.is_file())
        self.assertFalse(private_object.is_symlink())
        self.assertFalse(alternate_object.exists())
        return commit

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

    def test_a_baseline_that_does_not_descend_from_the_corpus_fails_closed(self) -> None:
        # The whole point of the relation: a base whose history does not contain the
        # frozen commit cannot vouch for it.  A commit off HEAD's line is the
        # clearest case -- this is also the shape of the planted-ancestor attack,
        # where the candidate declares a commit that exists only on its own branch
        # and is therefore absent from the base the runner names.
        clean_environment = dict(os.environ)
        for name in (
            "GIT_DIR",
            "GIT_WORK_TREE",
            "GIT_COMMON_DIR",
            "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        ):
            clean_environment.pop(name, None)
        with mock.patch.dict(os.environ, clean_environment, clear=True):
            head_before = self._head_commit()
            status_before = _run_trusted_git(
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
            )
            self.assertEqual(
                0,
                status_before.returncode,
                status_before.stderr.decode("utf-8", "replace"),
            )
            refs_before = _run_trusted_git(
                "for-each-ref",
                "--format=%(refname)%00%(objectname)",
            )
            self.assertEqual(
                0,
                refs_before.returncode,
                refs_before.stderr.decode("utf-8", "replace"),
            )
            top_level = _run_trusted_git(
                "rev-parse",
                "--path-format=absolute",
                "--show-toplevel",
            )
            self.assertEqual(
                0,
                top_level.returncode,
                top_level.stderr.decode("utf-8", "replace"),
            )
            self.assertEqual(ROOT, Path(top_level.stdout.decode("utf-8").strip()))
            common = _run_trusted_git(
                "rev-parse",
                "--path-format=absolute",
                "--git-common-dir",
            )
            self.assertEqual(
                0,
                common.returncode,
                common.stderr.decode("utf-8", "replace"),
            )
            common_directory = Path(
                common.stdout.decode("utf-8").strip()
            )
            self.assertTrue(common_directory.is_absolute())
            self.assertFalse(common_directory.is_symlink())
            self.assertEqual(
                common_directory,
                common_directory.resolve(strict=True),
            )
            alternate_objects = common_directory / "objects"
            self.assertTrue(alternate_objects.is_dir())
            self.assertFalse(alternate_objects.is_symlink())
            self.assertEqual(
                alternate_objects,
                alternate_objects.resolve(strict=True),
            )
            self.assertTrue(os.access(str(alternate_objects), os.R_OK | os.X_OK))
            if alternate_objects.stat().st_uid != os.getuid():
                self.assertFalse(os.access(str(alternate_objects), os.W_OK))

            temporary_parent = Path(tempfile.gettempdir()).resolve(strict=True)
            self.assertTrue(temporary_parent.is_dir())
            self.assertFalse(temporary_parent.is_symlink())
            with tempfile.TemporaryDirectory(
                prefix="dps-r0b-git-objects-",
                dir=str(temporary_parent),
            ) as temporary:
                private_state = Path(temporary)
                self.assertEqual(os.getuid(), private_state.stat().st_uid)
                self.assertEqual(0o700, private_state.stat().st_mode & 0o777)
                with self.assertRaises(ValueError):
                    private_state.relative_to(ROOT)
                private_objects = private_state / "objects"
                private_objects.mkdir(mode=0o700)
                self.assertEqual(os.getuid(), private_objects.stat().st_uid)
                self.assertEqual(0o700, private_objects.stat().st_mode & 0o777)
                isolated_environment = dict(clean_environment)
                isolated_environment.update(
                    {
                        "GIT_OBJECT_DIRECTORY": str(private_objects),
                        "GIT_ALTERNATE_OBJECT_DIRECTORIES": str(
                            alternate_objects
                        ),
                    }
                )
                with mock.patch.dict(
                    os.environ,
                    isolated_environment,
                    clear=True,
                ):
                    stranger = self._unreferenced_commit()
                    visible = _run_trusted_git(
                        "cat-file",
                        "-e",
                        stranger + "^{commit}",
                    )
                    self.assertEqual(
                        0,
                        visible.returncode,
                        visible.stderr.decode("utf-8", "replace"),
                    )
                    with self._with_injection(stranger):
                        with self.assertRaises(AssertionError) as raised:
                            _require_injected_baseline_commit()
                private_state_path = private_state
            self.assertFalse(private_state_path.exists())
            canonical_visibility = _run_trusted_git(
                "cat-file",
                "-e",
                stranger + "^{commit}",
            )
            self.assertNotEqual(0, canonical_visibility.returncode)
            self.assertEqual(head_before, self._head_commit())
            status_after = _run_trusted_git(
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
            )
            self.assertEqual(
                0,
                status_after.returncode,
                status_after.stderr.decode("utf-8", "replace"),
            )
            self.assertEqual(status_before.stdout, status_after.stdout)
            refs_after = _run_trusted_git(
                "for-each-ref",
                "--format=%(refname)%00%(objectname)",
            )
            self.assertEqual(
                0,
                refs_after.returncode,
                refs_after.stderr.decode("utf-8", "replace"),
            )
            self.assertEqual(refs_before.stdout, refs_after.stdout)
        self.assertIn("does not descend from the frozen baseline", str(raised.exception))

    def test_a_baseline_older_than_the_corpus_fails_closed(self) -> None:
        # An ancestor of the frozen commit is a real commit and an ancestor of HEAD,
        # so only the direction of the ancestry check rejects it.
        older = self._parent_commit(FROZEN_BASELINE_COMMIT)
        with self._with_injection(older):
            with self.assertRaises(AssertionError) as raised:
                _require_injected_baseline_commit()
        self.assertIn("does not descend from the frozen baseline", str(raised.exception))

    def test_provenance_repointed_at_another_ancestor_fails_closed(self) -> None:
        # The ancestry relation alone is satisfied by *any* older commit.  Without
        # the frozen constant, a rewritten corpus could re-point itself at whichever
        # genuine ancestor made the dual-run easiest to pass, and every check would
        # still be green.  This is the case that binds it to one commit.
        older = self._parent_commit(FROZEN_BASELINE_COMMIT)
        with self._with_injection(self._head_commit()):
            with mock.patch(
                __name__ + "._load_json",
                return_value={"baseline_commit": older},
            ):
                with self.assertRaises(AssertionError) as raised:
                    _require_injected_baseline_commit()
        self.assertIn("this batch is frozen at", str(raised.exception))

    def test_the_frozen_constant_is_what_the_corpus_declares(self) -> None:
        self.assertEqual(
            FROZEN_BASELINE_COMMIT,
            _load_json(FIXTURES / "provenance.json")["baseline_commit"],
        )

    def test_every_new_trust_bearing_artifact_is_byte_bound(self) -> None:
        # The whole protection is that changing which commit the corpus anchors to
        # invalidates the candidate trust anchor.  That holds only while the constant
        # is defined in a CANDIDATE_TRUST_PATHS file and this module merely imports
        # it; a local redefinition here would silently return the choice to
        # candidate-writable code, where any older ancestor would do.
        runner = ROOT / "Tools" / "ci" / "run_phase0_gate.py"
        gate_source = (ROOT / "Tools" / "ci" / "run_candidate_gate.py").read_text(encoding="utf-8")
        # Anchoring the constant is only part of it.  Everything this batch made
        # trust-bearing has to be byte-bound, or the guarantee can be removed
        # without the anchor noticing: the suites that enforce it (the runner only
        # checks that a registered test *name* appears in unittest output, so
        # emptied bodies still read as green), the F9 code that actually performs
        # the per-major validation, the two manifest schemas it validates against,
        # and the envelope schemas that decide which shape is even accepted.
        # Tests/ci/test_candidate_gate.py and its neighbours are already listed for
        # exactly this reason.
        anchored = (
            '"Tools/ci/run_phase0_gate.py"',
            # SUPPORTED_MANIFEST_SCHEMA_FILES lives here: it is now the registry
            # that decides which majors exist at all, so editing it must break the
            # anchor the same way editing a schema does.
            '"Tools/ci/phase0.py"',
            '"Tests/ci/test_r0b_receipt_migration_dual_run.py"',
            '"Tests/ci/test_manifest_schema_subset_evaluator.py"',
            '"Tools/verification/external_gate.py"',
            '"Tools/verification/tests/test_external_gate.py"',
            '"governance/schemas/module-manifest.schema.json"',
            '"governance/schemas/module-manifest.v1.schema.json"',
            '"governance/verification/f9-scale-input.v1.schema.json"',
            '"governance/verification/f9-scale-input.v2.schema.json"',
        )
        for path in anchored:
            self.assertIn(
                path,
                gate_source,
                "run_candidate_gate must keep {0} byte-bound, or the frozen "
                "baseline stops being enforced".format(path),
            )

        module_source = Path(__file__).read_text(encoding="utf-8")
        self.assertNotIn(
            "\nFROZEN_BASELINE_COMMIT =",
            module_source,
            "this module must import the frozen baseline, never define its own",
        )
        self.assertIn("R0B_FROZEN_BASELINE_COMMIT", runner.read_text(encoding="utf-8"))
        self.assertNotIn(
            FROZEN_BASELINE_COMMIT,
            module_source,
            "the commit id must not be duplicated outside the trust-anchored file",
        )

    def test_the_frozen_commit_is_accepted_and_returned(self) -> None:
        with self._with_injection(FROZEN_BASELINE_COMMIT):
            self.assertEqual(FROZEN_BASELINE_COMMIT, _require_injected_baseline_commit())

    def test_a_base_that_has_advanced_past_the_corpus_is_still_accepted(self) -> None:
        # The regression this relation exists to prevent: DPS_BASELINE_COMMIT is the
        # *current* base tip, so it moves whenever the base branch advances (and on
        # push events it is github.event.before).  Under an equality check the
        # anchors would go red on the next unrelated merge.  Any descendant of the
        # frozen commit must keep them green -- HEAD itself is one, which is also
        # why scripts/release.sh (--base HEAD) no longer breaks them.
        for descendant in (self._head_commit(), self._child_of(FROZEN_BASELINE_COMMIT)):
            with self.subTest(baseline=descendant):
                self.assertNotEqual(FROZEN_BASELINE_COMMIT, descendant)
                with self._with_injection(descendant):
                    self.assertEqual(FROZEN_BASELINE_COMMIT, _require_injected_baseline_commit())


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
        fixture_parent = ROOT / "Reports" / "ci"
        for directory in (
            ROOT,
            ROOT / "Reports",
            fixture_parent,
        ):
            self.assertTrue(directory.is_dir())
            self.assertFalse(directory.is_symlink())
            self.assertEqual(directory, directory.resolve(strict=True))
        parent_stat = fixture_parent.stat()
        self.assertEqual(os.getuid(), parent_stat.st_uid)
        self.assertEqual(0, parent_stat.st_mode & 0o022)
        self.assertTrue(
            os.access(
                str(fixture_parent),
                os.R_OK | os.W_OK | os.X_OK,
            )
        )
        probe = "Reports/ci/r0b-repo-local-git-fixture/probe"
        ignored = _run_trusted_git(
            "check-ignore",
            "--quiet",
            "--no-index",
            "--",
            probe,
        )
        self.assertEqual(0, ignored.returncode)
        tracked = _run_trusted_git(
            "ls-files",
            "--error-unmatch",
            "--",
            probe,
        )
        self.assertEqual(1, tracked.returncode)
        with tempfile.TemporaryDirectory(
            prefix="r0b-repo-local-git-",
            dir=str(fixture_parent),
        ) as poisoned_dir:
            poisoned_path = Path(poisoned_dir)
            self.assertFalse(poisoned_path.is_symlink())
            self.assertEqual(poisoned_path, poisoned_path.resolve(strict=True))
            self.assertEqual(os.getuid(), poisoned_path.stat().st_uid)
            self.assertEqual(0o700, poisoned_path.stat().st_mode & 0o777)
            self.assertEqual(fixture_parent, poisoned_path.parent)
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

    def test_v2_boolean_consts_refuse_numeric_impostors(self) -> None:
        # phase0's validator compares const with Python ``!=`` (Tools/ci/phase0.py:665),
        # and in Python ``1 == True``.  So {"const": true} ALONE accepted
        # receiptRequired: 1 and 1.0 -- a manifest could declare receipts required
        # with a value that is not a boolean at all, and the gate read it as true.
        # The fix is declared in the v2 schema rather than in the shared validator:
        # ``type`` is checked by _schema_type_matches (phase0.py:608), which does
        # ``isinstance(value, bool)`` and therefore refuses ints outright.
        # This is exercised against the LIVE v2 schema, so deleting the type
        # declaration turns this red.
        manifest = self._a_live_manifest()
        self.assertEqual("dps.module/v2", manifest["schemaVersion"])
        schema = self.dispatch["dps.module/v2"]
        self.assertEqual([], phase0.validate_json_schema(manifest, schema, schema, "live"))

        for impostor in (1, 1.0, 0, 0.0, "true"):
            with self.subTest(receiptRequired=impostor):
                candidate = copy.deepcopy(manifest)
                candidate["agents"]["receiptRequired"] = impostor
                self.assertNotEqual(
                    [],
                    phase0.validate_json_schema(candidate, schema, schema, "live"),
                    "a non-boolean receiptRequired was accepted as true",
                )
        # The honest value still passes, so the guard is not just refusing everything.
        honest = copy.deepcopy(manifest)
        honest["agents"]["receiptRequired"] = True
        self.assertEqual([], phase0.validate_json_schema(honest, schema, schema, "live"))

    def test_v2_release_eligibility_const_is_type_bound(self) -> None:
        # Companion to the above for the proposed-lifecycle branch.  Unlike
        # receiptRequired this one was NOT independently exploitable -- the base
        # property declaration at module.properties.releaseEligible already says
        # {"type": "boolean"} -- but the const in the allOf/then branch relied on
        # that neighbour for its safety.  Binding the type where the const lives
        # makes the branch self-sufficient.
        branch = self.live_v2["allOf"][0]["then"]["properties"]["module"]["properties"]["releaseEligible"]
        self.assertEqual({"type": "boolean", "const": False}, branch)

        schema = self.dispatch["dps.module/v2"]
        manifest = self._a_live_manifest()
        manifest["module"]["lifecycle"] = "proposed"
        for impostor in (0, 0.0, 1, True):
            with self.subTest(releaseEligible=impostor):
                candidate = copy.deepcopy(manifest)
                candidate["module"]["releaseEligible"] = impostor
                self.assertNotEqual(
                    [], phase0.validate_json_schema(candidate, schema, schema, "live")
                )

    def test_live_schemas_pin_two_distinct_majors(self) -> None:
        # major-map code-pin (proof-obligation 9): reverting either major fails here.
        self.assertEqual("dps.module/v1", self.live_v1["properties"]["schemaVersion"]["const"])
        self.assertEqual("dps.module/v2", self.live_v2["properties"]["schemaVersion"]["const"])
        self.assertIn("resolver", self.live_v1["properties"]["agents"]["properties"])
        self.assertNotIn("resolver", self.live_v2["properties"]["agents"]["properties"])
        self.assertEqual({"dps.module/v1", "dps.module/v2"}, set(self.dispatch))

    def test_a_planted_schema_cannot_introduce_a_major_of_its_own(self) -> None:
        # Discovering majors by globbing module-manifest*.schema.json and believing
        # each file's schemaVersion.const made the supported set candidate-defined:
        # a permissive module-manifest.v999.schema.json plus manifests switched to
        # dps.module/v999 validated clean with the entire agents block removed.  The
        # registry in phase0 -- a trust-anchored file -- decides which majors exist,
        # and an unregistered schema file is refused rather than skipped.
        self.assertEqual(
            {"dps.module/v1", "dps.module/v2"},
            set(phase0.SUPPORTED_MANIFEST_SCHEMA_FILES),
        )
        with tempfile.TemporaryDirectory() as directory:
            planted_root = Path(directory)
            schemas = planted_root / "governance" / "schemas"
            schemas.mkdir(parents=True)
            for major, name in phase0.SUPPORTED_MANIFEST_SCHEMA_FILES.items():
                source = LIVE_V1_SCHEMA_PATH if major == "dps.module/v1" else LIVE_V2_SCHEMA_PATH
                (schemas / name).write_bytes(source.read_bytes())
            self.assertEqual(
                {"dps.module/v1", "dps.module/v2"},
                set(phase0.load_manifest_schemas(planted_root)),
            )

            (schemas / "module-manifest.v999.schema.json").write_text(
                json.dumps(
                    {
                        "type": "object",
                        "properties": {"schemaVersion": {"const": "dps.module/v999"}},
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaises(Phase0Error) as raised:
                phase0.load_manifest_schemas(planted_root)
            self.assertIn("unregistered module manifest schema", str(raised.exception))

    def test_a_registered_schema_may_not_relabel_its_major(self) -> None:
        # The other half: keeping the registered filename but changing the const
        # would let v2's file answer for a major the registry never approved.
        with tempfile.TemporaryDirectory() as directory:
            planted_root = Path(directory)
            schemas = planted_root / "governance" / "schemas"
            schemas.mkdir(parents=True)
            for major, name in phase0.SUPPORTED_MANIFEST_SCHEMA_FILES.items():
                source = LIVE_V1_SCHEMA_PATH if major == "dps.module/v1" else LIVE_V2_SCHEMA_PATH
                document = _load_json(source)
                if major == "dps.module/v2":
                    document["properties"]["schemaVersion"]["const"] = "dps.module/v999"
                (schemas / name).write_text(json.dumps(document), encoding="utf-8")
            with self.assertRaises(Phase0Error) as raised:
                phase0.load_manifest_schemas(planted_root)
            self.assertIn("declares major", str(raised.exception))

    def test_v1_schema_accepts_the_frozen_v1_manifests(self) -> None:
        # proof-obligation 1
        rejected = {
            module_id: validate_json_schema(manifest, self.live_v1)
            for module_id, manifest in sorted(self.baseline_manifests.items())
            if validate_json_schema(manifest, self.live_v1)
        }
        self.assertEqual(
            EXPECTED_BASELINE_MODULE_COUNT, len(self.baseline_manifests)
        )
        self.assertEqual({}, rejected, "live v1 schema must accept the frozen v1 corpus")

    def test_v2_schema_accepts_the_current_v2_manifests(self) -> None:
        # proof-obligation 2
        self.assertEqual(EXPECTED_LIVE_MODULE_COUNT, len(self.live_manifests))
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
