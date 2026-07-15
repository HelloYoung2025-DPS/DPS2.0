from __future__ import annotations

import copy
import hashlib
import json
import sys
import unittest
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = (MODULE_ROOT / "src").resolve(strict=True)
if SOURCE_ROOT.parent != MODULE_ROOT:
    raise RuntimeError("test source root escaped its module")
sys.path.insert(0, str(SOURCE_ROOT))

from jsonschema import Draft202012Validator, FormatChecker, ValidationError

from factory_control_plane_host import (
    FactoryControlPlaneHost,
    InMemoryWorkflowRepository,
    InvalidWorkflowRequest,
    RoleSeparationError,
    SimulationReceiptVerifier,
    StaticRuntimeControlAuthority,
    validate_event_stream,
    validate_role_binding,
    validate_workflow_request,
)
from provider_verifier_fixture import build_test_provider_verifier
from postgres_repository import intake_replay_claim_key_sha256
from simulation_adapter import (
    DeterministicSimulationAdapter,
    SimulationExternalAuthority,
    SimulationRoleDirectory,
)


REPOSITORY_ROOT = MODULE_ROOT.parents[1]
PROVIDER_VERIFIER = build_test_provider_verifier(REPOSITORY_ROOT)
HOST_SCHEMAS = {
    path.name: json.loads(path.read_text(encoding="utf-8"))
    for path in sorted((MODULE_ROOT / "contracts" / "provided").glob("*.schema.json"))
}
PROVIDER_SCHEMAS = {
    "upgrade.intent/v1": "Modules/factory-upgrade-intake/contracts/provided/upgrade.intent.v1.schema.json",
    "instruction.receipt/v1": "Modules/factory-instruction-resolver/contracts/provided/instruction.receipt.v1.schema.json",
    "module.change.plan/v1": "Modules/factory-impact-analyzer/contracts/provided/module.change.plan.v1.schema.json",
    "worktree.plan/v1": "Modules/factory-worktree-manager/contracts/provided/worktree.plan.v1.schema.json",
    "worktree.lease/v1": "Modules/factory-worktree-manager/contracts/provided/worktree.lease.v1.schema.json",
    "trusted.test.result/v1": "Modules/factory-trusted-runner/contracts/provided/trusted.test.result.v1.schema.json",
    "merge.decision/v1": "Modules/factory-merge-controller/contracts/provided/merge.decision.v1.schema.json",
    "artifact.descriptor/v1": "Modules/factory-artifact-builder/contracts/provided/artifact.descriptor.v1.schema.json",
    "upgrade.event/v1": "Modules/factory-evidence-ledger/contracts/provided/upgrade.event.v1.schema.json",
    "rollout.event/v1": "Modules/factory-release-controller/contracts/provided/rollout.event.v1.schema.json",
    "rollback.plan/v1": "Modules/factory-rollback-controller/contracts/provided/rollback.plan.v1.schema.json",
    "rollback.result/v1": "Modules/factory-rollback-controller/contracts/provided/rollback.result.v1.schema.json",
}
ALL_FACTORY_SCHEMAS = {
    str(path.relative_to(REPOSITORY_ROOT)): json.loads(path.read_text(encoding="utf-8"))
    for path in sorted(REPOSITORY_ROOT.glob("Modules/factory-*/contracts/provided/*.schema.json"))
}


def request(workflow_id="upgrade:factory-host-contract-0001"):
    return {
        "schema_version": "1.0.0", "contract_id": "factory.workflow.request/v1",
        "producer_module": "factory-control-plane-host", "soul_id": "soul_" + "0" * 64,
        "device_binding_id": "db_" + "1" * 32, "platform_account_id": "pa_" + "2" * 32,
        "trace_id": "trace_" + "3" * 32, "idempotency_key": "idem_" + "4" * 64,
        "occurred_at": "2026-07-14T00:00:00Z", "privacy_class": "internal",
        "workflow_id": workflow_id, "mode": "SIMULATION", "risk_tier": "R1",
        "baseline_commit": "d" * 40, "target_modules": ["factory-control-plane-host"],
        "requested_paths": ["Modules/factory-control-plane-host/src/factory_control_plane_host.py"],
        "public_contract_changes": [], "external_context_ref": None,
    }


