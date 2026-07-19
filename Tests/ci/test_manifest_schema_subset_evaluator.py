#!/usr/bin/env python3
"""Differential proof for external_gate's stdlib module-manifest schema evaluator.

``Tools/verification/external_gate.py`` is stdlib-only by design -- it is the
*external* verifier and must run without the repository's locked dependencies --
so R0-B's per-major manifest dispatch had to hand-roll a JSON Schema subset
evaluator.  Hand-rolled means "trusted on the author's word" unless something
keeps checking it, and a validator that is quietly wrong in the permissive
direction is worse than no validator: it reports green while accepting exactly
the documents F9 exists to reject.

This suite is that check.  It runs the evaluator and a reference Draft 2020-12
implementation over the real 34-module corpus plus a mutation set covering every
keyword the two manifest schemas use, and requires the verdicts to agree case for
case.  It lives under ``Tests/ci`` rather than beside external_gate's own tests
precisely because it needs ``jsonschema``, which external_gate must never import.
"""

from __future__ import annotations

import copy
import json
import sys
import unittest
from pathlib import Path
from typing import Any, Callable, Dict, List, Tuple

import jsonschema
import yaml

ROOT = Path(__file__).resolve().parents[2]
SCHEMAS = ROOT / "governance" / "schemas"

_ORIGINAL_IMPORT_PATH = list(sys.path)
try:
    sys.path.insert(0, str(ROOT / "Tools" / "verification"))
    import external_gate as external_gate_module  # noqa: E402
finally:
    sys.path = _ORIGINAL_IMPORT_PATH

SCHEMA_FILES = {
    "dps.module/v1": "module-manifest.v1.schema.json",
    "dps.module/v2": "module-manifest.schema.json",
}


def _load_schema(schema_version: str) -> Dict[str, Any]:
    return json.loads((SCHEMAS / SCHEMA_FILES[schema_version]).read_text(encoding="utf-8"))


def _live_manifests() -> List[Tuple[str, Dict[str, Any]]]:
    return [
        (path.parent.name, yaml.safe_load(path.read_text(encoding="utf-8")))
        for path in sorted((ROOT / "Modules").glob("*/module.yaml"))
    ]


# One mutation per keyword class the manifest schemas actually use, so agreement
# is demonstrated across the whole implemented vocabulary rather than on the happy
# path alone.  Mutations that do not apply to a given manifest are skipped.
MUTATIONS: List[Tuple[str, Callable[[Dict[str, Any]], Any]]] = [
    ("agents_resolver_reintroduced", lambda m: m["agents"].__setitem__("resolver", "factory-instruction-resolver")),
    ("agents_block_removed", lambda m: m.pop("agents")),
    ("receipt_required_disabled", lambda m: m["agents"].__setitem__("receiptRequired", False)),
    ("module_version_pattern", lambda m: m["module"].__setitem__("version", "not-a-semver")),
    ("unexpected_top_level_property", lambda m: m.__setitem__("surprise", 1)),
    ("risk_tier_enum", lambda m: m["module"].__setitem__("riskTier", "R9")),
    ("owned_paths_min_items", lambda m: m["paths"].__setitem__("owned", [])),
    ("module_major_boolean", lambda m: m["compatibility"].__setitem__("moduleMajor", True)),
    ("suite_type_enum", lambda m: m["tests"]["suites"][0].__setitem__("type", "weird")),
    ("failure_outcomes_min_items", lambda m: m["tests"].__setitem__("failureOutcomes", ["FAIL"])),
    ("policy_id_pattern", lambda m: m["security"]["policyIds"].__setitem__(0, "lower-001")),
    ("provided_contract_mode_if_then", lambda m: m["contracts"]["provided"][0].__setitem__("mode", "compat-read")),
    ("supported_majors_additional_properties", lambda m: m["compatibility"]["supportedContractMajors"].__setitem__("probe.contract", ["1"])),
    ("nullable_rollout_field", lambda m: m["rollout"].__setitem__("canary", None)),
    ("timeout_minimum", lambda m: m["communication"]["outbound"][0].__setitem__("timeoutMs", 0)),
    ("duplicate_dependencies", lambda m: m.__setitem__("dependencies", m["dependencies"] * 2)),
    ("compatibility_const", lambda m: m["compatibility"].__setitem__("unknownMajorBehavior", "allow")),
    ("schema_version_const", lambda m: m.__setitem__("schemaVersion", "dps.module/v2" if m["schemaVersion"] == "dps.module/v1" else "dps.module/v1")),
]


class ManifestSchemaEvaluatorDifferentialTest(unittest.TestCase):
    def _subset_accepts(self, manifest: Dict[str, Any], schema: Dict[str, Any]) -> bool:
        try:
            external_gate_module._validate_manifest_against_schema(manifest, schema, "differential probe")
        except external_gate_module.ExternalGateError:
            return False
        return True

    @staticmethod
    def _reference_accepts(manifest: Dict[str, Any], schema: Dict[str, Any]) -> bool:
        return not list(jsonschema.Draft202012Validator(schema).iter_errors(manifest))

    def test_the_live_corpus_is_accepted_by_both_implementations(self) -> None:
        manifests = _live_manifests()
        self.assertTrue(manifests, "no module manifests were discovered")
        for module_id, manifest in manifests:
            with self.subTest(module=module_id):
                self.assertIn(manifest.get("schemaVersion"), SCHEMA_FILES)
                schema = _load_schema(manifest["schemaVersion"])
                self.assertTrue(self._reference_accepts(manifest, schema))
                self.assertTrue(self._subset_accepts(manifest, schema))

    def test_the_subset_evaluator_agrees_with_draft_2020_12_on_every_mutation(self) -> None:
        manifests = _live_manifests()
        compared = 0
        exercised: set[str] = set()
        for module_id, base in manifests:
            schema = _load_schema(base["schemaVersion"])
            for name, mutate in MUTATIONS:
                mutated = copy.deepcopy(base)
                try:
                    mutate(mutated)
                except (KeyError, IndexError):
                    continue  # this manifest has nothing to mutate for that keyword
                exercised.add(name)
                compared += 1
                with self.subTest(module=module_id, mutation=name):
                    self.assertEqual(
                        self._reference_accepts(mutated, schema),
                        self._subset_accepts(mutated, schema),
                        "the stdlib evaluator disagrees with Draft 2020-12; F9 would "
                        "either reject honest manifests or accept ones the schema forbids",
                    )
        self.assertEqual(
            {name for name, _ in MUTATIONS},
            exercised,
            "every declared mutation must be reachable in the live corpus, otherwise "
            "the vocabulary it stands for is not actually compared",
        )
        self.assertGreater(compared, 100)

    def test_the_mutations_are_real_rejections_and_not_no_ops(self) -> None:
        # A mutation set that quietly stopped changing anything would make the
        # agreement above vacuous, so require the reference implementation itself
        # to reject at least one manifest per mutation.
        manifests = _live_manifests()
        for name, mutate in MUTATIONS:
            with self.subTest(mutation=name):
                rejected = False
                for _module_id, base in manifests:
                    mutated = copy.deepcopy(base)
                    try:
                        mutate(mutated)
                    except (KeyError, IndexError):
                        continue
                    if not self._reference_accepts(mutated, _load_schema(base["schemaVersion"])):
                        rejected = True
                        break
                self.assertTrue(rejected, f"mutation {name} never produced an invalid manifest")


if __name__ == "__main__":
    unittest.main()
