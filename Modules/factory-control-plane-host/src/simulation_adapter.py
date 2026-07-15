"""Deterministic, zero-side-effect adapters for local Factory simulation only."""

from __future__ import annotations

import copy
import datetime as dt
from typing import Any, Callable, Mapping

from factory_control_plane_host import (
    ROLE_PATH_CLASSES, canonical_bytes, logical_request_sha256, sha256, utc_now,
    writer_role_for_path,
)


def _common(command: Mapping[str, Any], contract_id: str, producer: str) -> dict[str, Any]:
    request = command["context"]["workflow_request"]
    return {
        "schema_version": "1.0.0", "contract_id": contract_id, "producer_module": producer,
        "soul_id": request["soul_id"], "device_binding_id": request["device_binding_id"],
        "platform_account_id": request["platform_account_id"], "trace_id": request["trace_id"],
        "idempotency_key": "idem_" + sha256(command["request_id"]), "occurred_at": command["occurred_at"],
        "privacy_class": "internal",
    }


class SimulationRoleDirectory:
    def __init__(self, prefix: str = "sim") -> None:
        self._roles = {
            "impact-planner": prefix + ":impact-planner",
            "contract-architect": prefix + ":contract-architect",
            "module-implementer": prefix + ":module-implementer",
            "independent-test-agent": prefix + ":independent-test-agent",
            "security-privacy-adversary": prefix + ":security-privacy-adversary",
            "reliability-reviewer": prefix + ":reliability-reviewer",
            "windows-zenno-reviewer": prefix + ":windows-zenno-reviewer",
            "evidence-auditor": prefix + ":evidence-auditor",
            "release-rollback-controller": prefix + ":release-rollback-controller",
        }

    def resolve(self, workflow_id: str, request_sha256: str) -> Mapping[str, Any]:
        return {
            "verified": True, "policy_sha256": sha256({"roles": self._roles, "request": request_sha256}),
            "verifier_identity": "simulation-role-directory", "verified_at": "2026-07-14T00:00:00Z",
            "roles": dict(self._roles), "verification_ref": "simulation:" + sha256(workflow_id)[:32],
        }


