#!/usr/bin/env python3
"""Repository-backed invariants for the candidate Contract/Integration policy."""

from __future__ import annotations

import copy
import json
import re
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator


ROOT = Path(__file__).resolve().parents[2]
POLICY_PATH = ROOT / "governance" / "policies" / "candidate-test-policy.yaml"
SCHEMA_PATH = ROOT / "governance" / "schemas" / "candidate-test-policy.schema.json"

EXPECTED_CONTRACT_FLOORS = {
    "audit-metrics.contract": 4,
    "binding.contract": 8,
    "command-orchestrator.contract": 9,
    "command-orchestrator.contract-schema": 4,
    "control-plane-host.contract": 31,
    "control-plane-host.contract-schema": 20,
    "device-registry.contract": 4,
    "edge-local-journal.contract": 3,
    "edge-local-journal.contract-schema": 6,
    "evidence-service.contract": 6,
    "executor-gateway.contract": 15,
    "gbrain-projector.contract": 8,
    "interest-reducer.contract": 10,
    "memory-event-ledger.contract": 13,
    "operation-compiler.contract": 11,
    "persona-store.contract": 8,
    "persona-store.schema-contract": 13,
    "planner.contract": 7,
    "planner.contract-schema": 5,
    "platform-account-registry.contract": 5,
    "platform-authorization-authority.contract": 14,
    "policy-approval.contract": 22,
    "policy-approval.schema-contract": 5,
    "soul-memory-adapter.schema-corpus": 3,
    "soul-memory-adapter.contract": 5,
    "soul-registry.contract": 7,
    "windows-edge-supervisor.contract": 13,
    "windows-edge-supervisor.contract-schema": 7,
    "windows-edge-worker.contract": 9,
    "windows-edge-worker.provider-contracts": 6,
    "zenno-bridge.contract-schema": 5,
    "zenno-bridge.contract-wire": 2,
}

EXPECTED_INTEGRATION_FLOORS = {
    "audit-metrics.integration": 8,
    "binding.integration": 42,
    "command-orchestrator.postgresql18": 21,
    "control-plane-host.integration": 74,
    "control-plane-host.same-instance-integration": 4,
    "device-registry.integration": 15,
    "edge-local-journal.recovery-simulation": 7,
    "evidence-service.integration": 1,
    "executor-gateway.local-process": 8,
    "gbrain-projector.integration": 1,
    "interest-reducer.integration": 1,
    "memory-event-ledger.integration": 9,
    "operation-compiler.crypto-storage-simulation": 10,
    "persona-store.integration": 25,
    "planner.real-local-process": 7,
    "platform-account-registry.integration": 15,
    "policy-approval.integration": 53,
    "soul-memory-adapter.gbrain-loopback-protocol": 23,
    "soul-memory-adapter.postgres-integration": 9,
    "soul-registry.integration": 5,
    "windows-edge-supervisor.ab-simulation": 2,
    "windows-edge-worker.failure-simulation": 45,
    "zenno-bridge.auth-simulation": 4,
}

EXPECTED_INTEGRATION_TRUST = {
    "audit-metrics.integration": ("REAL_POSTGRESQL", frozenset({"DPS_TEST_POSTGRES"})),
    "binding.integration": (
        "REAL_POSTGRESQL",
        frozenset(
            {
                "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE",
                "DPS_TEST_POSTGRES",
            }
        ),
    ),
    "command-orchestrator.postgresql18": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "control-plane-host.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "control-plane-host.same-instance-integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "device-registry.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "edge-local-journal.recovery-simulation": ("SIMULATION", frozenset()),
    "evidence-service.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "executor-gateway.local-process": ("REAL_LOCAL_PROCESS", frozenset()),
    "gbrain-projector.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "interest-reducer.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "memory-event-ledger.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "operation-compiler.crypto-storage-simulation": ("SIMULATION", frozenset()),
    "persona-store.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "planner.real-local-process": ("REAL_LOCAL_PROCESS", frozenset()),
    "platform-account-registry.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "policy-approval.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "soul-memory-adapter.gbrain-loopback-protocol": (
        "SIMULATION",
        frozenset(),
    ),
    "soul-memory-adapter.postgres-integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "soul-registry.integration": (
        "REAL_POSTGRESQL",
        frozenset({"DPS_TEST_POSTGRES"}),
    ),
    "windows-edge-supervisor.ab-simulation": ("SIMULATION", frozenset()),
    "windows-edge-worker.failure-simulation": ("SIMULATION", frozenset()),
    "zenno-bridge.auth-simulation": ("SIMULATION", frozenset()),
}

