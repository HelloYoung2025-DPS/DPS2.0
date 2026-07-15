from __future__ import annotations

import sys
import unittest
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = (MODULE_ROOT / "src").resolve(strict=True)
if SOURCE_ROOT.parent != MODULE_ROOT:
    raise RuntimeError("test source root escaped its module")
sys.path.insert(0, str(SOURCE_ROOT))

from factory_control_plane_host import (
    FactoryControlPlaneHost,
    InMemoryWorkflowRepository,
    SimulationReceiptVerifier,
    StaticRuntimeControlAuthority,
    validate_event_stream,
)
from simulation_adapter import (
    CrashAfterProviderSuccessAdapter,
    DeterministicSimulationAdapter,
    SimulationExternalAuthority,
    SimulationRoleDirectory,
)
from provider_verifier_fixture import build_test_provider_verifier


PROVIDER_VERIFIER = build_test_provider_verifier(Path(__file__).resolve().parents[3])


def request(workflow_id="upgrade:factory-host-e2e-0001"):
    return {
        "schema_version": "1.0.0", "contract_id": "factory.workflow.request/v1",
        "producer_module": "factory-control-plane-host", "soul_id": None,
        "device_binding_id": None, "platform_account_id": None,
        "trace_id": "trace_" + "5" * 32, "idempotency_key": "idem_" + "6" * 64,
        "occurred_at": "2026-07-14T00:00:00Z", "privacy_class": "internal",
        "workflow_id": workflow_id, "mode": "SIMULATION", "risk_tier": "R1",
        "baseline_commit": "c" * 40, "target_modules": ["factory-control-plane-host"],
        "requested_paths": ["Modules/factory-control-plane-host/src/factory_control_plane_host.py"],
        "public_contract_changes": [], "external_context_ref": None,
    }


def host(repository, adapter):
    return FactoryControlPlaneHost(
        repository, SimulationRoleDirectory(), adapter,
        SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
        StaticRuntimeControlAuthority(),
    )


class EndToEndSimulationTests(unittest.TestCase):
    def test_complete_workflow_and_rollback_cover_all_ten_modules(self):
        repository = InMemoryWorkflowRepository()
        adapter = DeterministicSimulationAdapter()
        service = host(repository, adapter)
        workflow_id = service.start(request())["workflow_id"]
        completed = service.run_until_blocked(workflow_id, "worker-a", maximum_steps=200)
        self.assertEqual("COMPLETED", completed["state"])
        self.assertTrue(completed["simulation_only"])
        self.assertFalse(completed["production_authorized"])
        self.assertEqual("INTEGRATION_VERIFIED", completed["verification_ceiling"])
        self.assertTrue(all(call["mode"] == "SIMULATION" for call in adapter.calls))
        outputs = [
            output["payload"]
            for receipt in repository.receipts(workflow_id)
            for output in receipt["outputs"]
        ]
        tested = [
            item["tested_commit"] for item in outputs
            if item["contract_id"] == "trusted.test.result/v1"
        ]
        merge = next(
            item for item in outputs if item["contract_id"] == "merge.decision/v1"
        )
        artifact = next(
            item for item in outputs if item["contract_id"] == "artifact.descriptor/v1"
        )
        self.assertTrue(tested)
        self.assertEqual(1, len(set(tested)))
        self.assertNotEqual(request()["baseline_commit"], tested[0])
        self.assertEqual(tested[0], merge["integration_commit"])
        self.assertEqual(merge["integration_commit"], artifact["integration_commit"])

        rolled_back = service.request_rollback(workflow_id, "worker-b", "SIMULATED_ROLLBACK_DRILL")
        self.assertEqual("ROLLED_BACK", rolled_back["state"])
        self.assertEqual(
            {
                "factory-upgrade-intake", "factory-instruction-resolver", "factory-impact-analyzer",
                "factory-worktree-manager", "factory-trusted-runner", "factory-merge-controller",
                "factory-artifact-builder", "factory-evidence-ledger", "factory-release-controller",
                "factory-rollback-controller",
            },
            {call["target_module"] for call in adapter.calls},
        )
        validate_event_stream(repository.events(workflow_id))
        self.assertEqual(0, sum(receipt["side_effect_count"] for receipt in repository.receipts(workflow_id)))

    def test_crash_after_provider_success_reuses_stable_request_and_recovers(self):
        repository = InMemoryWorkflowRepository()
        deterministic = DeterministicSimulationAdapter()
        crashing = CrashAfterProviderSuccessAdapter(deterministic, "freeze-contract-plan")
        first_process = host(repository, crashing)
        workflow_id = first_process.start(request("upgrade:factory-host-crash-0001"))["workflow_id"]
        with self.assertRaisesRegex(RuntimeError, "SIMULATED_PROCESS_CRASH"):
            first_process.run_until_blocked(workflow_id, "worker-before-crash", maximum_steps=80)
        crashed_calls = [call for call in deterministic.calls if call["operation"] == "freeze-contract-plan"]
        self.assertEqual(1, len(crashed_calls))
        stable_request_id = crashed_calls[0]["request_id"]

        second_process = host(repository, deterministic)
        recovered = second_process.run_until_blocked(workflow_id, "worker-after-restart", maximum_steps=200)
        self.assertEqual("COMPLETED", recovered["state"])
        replayed = [call for call in deterministic.calls if call["operation"] == "freeze-contract-plan"]
        self.assertEqual(2, len(replayed))
        self.assertEqual([stable_request_id, stable_request_id], [call["request_id"] for call in replayed])
        self.assertGreater(replayed[1]["fencing_token"], replayed[0]["fencing_token"])
        self.assertEqual(replayed[0]["logical_request_sha256"], replayed[1]["logical_request_sha256"])
        validate_event_stream(repository.events(workflow_id))


if __name__ == "__main__":
    unittest.main()