class SimulationExternalAuthority:
    def __init__(
        self, *, production_bom_available: bool = False,
        production_human_available: bool = False,
        production_rollback_available: bool = False,
        clock: Callable[[], dt.datetime] | None = None,
    ) -> None:
        self.production_bom_available = production_bom_available
        self.production_human_available = production_human_available
        self.production_rollback_available = production_rollback_available
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))

    def verify_signed_bom(self, workflow_id: str, request_sha256: str, external_context_ref: str | None, mode: str) -> Mapping[str, Any] | None:
        if mode == "PRODUCTION" and not self.production_bom_available:
            return None
        return {
            "verified": True, "fact_id": "bom-fact:" + sha256(workflow_id)[:32],
            "bom_sha256": sha256({"workflow": workflow_id, "kind": "bom"}),
            "artifact_sha256": sha256({"workflow": workflow_id, "kind": "artifact"}),
            "previous_stable_bom_id": "stable-bom:" + sha256(workflow_id)[:32],
            "previous_stable_bom_sha256": sha256({"workflow": workflow_id, "kind": "previous-stable-bom"}),
            "previous_stable_verification_id": "stable-proof:" + sha256(workflow_id)[:32],
            "signer_identity": "simulation-external-signer" if mode == "SIMULATION" else "deployed-external-signer",
            "signature_sha256": sha256({"workflow": workflow_id, "kind": "bom-signature"}),
            "signature_key_id": "signing-key:" + sha256(workflow_id)[:32],
            "verified_at": utc_now(),
            "simulation_only": mode == "SIMULATION", "request_sha256": request_sha256,
            "external_context_ref": external_context_ref,
        }

    def verify_human_transition(
        self,
        workflow_id: str,
        request_sha256: str,
        external_context_ref: str | None,
        risk_tier: str,
        from_state: str,
        to_state: str,
        role_identities: tuple[str, ...],
    ) -> Mapping[str, Any] | None:
        if not self.production_human_available:
            return None
        issued = self._clock()
        expires = issued + dt.timedelta(minutes=10)
        issued_at = issued.isoformat().replace("+00:00", "Z")
        expires_at = expires.isoformat().replace("+00:00", "Z")
        approver = "human:release-approver"
        bom_sha256 = sha256({"workflow": workflow_id, "kind": "bom"})
        artifact_sha256 = sha256({"workflow": workflow_id, "kind": "artifact"})
        bom_signature_sha256 = sha256({"workflow": workflow_id, "kind": "bom-signature"})
        nonce = "approval-nonce:" + sha256({
            "workflow_id": workflow_id,
            "request_sha256": request_sha256,
            "bom_sha256": bom_sha256,
            "artifact_sha256": artifact_sha256,
            "bom_signature_sha256": bom_signature_sha256,
            "approver_identity": approver,
            "from_state": from_state,
            "to_state": to_state,
            "issued_at": issued_at,
            "expires_at": expires_at,
        })[:32]
        return {
            "verified": True,
            "fact_id": "approval:" + sha256({
                "workflow": workflow_id, "from_state": from_state,
                "to_state": to_state, "issued_at": issued_at,
            })[:32],
            "approver_identity": approver, "risk_tier": risk_tier,
            "request_sha256": request_sha256, "external_context_ref": external_context_ref,
            "bom_sha256": bom_sha256, "artifact_sha256": artifact_sha256,
            "bom_signature_sha256": bom_signature_sha256,
            "from_state": from_state, "to_state": to_state,
            "approval_nonce": nonce, "issued_at": issued_at,
            "expires_at": expires_at,
            "approval_signature_sha256": sha256({
                "workflow_id": workflow_id, "nonce": nonce, "approver": approver,
            }),
            "approval_key_id": "approval-key:" + sha256(workflow_id)[:32],
        }

    def verify_rollback_authorization(
        self,
        workflow_id: str,
        request_sha256: str,
        external_context_ref: str | None,
        mode: str,
        reason_code: str,
        previous_stable_bom_sha256: str,
    ) -> Mapping[str, Any] | None:
        if mode == "PRODUCTION" and not self.production_rollback_available:
            return None
        issued = self._clock()
        expires = issued + dt.timedelta(minutes=10)
        issued_at = issued.isoformat().replace("+00:00", "Z")
        expires_at = expires.isoformat().replace("+00:00", "Z")
        return {
            "verified": True,
            "fact_id": "rollback-auth:" + sha256({
                "workflow": workflow_id, "reason": reason_code, "issued_at": issued_at,
            })[:32],
            "authorizer_identity": (
                "simulation-rollback-authority"
                if mode == "SIMULATION"
                else "deployed-rollback-safety-authority"
            ),
            "authorization_kind": (
                "SIMULATION_POLICY" if mode == "SIMULATION" else "AUTOMATED_SAFETY"
            ),
            "request_sha256": request_sha256,
            "external_context_ref": external_context_ref,
            "reason_code": reason_code,
            "candidate_bom_sha256": sha256({"workflow": workflow_id, "kind": "bom"}),
            "previous_stable_bom_sha256": previous_stable_bom_sha256,
            "previous_stable_verification_id": "stable-proof:" + sha256(workflow_id)[:32],
            "authorization_signature_sha256": sha256({
                "workflow": workflow_id, "reason": reason_code,
                "previous_stable_bom_sha256": previous_stable_bom_sha256,
                "issued_at": issued_at, "expires_at": expires_at,
            }),
            "authorization_key_id": "rollback-key:" + sha256(workflow_id)[:32],
            "verified_at": issued_at,
            "expires_at": expires_at,
            "simulation_only": mode == "SIMULATION",
        }