EXPECTED_INTEGRATION_CATEGORIES = {
    "audit-metrics.integration": "Integration",
    "binding.integration": "Integration",
    "command-orchestrator.postgresql18": "Integration",
    "control-plane-host.integration": "Integration",
    "control-plane-host.same-instance-integration": "Integration",
    "device-registry.integration": "Integration",
    "edge-local-journal.recovery-simulation": "Integration",
    "evidence-service.integration": "Integration",
    "executor-gateway.local-process": "Integration",
    "gbrain-projector.integration": "Integration",
    "interest-reducer.integration": "Integration",
    "memory-event-ledger.integration": "Integration",
    "operation-compiler.crypto-storage-simulation": "Integration",
    "persona-store.integration": "Integration",
    "planner.real-local-process": "Integration",
    "platform-account-registry.integration": "Integration",
    "policy-approval.integration": "Integration",
    "soul-memory-adapter.gbrain-loopback-protocol": "SecuritySimulation",
    "soul-memory-adapter.postgres-integration": "Integration",
    "soul-registry.integration": "Integration",
    "windows-edge-supervisor.ab-simulation": "Integration",
    "windows-edge-worker.failure-simulation": "Integration",
    "zenno-bridge.auth-simulation": "SecuritySimulation",
}


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def required_suites(test_type: str, evidence_level: str) -> dict[tuple[str, str], dict]:
    suites: dict[tuple[str, str], dict] = {}
    for manifest_path in sorted((ROOT / "Modules").glob("*/module.yaml")):
        manifest = load_json(manifest_path)
        module_id = manifest["module"]["id"]
        for suite in manifest["tests"]["suites"]:
            if (
                suite.get("required") is True
                and str(suite.get("type", "")).lower() == test_type.lower()
                and suite.get("evidenceLevel") == evidence_level
            ):
                key = (module_id, suite["id"])
                if key in suites:
                    raise AssertionError(f"duplicate manifest suite: {key}")
                suites[key] = suite
    return suites


def policy_suites(entries: list[dict]) -> dict[tuple[str, str], dict]:
    suites: dict[tuple[str, str], dict] = {}
    for entry in entries:
        key = (entry["moduleId"], entry["suiteId"])
        if key in suites:
            raise AssertionError(f"duplicate policy suite: {key}")
        suites[key] = entry
    return suites


def manifest_suites() -> dict[tuple[str, str], dict]:
    suites: dict[tuple[str, str], dict] = {}
    for manifest_path in sorted((ROOT / "Modules").glob("*/module.yaml")):
        manifest = load_json(manifest_path)
        module_id = manifest["module"]["id"]
        for suite in manifest["tests"]["suites"]:
            key = (module_id, suite["id"])
            if key in suites:
                raise AssertionError(f"duplicate manifest suite: {key}")
            suites[key] = suite
    return suites


def explicit_dotnet_floor(suite: dict) -> int | None:
    command = suite.get("command")
    if not isinstance(command, str):
        return None
    matches = re.findall(
        r"(?:^|\s)--minimum-expected-tests\s+([1-9][0-9]*)(?=\s|$)",
        command,
    )
    if not matches:
        return None
    if len(matches) != 1:
        raise AssertionError(
            f"suite command must declare one exact test floor: {suite.get('id')}"
        )
    return int(matches[0])


class CandidateTestPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.policy = load_json(POLICY_PATH)
        cls.schema = load_json(SCHEMA_PATH)

    def test_policy_validates_against_draft_2020_12_schema(self):
        Draft202012Validator.check_schema(self.schema)
        Draft202012Validator(self.schema).validate(self.policy)

    def test_schema_rejects_old_python_only_shape_and_missing_floors(self):
        legacy = copy.deepcopy(self.policy)
        legacy["pythonContractSuites"] = legacy.pop("contractSuites")
        self.assertTrue(list(Draft202012Validator(self.schema).iter_errors(legacy)))

        missing_floor = copy.deepcopy(self.policy)
        del missing_floor["contractSuites"][0]["minimumExecutedTests"]
        self.assertTrue(list(Draft202012Validator(self.schema).iter_errors(missing_floor)))

        wrong_category = copy.deepcopy(self.policy)
        wrong_category["contractSuites"][0]["testCategory"] = "Integration"
        self.assertTrue(list(Draft202012Validator(self.schema).iter_errors(wrong_category)))

    def test_schema_rejects_evidence_kind_category_and_environment_downgrades(self):
        validator = Draft202012Validator(self.schema)

        postgresql = copy.deepcopy(self.policy)
        postgresql_entry = next(
            entry
            for entry in postgresql["integrationSuites"]
            if entry["evidenceKind"] == "REAL_POSTGRESQL"
        )
        postgresql_entry["testCategory"] = "SecuritySimulation"
        self.assertTrue(list(validator.iter_errors(postgresql)))

        postgresql_without_connection = copy.deepcopy(self.policy)
        postgresql_entry = next(
            entry
            for entry in postgresql_without_connection["integrationSuites"]
            if entry["evidenceKind"] == "REAL_POSTGRESQL"
        )
        postgresql_entry["requiredEnvironment"] = ["DPS_PSQL"]
        self.assertTrue(list(validator.iter_errors(postgresql_without_connection)))

        local_process = copy.deepcopy(self.policy)
        local_entry = next(
            entry
            for entry in local_process["integrationSuites"]
            if entry["evidenceKind"] == "REAL_LOCAL_PROCESS"
        )
        local_entry["requiredEnvironment"] = ["DPS_TEST_POSTGRES"]
        self.assertTrue(list(validator.iter_errors(local_process)))

        simulation = copy.deepcopy(self.policy)
        simulation_entry = next(
            entry
            for entry in simulation["integrationSuites"]
            if entry["evidenceKind"] == "SIMULATION"
        )
        simulation_entry["requiredEnvironment"] = ["DPS_TEST_POSTGRES"]
        self.assertTrue(list(validator.iter_errors(simulation)))

    def test_suite_ids_are_unique_across_contract_and_integration_policies(self):
        contract_ids = {
            entry["suiteId"] for entry in self.policy["contractSuites"]
        }
        integration_ids = {
            entry["suiteId"] for entry in self.policy["integrationSuites"]
        }
        self.assertTrue(contract_ids.isdisjoint(integration_ids))

    def test_policy_evidence_matches_and_explicit_floors_never_weaken_manifests(self):
        manifests = manifest_suites()
        contract_policy = policy_suites(self.policy["contractSuites"])
        integration_policy = policy_suites(self.policy["integrationSuites"])

        expected = (
            (contract_policy, "contract", "CONTRACT_VERIFIED"),
            (integration_policy, "integration", "INTEGRATION_VERIFIED"),
        )
        for policy, suite_type, evidence_level in expected:
            for key, entry in policy.items():
                self.assertIn(key, manifests, key)
                suite = manifests[key]
                self.assertIs(suite.get("required"), True, key)
                self.assertEqual(suite_type, suite.get("type"), key)
                self.assertEqual(evidence_level, suite.get("evidenceLevel"), key)
                manifest_floor = explicit_dotnet_floor(suite)
                if manifest_floor is not None:
                    self.assertGreaterEqual(
                        entry["minimumExecutedTests"],
                        manifest_floor,
                        key,
                    )

        for key, suite in manifests.items():
            if suite.get("type") == "contract" and suite.get(
                "evidenceLevel"
            ) != "CONTRACT_VERIFIED":
                self.assertNotIn(key, contract_policy, key)
            if suite.get("type") == "integration" and suite.get(
                "evidenceLevel"
            ) != "INTEGRATION_VERIFIED":
                self.assertNotIn(key, integration_policy, key)

    def test_contract_policy_is_exactly_the_required_contract_inventory(self):
        manifest = required_suites("contract", "CONTRACT_VERIFIED")
        policy = policy_suites(self.policy["contractSuites"])

        self.assertEqual(set(manifest), set(policy))
        self.assertEqual(
            len(policy),
            len({entry["suiteId"] for entry in policy.values()}),
            "contract suite IDs must be globally unique before applying explicit floors",
        )
        self.assertEqual(
            EXPECTED_CONTRACT_FLOORS,
            {entry["suiteId"]: entry["minimumExecutedTests"] for entry in policy.values()},
        )
        self._assert_targets_are_canonical_and_bound(policy, manifest)
        self.assertEqual(
            {"Contract"},
            {entry["testCategory"] for entry in policy.values()},
        )

    def test_integration_policy_is_exactly_the_required_integration_inventory(self):
        manifest = required_suites("integration", "INTEGRATION_VERIFIED")
        policy = policy_suites(self.policy["integrationSuites"])

        self.assertEqual(set(manifest), set(policy))
        self.assertEqual(
            len(policy),
            len({entry["suiteId"] for entry in policy.values()}),
            "integration suite IDs must be globally unique before applying explicit trust bindings",
        )
        self.assertEqual(
            EXPECTED_INTEGRATION_FLOORS,
            {entry["suiteId"]: entry["minimumExecutedTests"] for entry in policy.values()},
        )
        self.assertEqual(
            EXPECTED_INTEGRATION_TRUST,
            {
                entry["suiteId"]: (
                    entry["evidenceKind"],
                    frozenset(entry["requiredEnvironment"]),
                )
                for entry in policy.values()
            },
        )
        self.assertEqual(
            EXPECTED_INTEGRATION_CATEGORIES,
            {entry["suiteId"]: entry["testCategory"] for entry in policy.values()},
        )
        self._assert_targets_are_canonical_and_bound(policy, manifest)

    def _assert_targets_are_canonical_and_bound(
        self,
        policy: dict[tuple[str, str], dict],
        manifest: dict[tuple[str, str], dict],
    ) -> None:
        target_categories = [
            (entry["testTarget"], entry["testCategory"])
            for entry in policy.values()
        ]
        self.assertEqual(
            len(target_categories),
            len(set(target_categories)),
            "duplicate canonical test target/category",
        )

        for key, entry in policy.items():
            module_id, _ = key
            target = entry["testTarget"]
            expected_prefix = f"Modules/{module_id}/tests/"
            self.assertTrue(target.startswith(expected_prefix), target)
            self.assertTrue(target.endswith((".csproj", ".py")), target)
            self.assertTrue((ROOT / target).is_file(), target)
            self.assertGreaterEqual(entry["minimumExecutedTests"], 1)

            command = manifest[key]["command"]
            if target in command:
                continue

            parent = str(Path(target).parent)
            if parent in command:
                if Path(target).suffix == ".py":
                    self.assertIn(Path(target).name, command)
                    continue
                project_files = sorted((ROOT / parent).glob("*.csproj"))
                self.assertEqual([ROOT / target], project_files, target)
                continue

            tokens = command.split()
            self.assertGreaterEqual(len(tokens), 2, command)
            self.assertEqual("bash", tokens[0], command)
            wrapper = ROOT / tokens[1]
            self.assertTrue(wrapper.is_file(), command)
            self.assertIn(target, wrapper.read_text(encoding="utf-8"), command)


if __name__ == "__main__":
    unittest.main()