def validator(filename):
    return Draft202012Validator(HOST_SCHEMAS[filename], format_checker=FormatChecker())


def _walk_patterns(node, pointer="$"):
    if isinstance(node, dict):
        pattern = node.get("pattern")
        if isinstance(pattern, str):
            yield pointer + "/pattern", pattern
        for key, value in node.items():
            yield from _walk_patterns(value, pointer + "/" + str(key))
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from _walk_patterns(value, pointer + "/" + str(index))


def _resolve_local_ref(root, node):
    current = node
    seen = set()
    while isinstance(current, dict) and isinstance(current.get("$ref"), str):
        reference = current["$ref"]
        if not reference.startswith("#/") or reference in seen:
            break
        seen.add(reference)
        current = root
        for part in reference[2:].split("/"):
            current = current[part.replace("~1", "/").replace("~0", "~")]
    return current


def _patterned_instance_paths(root, node, instance, path=()):
    resolved = _resolve_local_ref(root, node)
    if not isinstance(resolved, dict):
        return
    pattern = resolved.get("pattern")
    if isinstance(instance, str) and isinstance(pattern, str) and pattern.startswith("^"):
        yield path, pattern
    if isinstance(instance, dict):
        properties = resolved.get("properties")
        if isinstance(properties, dict):
            for key, value in instance.items():
                child = properties.get(key)
                if isinstance(child, dict):
                    yield from _patterned_instance_paths(root, child, value, path + (key,))
    elif isinstance(instance, list) and isinstance(resolved.get("items"), dict):
        for index, value in enumerate(instance):
            yield from _patterned_instance_paths(root, resolved["items"], value, path + (index,))


def _replace_at_path(value, path, replacement):
    cursor = value
    for part in path[:-1]:
        cursor = cursor[part]
    cursor[path[-1]] = replacement


def assert_recursive_terminal_whitespace_rejected(testcase, schema, sample, label):
    checked = Draft202012Validator(schema, format_checker=FormatChecker())
    checked.validate(sample)
    paths = list(_patterned_instance_paths(schema, schema, sample))
    testcase.assertTrue(paths, "terminal-whitespace corpus found no patterned fields for " + label)
    suffixes = {
        "LF": "\n", "CR": "\r", "CRLF": "\r\n", "SPACE": " ",
        "TAB": "\t", "NEL": "\u0085", "LINE_SEPARATOR": "\u2028",
        "PARAGRAPH_SEPARATOR": "\u2029",
    }
    for path, pattern in paths:
        cursor = sample
        for part in path:
            cursor = cursor[part]
        for suffix_name, suffix in suffixes.items():
            attacked = copy.deepcopy(sample)
            _replace_at_path(attacked, path, cursor + suffix)
            with testcase.subTest(
                document=label, path=path, pattern=pattern, suffix=suffix_name,
            ):
                with testcase.assertRaises(ValidationError):
                    checked.validate(attacked)


class ContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for schema in HOST_SCHEMAS.values():
            Draft202012Validator.check_schema(schema)
        for relative in PROVIDER_SCHEMAS.values():
            Draft202012Validator.check_schema(json.loads((REPOSITORY_ROOT / relative).read_text(encoding="utf-8")))
        for schema in ALL_FACTORY_SCHEMAS.values():
            Draft202012Validator.check_schema(schema)

    def test_runtime_documents_and_all_provider_outputs_validate(self):
        repository = InMemoryWorkflowRepository()
        adapter = DeterministicSimulationAdapter()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), adapter,
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
            StaticRuntimeControlAuthority(),
        )
        workflow_id = request()["workflow_id"]
        initial = service.start(request())
        completed = service.run_until_blocked(workflow_id, "contract-worker", maximum_steps=200)
        service.request_rollback(workflow_id, "rollback-worker", "CONTRACT_ROLLBACK_DRILL")

        workflow_request = repository.request(workflow_id)
        role_binding = repository.role_binding(workflow_id)
        validator("factory.workflow.request.v1.schema.json").validate(workflow_request)
        validator("factory.role.binding.v1.schema.json").validate(role_binding)
        validator("factory.workflow.status.v1.schema.json").validate(initial)
        validator("factory.workflow.status.v1.schema.json").validate(completed)
        events = repository.events(workflow_id)
        for event in events:
            validator("factory.workflow.event.v1.schema.json").validate(event)
        for command in adapter.calls:
            validator("factory.module.command.v1.schema.json").validate(command)
        stored_receipts = repository.receipts(workflow_id)
        for stored in stored_receipts:
            receipt = {key: value for key, value in stored.items() if key != "receipt_id"}
            validator("factory.module.receipt.v1.schema.json").validate(receipt)
            for output in receipt["outputs"]:
                schema = json.loads((REPOSITORY_ROOT / PROVIDER_SCHEMAS[output["contract_id"]]).read_text(encoding="utf-8"))
                Draft202012Validator(schema, format_checker=FormatChecker()).validate(output["payload"])
                assert_recursive_terminal_whitespace_rejected(
                    self, schema, output["payload"], output["contract_id"],
                )

        public_samples = {
            "factory.workflow.request.v1.schema.json": workflow_request,
            "factory.role.binding.v1.schema.json": role_binding,
            "factory.workflow.event.v1.schema.json": events[0],
            "factory.workflow.status.v1.schema.json": completed,
            "factory.module.command.v1.schema.json": adapter.calls[0],
            "factory.module.receipt.v1.schema.json": {
                key: value for key, value in stored_receipts[0].items() if key != "receipt_id"
            },
        }
        for schema_name, sample in public_samples.items():
            assert_recursive_terminal_whitespace_rejected(
                self, HOST_SCHEMAS[schema_name], sample, schema_name,
            )
            for field in ("device_binding_id", "platform_account_id", "trace_id", "idempotency_key"):
                attacked = copy.deepcopy(sample)
                attacked[field] += "\n"
                with self.subTest(schema=schema_name, field=field), self.assertRaises(ValidationError):
                    validator(schema_name).validate(attacked)

    def test_all_factory_terminal_patterns_use_ecmascript_absolute_end(self):
        schemas = dict(ALL_FACTORY_SCHEMAS)
        for schema_name, schema in schemas.items():
            for pointer, pattern in _walk_patterns(schema):
                with self.subTest(schema=schema_name, pointer=pointer):
                    self.assertFalse(
                        pattern.endswith("$"),
                        "ordinary terminal $ accepts trailing newline; use $(?![\\s\\S])",
                    )

    def test_request_schema_and_runtime_reject_unknown_authority_and_r4(self):
        attacked = request()
        attacked["shell"] = "sh -c true"
        with self.assertRaises(ValidationError):
            validator("factory.workflow.request.v1.schema.json").validate(attacked)
        with self.assertRaises(InvalidWorkflowRequest):
            validate_workflow_request(attacked)
        r4 = request()
        r4["risk_tier"] = "R4"
        validator("factory.workflow.request.v1.schema.json").validate(r4)
        with self.assertRaises(InvalidWorkflowRequest):
            validate_workflow_request(r4)

    def test_opaque_ids_reject_trailing_newline_in_schema_and_runtime(self):
        values = {
            "device_binding_id": "db_" + "a" * 32,
            "platform_account_id": "pa_" + "b" * 32,
            "trace_id": "trace_" + "c" * 32,
            "idempotency_key": "idem_" + "d" * 64,
        }
        schema = validator("factory.workflow.request.v1.schema.json")
        for field, value in values.items():
            valid = request("upgrade:factory-host-opaque-" + field.replace("_", "-"))
            valid[field] = value
            schema.validate(valid)
            validate_workflow_request(valid)
            attacked = copy.deepcopy(valid)
            attacked[field] = value + "\n"
            with self.subTest(field=field), self.assertRaises(ValidationError):
                schema.validate(attacked)
            with self.subTest(field=field), self.assertRaises(InvalidWorkflowRequest):
                validate_workflow_request(attacked)

        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), DeterministicSimulationAdapter(),
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
            StaticRuntimeControlAuthority(),
        )
        workflow_id = service.start(request("upgrade:factory-host-event-opaque"))["workflow_id"]
        attacked_event = copy.deepcopy(repository.events(workflow_id)[0])
        attacked_event["idempotency_key"] += "\n"
        with self.assertRaises(ValidationError):
            validator("factory.workflow.event.v1.schema.json").validate(attacked_event)

    def test_command_rejects_unknown_target_major_and_request_authored_argv(self):
        repository = InMemoryWorkflowRepository()
        adapter = DeterministicSimulationAdapter()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), adapter,
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
            StaticRuntimeControlAuthority(),
        )
        workflow_id = service.start(request("upgrade:factory-host-contract-0002"))["workflow_id"]
        service.run_until_blocked(workflow_id, "worker", maximum_steps=200)
        valid = adapter.calls[0]
        schema = validator("factory.module.command.v1.schema.json")
        schema.validate(valid)
        for field, value in (
            ("target_module", "unknown-provider"),
            ("contract_id", "factory.module.command/v2"),
            ("argv", ["sh", "-c", "true"]),
        ):
            attacked = copy.deepcopy(valid)
            attacked[field] = value
            with self.subTest(field=field), self.assertRaises(ValidationError):
                schema.validate(attacked)

    def test_simulation_receipt_and_status_cannot_claim_side_effect_or_production(self):
        repository = InMemoryWorkflowRepository()
        adapter = DeterministicSimulationAdapter()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), adapter,
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
            StaticRuntimeControlAuthority(),
        )
        workflow_id = service.start(request("upgrade:factory-host-contract-0003"))["workflow_id"]
        service.run_until_blocked(workflow_id, "worker", maximum_steps=200)
        receipt = dict(repository.receipts(workflow_id)[0])
        receipt.pop("receipt_id")
        receipt["side_effect_count"] = 1
        with self.assertRaises(ValidationError):
            validator("factory.module.receipt.v1.schema.json").validate(receipt)
        status = service.status(workflow_id)
        status["production_authorized"] = True
        with self.assertRaises(ValidationError):
            validator("factory.workflow.status.v1.schema.json").validate(status)

    def test_role_overlap_and_event_hash_corruption_fail_closed(self):
        directory = SimulationRoleDirectory()
        raw = dict(directory.resolve("upgrade:factory-host-contract-0004", "e" * 64))
        raw["roles"] = dict(raw["roles"])
        raw["roles"]["module-implementer"] = raw["roles"]["evidence-auditor"]
        with self.assertRaises(RoleSeparationError):
            validate_role_binding("upgrade:factory-host-contract-0004", raw)

        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, directory, DeterministicSimulationAdapter(),
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
            StaticRuntimeControlAuthority(),
        )
        workflow_id = service.start(request("upgrade:factory-host-contract-0005"))["workflow_id"]
        events = repository.events(workflow_id)
        events[0]["payload"]["request_sha256"] = "f" * 64
        with self.assertRaises(Exception):
            validate_event_stream(events)

    def test_intake_replay_persistence_contract_uses_only_domain_hash_keys(self):
        nonce = "nonce_" + "9" * 32
        expected = hashlib.sha256(
            b"DPS\x00dps.factory-control-plane-host/intake-replay/v1/requester-auth-nonce\x00"
            + json.dumps(
                {"value": nonce}, sort_keys=True, separators=(",", ":"),
                ensure_ascii=False, allow_nan=False,
            ).encode("utf-8")
        ).hexdigest()
        self.assertEqual(
            expected,
            intake_replay_claim_key_sha256("REQUESTER_AUTH_NONCE", nonce),
        )

        migration = (
            MODULE_ROOT / "migrations" / "002_intake_replay_guard.sql"
        ).read_text(encoding="utf-8")
        for claim_kind in (
            "INTENT_ID", "IDEMPOTENCY_KEY", "REQUESTER_AUTH_NONCE", "APPROVAL_NONCE",
        ):
            self.assertIn(claim_kind, migration)
        for raw_column in (
            "intent_id text", "idempotency_key text",
            "requester_auth_nonce text", "approval_nonce text",
        ):
            self.assertNotIn(raw_column, migration.lower())

        manifest = json.loads((MODULE_ROOT / "module.yaml").read_text(encoding="utf-8"))
        stores = manifest["data"]["ownedStores"]
        self.assertIn("postgresql:factory_control_plane_host.schema_migration", stores)
        self.assertIn("postgresql:factory_control_plane_host.intake_replay_binding", stores)
        self.assertIn("postgresql:factory_control_plane_host.intake_replay_conflict", stores)
        intake_declarations = [
            item for item in manifest["contracts"]["consumed"]
            if item["contractId"] == "upgrade.intent"
        ]
        self.assertEqual([1], [item["major"] for item in intake_declarations])

    def test_native_stop_trust_manifest_and_release_edge_are_exact_reciprocals(self):
        host_manifest = json.loads(
            (MODULE_ROOT / "module.yaml").read_text(encoding="utf-8"),
        )
        release_root = REPOSITORY_ROOT / "Modules/factory-release-controller"
        release_manifest = json.loads(
            (release_root / "module.yaml").read_text(encoding="utf-8"),
        )
        contract_id = "release.bom.native.stop.authority.trust"
        host_contracts = [
            item for item in host_manifest["contracts"]["consumed"]
            if item["contractId"] == contract_id
        ]
        release_contracts = [
            item for item in release_manifest["contracts"]["provided"]
            if item["contractId"] == contract_id
        ]
        self.assertEqual(1, len(host_contracts))
        self.assertEqual(1, len(release_contracts))
        self.assertEqual(
            {
                "contractId": contract_id,
                "major": 1,
                "source": "Modules/factory-release-controller/contracts/provided/release.bom.native.stop.authority.trust.v1.schema.json",
                "status": "proposed",
                "mode": "active",
                "ownerModule": "factory-release-controller",
            },
            host_contracts[0],
        )
        self.assertEqual(release_contracts[0], host_contracts[0])

        inbound = [
            item for item in host_manifest["communication"]["inbound"]
            if item["contractId"] == contract_id
        ]
        outbound = [
            item for item in release_manifest["communication"]["outbound"]
            if item["contractId"] == contract_id
            and item["peerModule"] == "factory-control-plane-host"
        ]
        self.assertEqual(1, len(inbound))
        self.assertEqual(1, len(outbound))
        self.assertEqual("inbound", inbound[0]["direction"])
        self.assertEqual("outbound", outbound[0]["direction"])
        for field in (
            "contractId", "major", "transport", "timeoutMs", "retryPolicy",
            "idempotencyKey", "authScope", "failureMode",
        ):
            self.assertEqual(outbound[0][field], inbound[0][field], field)
        self.assertEqual("factory-release-controller", inbound[0]["peerModule"])
        self.assertEqual("factory-control-plane-host", outbound[0]["peerModule"])
        self.assertEqual(
            [1],
            host_manifest["compatibility"]["supportedContractMajors"][contract_id],
        )
        self.assertEqual("reject", host_manifest["compatibility"]["unknownMajorBehavior"])
        self.assertEqual("reject", host_manifest["compatibility"]["missingMajorBehavior"])

        schema_path = REPOSITORY_ROOT / host_contracts[0]["source"]
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        self.assertEqual(
            "release.bom.native.stop.authority.trust/v1",
            schema["properties"]["contract_id"]["const"],
        )
        checked = Draft202012Validator(schema, format_checker=FormatChecker())
        fixture = {
            "schema_version": "1.0.0",
            "contract_id": "release.bom.native.stop.authority.trust/v2",
            "producer_module": "factory-release-controller",
        }
        with self.assertRaises(ValidationError):
            checked.validate(fixture)
        fixture["contract_id"] = "release.bom.native-stop-authority-trust/v1"
        with self.assertRaises(ValidationError):
            checked.validate(fixture)

        migration = (
            MODULE_ROOT / "migrations" / "003_native_stop_trust_binding.sql"
        ).read_text(encoding="utf-8")
        for required in (
            "native_stop_authority_trust_binding", "receipt_id text PRIMARY KEY",
            "receipt_sha256 char(64)", "release_bom_sha256 char(64)",
            "release_bom_generation bigint", "authority_sets_sha256 char(64)",
            "fact_sha256 char(64)", "fact_json jsonb", "reject_mutation",
            "reject_truncate",
        ):
            self.assertIn(required, migration)
        for forbidden in (
            "activation_token text", "private_key", "private key", "api_key",
            "password text", "credential text", "service_secret",
        ):
            self.assertNotIn(forbidden, migration.lower())
        self.assertIn(
            "postgresql:factory_control_plane_host.native_stop_authority_trust_binding",
            host_manifest["data"]["ownedStores"],
        )


if __name__ == "__main__":
    unittest.main()