class DeterministicSimulationAdapter:
    def __init__(self, failures: Mapping[str, str] | None = None) -> None:
        self.failures = dict(failures or {})
        self.calls: list[dict[str, Any]] = []
        self._logical_results: dict[str, dict[str, Any]] = {}

    def invoke(self, command: Mapping[str, Any]) -> Mapping[str, Any]:
        self._validate_command(command)
        self.calls.append(copy.deepcopy(dict(command)))
        request_id = str(command["request_id"])
        cached = self._logical_results.get(request_id)
        if cached is not None:
            if cached["logical_request_sha256"] != command["logical_request_sha256"]:
                raise ValueError("request_id is already bound to different logical content")
            status = str(cached["status"])
            outputs = copy.deepcopy(cached["outputs"])
        else:
            status = self.failures.get(str(command["operation"]), "PASS")
            outputs = [] if status != "PASS" else [self._output(command, contract_id) for contract_id in command["expected_output_contracts"]]
            self._logical_results[request_id] = {
                "logical_request_sha256": command["logical_request_sha256"],
                "status": status,
                "outputs": copy.deepcopy(outputs),
            }
        unsigned = {
            "schema_version": "1.0.0", "contract_id": "factory.module.receipt/v1",
            "producer_module": "factory-control-plane-host",
            "soul_id": command["soul_id"], "device_binding_id": command["device_binding_id"],
            "platform_account_id": command["platform_account_id"], "trace_id": command["trace_id"],
            "idempotency_key": command["idempotency_key"], "privacy_class": "internal",
            "workflow_id": command["workflow_id"],
            "request_id": command["request_id"], "stage_id": command["stage_id"],
            "target_module": command["target_module"], "operation": command["operation"],
            "actor_identity": command["actor_identity"], "actor_role": command["actor_role"],
            "fencing_token": command["fencing_token"],
            "logical_request_sha256": command["logical_request_sha256"],
            "status": status, "mode": "SIMULATION",
            "evidence_kind": "SIMULATION", "verification_level": "INTEGRATION_VERIFIED",
            "simulation_only": True, "side_effect_count": 0, "outputs": outputs,
            "occurred_at": command["occurred_at"],
        }
        receipt = dict(unsigned)
        receipt["attestation"] = {
            "kind": "SIMULATION_ONLY", "verifier_identity": "deterministic-simulation-adapter",
            "payload_sha256": sha256(unsigned), "reference": "simulation:" + sha256(unsigned)[:32],
        }
        return receipt

    @staticmethod
    def _validate_command(command: Mapping[str, Any]) -> None:
        if command.get("mode") != "SIMULATION" or command.get("fencing_token", 0) < 1:
            raise ValueError("simulation adapter accepts only fenced SIMULATION commands")
        if any(name in command for name in ("argv", "shell", "command", "environment", "cwd")):
            raise ValueError("request-authored process authority is forbidden")
        context = command.get("context")
        if not isinstance(context, Mapping) or command.get("context_sha256") != sha256(context):
            raise ValueError("simulation command context digest mismatch")
        if command.get("logical_request_sha256") != logical_request_sha256(command):
            raise ValueError("simulation command logical request digest mismatch")
        role = command.get("actor_role")
        if context.get("allowed_path_classes") != list(ROLE_PATH_CLASSES.get(str(role), ())) or role not in ROLE_PATH_CLASSES:
            raise ValueError("role path capability was not host-bound")

    def _output(self, command: Mapping[str, Any], contract_id: str) -> dict[str, Any]:
        producer = {
            "upgrade.intent/v1": "factory-upgrade-intake", "instruction.receipt/v1": "factory-instruction-resolver",
            "module.change.plan/v1": "factory-impact-analyzer", "worktree.plan/v1": "factory-worktree-manager",
            "worktree.lease/v1": "factory-worktree-manager", "trusted.test.result/v1": "factory-trusted-runner",
            "merge.decision/v1": "factory-merge-controller", "artifact.descriptor/v1": "factory-artifact-builder",
            "upgrade.event/v1": "factory-evidence-ledger", "rollout.event/v1": "factory-release-controller",
            "rollback.plan/v1": "factory-rollback-controller", "rollback.result/v1": "factory-rollback-controller",
        }[contract_id]
        payload = self._payload(command, contract_id, producer)
        return {"contract_id": contract_id, "producer_module": producer, "payload_sha256": sha256(payload), "payload": payload}

    def _payload(self, command: Mapping[str, Any], contract_id: str, producer: str) -> dict[str, Any]:
        common = _common(command, contract_id, producer)
        context = command["context"]
        request = context["workflow_request"]
        causal_outputs = context.get("causal_outputs", {})

        def prior(contract: str) -> list[Mapping[str, Any]]:
            group = causal_outputs.get(contract, []) if isinstance(causal_outputs, Mapping) else []
            return [item["payload"] for item in group if isinstance(item, Mapping) and isinstance(item.get("payload"), Mapping)]

        suffix = sha256({"request": command["request_id"], "contract": contract_id})
        baseline = request["baseline_commit"]
        targets = list(request["target_modules"])
        paths = list(request["requested_paths"])
        changeset_commit = sha256({
            "baseline_commit": baseline,
            "requested_paths": paths,
            "public_contract_changes": request["public_contract_changes"],
        })[:40]
        if contract_id == "upgrade.intent/v1":
            common["schema_version"] = "dps.upgrade-intent/v1"
            common.update({
                "intent_id": "intent:" + suffix[:32], "auth_context_id": "authctx:" + suffix[:32],
                "baseline_commit": baseline, "target_modules": targets, "requested_paths": paths,
                "public_contract_changes": list(request["public_contract_changes"]), "risk_tier": request["risk_tier"],
                "requested_stage": "development", "requester": {"identity": command["actor_identity"], "role": "impact-planner"},
                "authorization": {"status": "not-required", "approved_by": None, "approver_role": "not-applicable", "approval_scope": None},
            })
            return common
        if contract_id == "instruction.receipt/v1":
            common["schema_version"] = "dps.instruction-receipt/v1"
            intents = prior("upgrade.intent/v1")
            if not intents:
                raise ValueError("instruction simulation lacks upgrade intent")
            def bound(path: str, order: int) -> dict[str, Any]:
                return {"path": path, "order": order, "source_state": "tracked", "git_blob": "a" * 40, "sha256": sha256(path)}
            common.update({
                "receipt_id": "instruction:" + suffix[:32], "intent_id": intents[-1]["intent_id"],
                "auth_context_id": intents[-1]["auth_context_id"], "agent_identity": command["actor_identity"],
                "agent_role": command["actor_role"], "baseline_commit": baseline,
                "resolved_at": command["occurred_at"], "scope": targets,
                "instructions": [bound("AGENTS.md", 0), bound("Modules/%s/AGENTS.md" % targets[0], 1)],
                "manifests": [bound("Modules/%s/module.yaml" % targets[0], 2)], "contracts": [],
                "governance": [bound("governance/modules/dependency-graph.yaml", 3), bound("governance/modules/compatibility.yaml", 4), bound("governance/policies/risk-policy.yaml", 5)],
                "tests": [bound("Modules/%s/tests/README.md" % targets[0], 6)],
                "operations": [bound("Modules/%s/operations/README.md" % targets[0], 7)],
                "diff_fingerprint": suffix, "status": "BOUND", "invalidated_reason": None,
            })
            return common
        if contract_id == "module.change.plan/v1":
            common["schema_version"] = "dps.module-change-plan/v1"
            intents = prior("upgrade.intent/v1")
            instructions = prior("instruction.receipt/v1")
            if not intents or not instructions:
                raise ValueError("change-plan simulation lacks bound inputs")
            role_ids = context["role_identities"]
            common.update({
                "plan_id": "change:" + suffix[:32], "intent_id": intents[-1]["intent_id"],
                "instruction_receipt_id": instructions[-1]["receipt_id"], "baseline_commit": baseline,
                "risk_tier": request["risk_tier"], "affected_modules": targets, "requested_paths": paths,
                "public_contract_changes": list(request["public_contract_changes"]), "dependency_edges": [],
                "parallel_waves": [targets], "required_checks": ["phase0.repository", "merge.head"],
                "role_assignments": {
                    "impact_planner": [role_ids["impact-planner"]],
                    "contract_architect": [role_ids["contract-architect"]],
                    "module_implementer": [role_ids["module-implementer"]],
                    "independent_test_agent": [role_ids["independent-test-agent"]],
                    "evidence_auditor": [role_ids["evidence-auditor"]],
                    "release_approver": ["human:simulation-release-approver"],
                }, "trusted_policy_sha256": suffix,
            })
            return common
        if contract_id == "worktree.plan/v1":
            common["schema_version"] = "dps.worktree-plan/v1"
            changes = prior("module.change.plan/v1")
            instructions = prior("instruction.receipt/v1")
            if not changes or not instructions:
                raise ValueError("worktree simulation lacks bound plan inputs")
            role_ids = context["role_identities"]
            contract_paths = [
                path for path in paths
                if "/contracts/" in path
            ]
            module_paths = [path for path in paths if path not in contract_paths]
            entries = []
            for module_id in targets:
                for writer_role in (
                    "module-implementer", "independent-test-agent",
                    "contract-architect",
                    "reliability-reviewer",
                ):
                    owned = [
                        path for path in module_paths
                        if path.startswith("Modules/%s/" % module_id)
                        and writer_role_for_path(path) == writer_role
                    ]
                    if not owned:
                        continue
                    entries.append({
                        "module_id": module_id,
                        "writer_identity": role_ids[writer_role],
                        "owned_paths": owned,
                        "worktree_ref": "factory-worktree:%s-%s:%s" % (
                            module_id, writer_role, suffix[:16],
                        ),
                        "depends_on": [],
                        "lease_keys": [
                            "module:%s:writer:%s" % (module_id, writer_role),
                            *("path:" + path for path in owned),
                        ],
                    })
            contract_worktree = None
            if request["public_contract_changes"]:
                if not contract_paths:
                    raise ValueError("declared contract change lacks a contract path")
                contract_worktree = {
                    "writer_identity": role_ids["contract-architect"],
                    "contract_ids": list(request["public_contract_changes"]),
                    "owned_paths": contract_paths,
                    "worktree_ref": "factory-contract-worktree:" + suffix[:16],
                    "lease_keys": ["contract:" + item for item in request["public_contract_changes"]] + ["path:" + path for path in contract_paths],
                }
            common.update({
                "plan_id": "worktree:" + suffix[:32], "change_plan_id": changes[-1]["plan_id"],
                "instruction_receipt_id": instructions[-1]["receipt_id"], "baseline_commit": baseline,
                "entries": entries, "contract_worktree": contract_worktree,
                "trusted_policy_sha256": changes[-1]["trusted_policy_sha256"],
            })
            return common
        if contract_id == "worktree.lease/v1":
            common["schema_version"] = "dps.worktree-lease/v1"
            worktree_plans = prior("worktree.plan/v1")
            if not worktree_plans:
                raise ValueError("worktree lease simulation lacks its causal plan")
            worktree_plan = worktree_plans[-1]
            lock_keys: list[str] = []
            for entry in worktree_plan["entries"]:
                if entry["writer_identity"] == command["actor_identity"]:
                    lock_keys.extend(entry["lease_keys"])
            contract_entry = worktree_plan.get("contract_worktree")
            if (
                isinstance(contract_entry, Mapping)
                and contract_entry["writer_identity"] == command["actor_identity"]
            ):
                lock_keys.extend(contract_entry["lease_keys"])
            if not lock_keys:
                raise ValueError("worktree lease actor has no planned path coverage")
            acquired = dt.datetime.fromisoformat(command["occurred_at"].replace("Z", "+00:00"))
            expires = (acquired + dt.timedelta(minutes=10)).isoformat().replace("+00:00", "Z")
            common.update({
                "lease_id": "lease:" + suffix[:32], "plan_id": worktree_plan["plan_id"],
                "holder_identity": command["actor_identity"], "lock_keys": lock_keys,
                "lock_tokens": {lock: command["fencing_token"] for lock in lock_keys},
                "fencing_token": command["fencing_token"], "acquired_at": command["occurred_at"],
                "expires_at": expires, "status": "ACTIVE",
            })
            return common
        if contract_id == "trusted.test.result/v1":
            check_id = "factory." + command["operation"]
            instructions = prior("instruction.receipt/v1")
            worktrees = prior("worktree.plan/v1")
            leases = prior("worktree.lease/v1")
            if not instructions or not worktrees or not leases:
                raise ValueError("test simulation lacks worktree truth")
            subject_module = context.get("subject_module")
            if subject_module not in targets:
                raise ValueError("test simulation lacks its host-bound subject module")
            matching_leases = [
                lease for lease in leases
                if any(
                    key.startswith("module:%s:" % subject_module)
                    or key.startswith("path:Modules/%s/" % subject_module)
                    for key in lease.get("lock_keys", [])
                )
            ]
            if not matching_leases:
                raise ValueError("test simulation lacks a subject-scoped worktree lease")
            lease = matching_leases[-1]
            unsigned = dict(common)
            unsigned.update({
                "result_id": "result:" + suffix[:32], "request_id": command["request_id"],
                "worktree_plan_id": worktrees[-1]["plan_id"], "module_id": subject_module,
                "check_id": check_id, "suite_id": check_id, "evidence_level": "INTEGRATION_VERIFIED",
                "template_id": "python.unit", "tested_commit": changeset_commit, "required": True, "status": "PASS",
                "release_allowed": True, "runner_identity": "simulation-trusted-runner",
                "auth_context_id": instructions[-1]["auth_context_id"],
                "instruction_receipt_id": instructions[-1]["receipt_id"],
                "manifest_sha256": suffix, "workspace_sha256": sha256(paths), "required_checks_sha256": sha256([check_id]),
                "trusted_policy_sha256": sha256("simulation-runner-policy"), "lease_id": lease["lease_id"],
                "fencing_token": lease["fencing_token"], "command_argv": ["python3.12", "-m", "unittest"],
                "timeout_seconds": 60, "started_at": command["occurred_at"], "finished_at": command["occurred_at"],
                "exit_code": 0, "stdout_sha256": sha256(b"OK"), "stderr_sha256": sha256(b""),
                "log_sha256": sha256(b"OK"),
            })
            unsigned["raw_artifact_sha256"] = sha256(unsigned)
            unsigned["runner_attestation"] = {"algorithm": "rsa-pss-sha256", "key_id": "simulation-key", "signer_identity": "simulation-trusted-runner", "payload_sha256": sha256(unsigned), "signature_value": "A" * 128}
            return unsigned
        if contract_id == "merge.decision/v1":
            results = prior("trusted.test.result/v1")
            if not results:
                raise ValueError("merge simulation lacks trusted results")
            common.update({
                "decision_id": "merge-" + suffix[:32], "merge_request_id": "merge-request:" + suffix[:32],
                "integration_commit": changeset_commit, "outcome": "APPROVED", "reasons": [],
                "evidence_ids": [item["result_id"] for item in results], "decided_by": "simulation-merge-controller",
                "verification_scope": "MERGE_HEAD_ONLY", "trusted_policy_sha256": suffix,
                "runner_attestation_sha256": sha256("simulation-runner-attestation"),
            })
            return common
        if contract_id == "artifact.descriptor/v1":
            decisions = prior("merge.decision/v1")
            if not decisions:
                raise ValueError("artifact simulation lacks merge decision")
            common["idempotency_key"] = "idem_" + suffix
            artifact_sha = sha256({"workflow": request["workflow_id"], "kind": "artifact"})
            common.update({
                "artifact_id": "artifact-" + artifact_sha[:32], "build_id": "build:" + suffix[:32],
                "module_id": targets[0], "module_version": "0.1.0", "integration_commit": decisions[-1]["integration_commit"],
                "artifact_uri": "sha256:" + artifact_sha, "artifact_file": "candidate.zip",
                "artifact_sha256": artifact_sha, "size_bytes": 1, "merge_decision_id": decisions[-1]["decision_id"],
                "trusted_merge_policy_sha256": suffix, "source_tree_sha256": sha256(paths),
                "agents_sha256": sha256("AGENTS"), "manifest_sha256": sha256("module"),
                "sbom": {"path": "candidate.spdx.json", "sha256": sha256("sbom"), "media_type": "application/json"},
                "provenance": {"path": "candidate.provenance.json", "sha256": sha256("provenance"), "media_type": "application/json"},
                "signature": {"status": "UNSIGNED_AWAITING_EXTERNAL_SIGNER", "signer_required": "external-controlled-signer"},
            })
            return common
        if contract_id == "upgrade.event/v1":
            payload = {"audit": "SIMULATION_ONLY", "workflow_id": request["workflow_id"]}
            common.update({
                "event_id": "event-" + suffix[:32], "stream_id": request["workflow_id"], "sequence": 1,
                "event_type": "SIMULATION_AUDITED", "source_module": "factory-release-controller",
                "payload": payload, "payload_sha256": sha256(payload), "previous_event_sha256": "0" * 64,
                "append_status": "APPENDED",
            })
            material = {key: value for key, value in common.items() if key != "event_sha256"}
            common["event_sha256"] = sha256(material)
            return common
        if contract_id == "rollout.event/v1":
            state_map = {"verify-signed-bom": ("CANDIDATE_VERIFIED", "BOM_SIGNED"), "run-shadow": ("BOM_SIGNED", "SHADOW"), "run-canary": ("SHADOW", "CANARY"), "run-rolling": ("CANARY", "ROLLING"), "run-soak": ("ROLLING", "SOAKING"), "complete-release": ("SOAKING", "COMPLETED")}
            previous, current = state_map.get(command["operation"], ("REQUESTED", "REQUESTED"))
            artifacts = prior("artifact.descriptor/v1")
            rollouts = prior("rollout.event/v1")
            external = context.get("external_fact") or {}
            if not artifacts:
                raise ValueError("rollout simulation lacks artifact truth")
            bom_sha = external.get("bom_sha256") or (rollouts[-1]["bom_sha256"] if rollouts else sha256("simulation-bom"))
            artifact_sha = external.get("artifact_sha256") or artifacts[-1]["artifact_sha256"]
            common.update({
                "rollout_event_id": "rollout-" + suffix[:32], "upgrade_id": request["workflow_id"],
                "previous_state": previous, "current_state": current, "risk_tier": request["risk_tier"],
                "actor_identity": command["actor_identity"], "actor_role": "release-controller",
                "bom_sha256": bom_sha, "artifact_sha256": artifact_sha,
                "evidence_kind": "SIMULATION", "verification_level": "INTEGRATION_VERIFIED",
                "simulation_only": True, "side_effect_count": 0, "kill_switch_armed": True,
                "transition_request_sha256": sha256(command), "trusted_facts_sha256": sha256("simulation-facts"),
                "candidate_validation_sha256": sha256("simulation-candidate"), "evidence_refs": ["simulation:" + suffix[:16]],
            })
            return common
        if contract_id == "rollback.plan/v1":
            release_fact = context.get("bound_release_fact")
            authorization = context.get("bound_rollback_authorization")
            if not isinstance(release_fact, Mapping) or not isinstance(authorization, Mapping):
                raise ValueError("rollback simulation lacks bound release and authorization facts")
            common.update({
                "rollback_id": "rollback:" + suffix[:32], "upgrade_id": request["workflow_id"],
                "rollback_unit": "ROLLBACKABLE", "target_bom_id": release_fact["previous_stable_bom_id"],
                "target_bom_sha256": release_fact["previous_stable_bom_sha256"], "deadline_seconds": 300,
                "ordered_steps": ["STOP_ROUTING", "DRAIN", "RECONCILE", "SWITCH_BOM", "VERIFY"],
                "compensation_plan": None, "request_sha256": sha256(request),
                "stable_bom_verification_id": release_fact["previous_stable_verification_id"],
            })
            return common
        if contract_id == "rollback.result/v1":
            plans = prior("rollback.plan/v1")
            authorization = context.get("bound_rollback_authorization")
            if not plans or not isinstance(authorization, Mapping):
                raise ValueError("rollback simulation lacks rollback plan or authorization")
            target = plans[-1]["target_bom_sha256"]
            common.update({
                "rollback_id": plans[-1]["rollback_id"], "upgrade_id": request["workflow_id"],
                "rollback_unit": "ROLLBACKABLE", "outcome": "ROLLED_BACK",
                "completed_steps": ["STOP_ROUTING", "DRAIN", "RECONCILE", "SWITCH_BOM", "VERIFY"],
                "duration_seconds": 1, "target_bom_sha256": target, "active_bom_sha256": target,
                "verified_postconditions": True, "compensation_evidence_ids": [], "reason": None,
                "request_sha256": sha256(request), "plan_sha256": sha256(plans[-1]),
                "authorization_id": authorization["fact_id"],
                "stable_bom_verification_id": plans[-1]["stable_bom_verification_id"],
            })
            return common
        raise ValueError("unsupported simulation output contract")


class CrashAfterProviderSuccessAdapter:
    """Raises once after a deterministic provider produced a receipt."""

    def __init__(self, inner: DeterministicSimulationAdapter, operation: str) -> None:
        self.inner = inner
        self.operation = operation
        self._crashed = False

    def invoke(self, command: Mapping[str, Any]) -> Mapping[str, Any]:
        receipt = self.inner.invoke(command)
        if command.get("operation") == self.operation and not self._crashed:
            self._crashed = True
            raise RuntimeError("SIMULATED_PROCESS_CRASH_AFTER_PROVIDER_SUCCESS")
        return receipt


__all__ = [
    "CrashAfterProviderSuccessAdapter", "DeterministicSimulationAdapter",
    "SimulationExternalAuthority", "SimulationRoleDirectory",
]
