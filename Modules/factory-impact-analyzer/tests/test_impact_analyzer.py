import copy
import datetime as dt
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


REPOSITORY_ROOT = pathlib.Path(__file__).resolve().parents[3]
MODULE_ROOT = pathlib.Path(__file__).resolve().parents[1]


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


IMPACT = load_module(
    "impact_analyzer_v2_subject", MODULE_ROOT / "src" / "impact_analyzer.py"
)
INTAKE = load_module(
    "impact_test_real_intake",
    REPOSITORY_ROOT / "Modules/factory-upgrade-intake/src/upgrade_intake.py",
)
RESOLVER = load_module(
    "impact_test_real_resolver",
    REPOSITORY_ROOT
    / "Modules/factory-instruction-resolver/src/instruction_resolver.py",
)


def canonical_bytes(value):
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")


class IntakeClock:
    def __call__(self):
        return dt.datetime(2026, 7, 15, 1, 0, tzinfo=dt.timezone.utc)


class AuthPort(INTAKE.AuthVerificationPort):
    def verify(self, record):
        return record.get("verification_material") == {"fixture": "signed"}


class ManifestPort(INTAKE.ManifestOwnershipVerificationPort):
    def verify(self, baseline_commit, snapshot_sha256, receipt_id):
        return bool(baseline_commit and snapshot_sha256 and receipt_id.startswith("manifest:"))


class PipelineFixture:
    """A clean, isolated Git worktree exercising the real Intake and Resolver."""

    def __init__(self, change_kind="additive-schema", *, stage="development"):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.change_kind = change_kind
        self.stage = stage
        self._write("AGENTS.md", "root manifest contract tests compatibility rollout rollback")
        for path in (
            "governance/modules/module-catalog.yaml",
            "governance/modules/dependency-graph.yaml",
            "governance/modules/compatibility.yaml",
            "governance/policies/risk-policy.yaml",
            "governance/policies/compatibility-policy.yaml",
        ):
            self._write(path, "{}")

        self._module(
            "factory-upgrade-intake",
            provides=[
                self._provided("upgrade.intent", 1, "quarantine-only", "deprecated"),
                self._provided("upgrade.intent", 2, "active"),
            ],
        )
        self._module(
            "factory-instruction-resolver",
            provides=[
                self._provided(
                    "instruction.receipt", 1, "quarantine-only", "deprecated"
                ),
                self._provided("instruction.receipt", 2, "active"),
            ],
            consumes=[
                self._consumed(
                    "upgrade.intent", 1, "factory-upgrade-intake",
                    "quarantine-only", "deprecated",
                ),
                self._consumed(
                    "upgrade.intent", 2, "factory-upgrade-intake", "active"
                ),
            ],
            dependencies=["factory-upgrade-intake"],
        )

        baseline_contracts = []
        baseline_consumers = []
        if change_kind in {"additive-schema", "mode-transition"}:
            baseline_contracts = [self._provided("alpha.event", 1, "active")]
            baseline_consumers = [
                self._consumed("alpha.event", 1, "alpha-module", "active")
            ]
        elif change_kind == "add-major":
            baseline_contracts = [self._provided("alpha.event", 1, "active")]
            # This exact v1 consumer must never be pulled into a v2 change.
            baseline_consumers = [
                self._consumed("alpha.event", 1, "alpha-module", "active")
            ]
        self._module("alpha-module", provides=baseline_contracts)
        self._module(
            "exact-consumer",
            consumes=baseline_consumers,
            dependencies=["alpha-module"] if baseline_consumers else [],
        )
        if baseline_consumers:
            self._set_edge_pair("alpha-module", "exact-consumer", "alpha.event", 1)

        self._git("init", "-q")
        self._git("config", "user.email", "impact@example.invalid")
        self._git("config", "user.name", "Impact Test")
        self._git("add", ".")
        self._git("commit", "-qm", "baseline")
        self.baseline = self._git("rev-parse", "HEAD")
        self._materialize_change(change_kind)

        self.intent_clock = INTAKE.ProcessBoundAuthAuthority(
            AuthPort(), clock=IntakeClock()
        )
        self.manifest_authority = INTAKE.ProcessBoundManifestAuthority(ManifestPort())
        self.ownership = self.manifest_authority.verify(
            self.baseline,
            {"alpha-module": ["Modules/alpha-module/**"]},
            "manifest:ownership0001",
        )
        self.auth = self.intent_clock.verify(
            {
                "context_id": "authctx:impact0001",
                "subject": "factory-module-implementer",
                "role": "module-implementer",
                "audience": "dps.factory-upgrade-intake",
                "issued_at": "2026-07-15T00:00:00Z",
                "expires_at": "2026-07-15T03:00:00Z",
                "nonce": "nonce_" + "1" * 32,
                "receipt_id": "auth:impact-receipt0001",
                "approvals": [],
                "verification_material": {"fixture": "signed"},
            }
        )
        self.intent_value = self._build_intent()
        self.intent_raw = INTAKE.encode_upgrade_intent_v2(
            self.intent_value,
            self.auth,
            self.ownership,
            self.intent_clock,
            self.manifest_authority,
        )

        self.resolver_clock = RESOLVER.TrustedUtcClock.fixed_for_tests(
            "2026-07-15T01:00:00Z"
        )
        self.resolver_port = RESOLVER.UpgradeIntentVerifierPort(
            b"resolver-impact-pipeline-test-key-32-bytes", self.resolver_clock
        )
        self.resolver_intent_authority = RESOLVER.UpgradeIntentTrustAuthority(
            self.resolver_port
        )
        payload_sha = hashlib.sha256(self.intent_raw).hexdigest()
        resolver_attestation = self.resolver_port.create_process_bound_attestation(
            self.intent_raw,
            trust_receipt_id="trust:" + payload_sha[:32],
            trust_nonce="nonce_" + payload_sha[:32],
            issued_at="2026-07-15T00:30:00Z",
            expires_at="2026-07-15T02:00:00Z",
            requester_auth_expires_at="2026-07-15T03:00:00Z",
            manifest_ownership_expires_at="2026-07-15T03:00:00Z",
            approval_expires_at=None,
        )
        self.resolver_intent = self.resolver_intent_authority.verify_and_seal(
            self.intent_raw, resolver_attestation
        )
        self.resolver = RESOLVER.InstructionResolver(
            self.root, trust_authority=self.resolver_intent_authority
        )
        self.resolver_receipt = self.resolver.resolve(
            self.resolver_intent,
            agent_identity="factory-module-implementer",
            agent_role="module-implementer",
        )
        ok, reason, current = self.resolver.validate(
            self.resolver_receipt, self.resolver_intent
        )
        if not ok or reason != "BOUND" or current is None:
            raise AssertionError("real Resolver did not produce a current BOUND receipt")
        self.resolver_receipt = current
        self.receipt_value = self.resolver_receipt.canonical_receipt()
        self.receipt_raw = canonical_bytes(self.receipt_value)

        self.clock = IMPACT.TrustedUtcClock.fixed_for_tests("2026-07-15T01:00:00Z")
        self.intent_port = IMPACT.IntentVerifierPort(
            b"impact-intent-local-process-key-32-bytes", self.clock
        )
        self.receipt_port = IMPACT.ReceiptVerifierPort(
            b"impact-receipt-local-process-key-32-bytes", self.clock
        )
        self.policy_port = IMPACT.ImpactPolicyVerifierPort(
            b"impact-policy-local-process-key-32-bytes", self.clock
        )
        self.intent_authority = IMPACT.IntentTrustAuthority(self.intent_port)
        self.receipt_authority = IMPACT.ReceiptTrustAuthority(self.receipt_port)
        self.policy_authority = IMPACT.ImpactPolicyTrustAuthority(self.policy_port)
        self.intent_capability = self._seal_intent()
        self.receipt_capability = self._seal_receipt(
            self.receipt_value,
            source_capability=self._resolver_receipt_metadata(self.resolver_receipt),
        )
        self.policy_raw = (
            MODULE_ROOT / "operations/trusted-impact-policy.v2.json"
        ).read_bytes()
        self.policy_attestation = self.policy_port.create_process_bound_attestation(
            self.policy_raw,
            trust_receipt_id="policy:impact-shadow0001",
            trust_nonce="nonce_" + "9" * 32,
            issued_at="2026-07-15T00:30:00Z",
            expires_at="2026-07-15T02:00:00Z",
        )
        self.policy_capability = self.policy_authority.verify_and_seal(
            self.policy_raw, self.policy_attestation
        )
        self.analyzer = IMPACT.ImpactAnalyzer(
            self.root,
            intent_authority=self.intent_authority,
            receipt_authority=self.receipt_authority,
            policy_authority=self.policy_authority,
        )

    def close(self):
        self.temp.cleanup()

    @staticmethod
    def _provided(contract_id, major, mode, status="proposed"):
        return {
            "contractId": contract_id,
            "major": major,
            "mode": mode,
            "status": status,
        }

    @staticmethod
    def _consumed(contract_id, major, owner, mode, status="proposed"):
        return {
            "contractId": contract_id,
            "major": major,
            "ownerModule": owner,
            "mode": mode,
            "status": status,
        }

    @staticmethod
    def _edge(peer, contract_id, major, direction):
        return {
            "peerModule": peer,
            "contractId": contract_id,
            "major": major,
            "direction": direction,
            "transport": "event",
            "timeoutMs": 5000,
            "retryPolicy": "same-idempotency-key",
            "idempotencyKey": "event_id",
            "authScope": "module:test",
            "failureMode": "fail-closed",
        }

    def _write(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def _git(self, *args):
        return subprocess.check_output(["/usr/bin/git", *args], cwd=self.root, text=True).strip()

    def _source(self, contract_id, major):
        return "Modules/alpha-module/contracts/provided/%s.v%d.schema.json" % (
            contract_id, major
        )

    def _schema(self, module_id, contract_id, major, revision="baseline"):
        return json.dumps(
            {
                "title": "%s/v%d" % (contract_id, major),
                "revision": revision,
                "properties": {"producer_module": {"const": module_id}},
            },
            sort_keys=True,
        )

    def _module(self, module_id, provides=None, consumes=None, dependencies=None):
        root = "Modules/" + module_id
        self._write(
            root + "/AGENTS.md",
            "manifest contract compatibility tests canary rollout rollback communication",
        )
        provided_items = []
        for item in copy.deepcopy(provides or []):
            source = (
                "%s/contracts/provided/%s.v%d.schema.json"
                % (root, item["contractId"], item["major"])
            )
            self._write(
                source,
                self._schema(module_id, item["contractId"], item["major"]),
            )
            provided_items.append(
                {
                    "contractId": item["contractId"], "major": item["major"],
                    "source": source, "status": item["status"], "mode": item["mode"],
                    "ownerModule": module_id,
                }
            )
        consumed_items = []
        for item in copy.deepcopy(consumes or []):
            consumed_items.append(
                {
                    "contractId": item["contractId"], "major": item["major"],
                    "source": "Modules/%s/contracts/provided/%s.v%d.schema.json"
                    % (item["ownerModule"], item["contractId"], item["major"]),
                    "status": item["status"], "mode": item["mode"],
                    "ownerModule": item["ownerModule"],
                }
            )
        manifest = {
            "module": {"id": module_id, "riskTier": "R1"},
            "paths": {"owned": [root + "/**"]},
            "contracts": {"provided": provided_items, "consumed": consumed_items},
            "communication": {"inbound": [], "outbound": []},
            "dependencies": [
                {
                    "moduleId": dependency, "versionRange": ">=0.1.0",
                    "required": True, "reason": "fixture",
                }
                for dependency in (dependencies or [])
            ],
        }
        self._write(root + "/module.yaml", json.dumps(manifest, indent=2, sort_keys=True))
        self._write(root + "/src/domain.py", "baseline = True\n")
        self._write(root + "/tests/README.md", "tests")
        self._write(root + "/operations/README.md", "rollback")

    def _manifest(self, module_id):
        return json.loads(
            (self.root / ("Modules/%s/module.yaml" % module_id)).read_text()
        )

    def _write_manifest(self, module_id, manifest):
        self._write(
            "Modules/%s/module.yaml" % module_id,
            json.dumps(manifest, indent=2, sort_keys=True),
        )

    def _set_edge_pair(self, provider, consumer, contract_id, major):
        provider_manifest = self._manifest(provider)
        consumer_manifest = self._manifest(consumer)
        provider_manifest["communication"]["outbound"] = [
            self._edge(consumer, contract_id, major, "outbound")
        ]
        consumer_manifest["communication"]["inbound"] = [
            self._edge(provider, contract_id, major, "inbound")
        ]
        self._write_manifest(provider, provider_manifest)
        self._write_manifest(consumer, consumer_manifest)

    def _materialize_change(self, kind):
        self._write("Modules/alpha-module/src/domain.py", "candidate = True\n")
        alpha = self._manifest("alpha-module")
        consumer = self._manifest("exact-consumer")
        if kind in {"add-major", "introduce-quarantined-major"}:
            mode = "active" if kind == "add-major" else "quarantine-only"
            status = "proposed" if kind == "add-major" else "deprecated"
            source = self._source("alpha.event", 2)
            self._write(source, self._schema("alpha-module", "alpha.event", 2, "candidate"))
            alpha["contracts"]["provided"].append(
                {
                    "contractId": "alpha.event", "major": 2, "source": source,
                    "status": status, "mode": mode, "ownerModule": "alpha-module",
                }
            )
        elif kind == "additive-schema":
            self._write(
                self._source("alpha.event", 1),
                self._schema("alpha-module", "alpha.event", 1, "additive"),
            )
        elif kind == "mode-transition":
            alpha["contracts"]["provided"][0]["mode"] = "quarantine-only"
            alpha["contracts"]["provided"][0]["status"] = "deprecated"
            consumer["contracts"]["consumed"][0]["mode"] = "quarantine-only"
            consumer["contracts"]["consumed"][0]["status"] = "deprecated"
            alpha["communication"]["outbound"] = []
            consumer["communication"]["inbound"] = []
        else:
            raise AssertionError("unknown fixture change kind")
        self._write_manifest("alpha-module", alpha)
        self._write_manifest("exact-consumer", consumer)

    def _baseline_bytes(self, path):
        return subprocess.check_output(
            ["/usr/bin/git", "show", self.baseline + ":" + path], cwd=self.root
        )

    def _change(self):
        kind = self.change_kind
        major = 2 if kind in {"add-major", "introduce-quarantined-major"} else 1
        source = self._source("alpha.event", major)
        previous_sha = None
        previous_mode = None
        if kind in {"additive-schema", "mode-transition"}:
            previous_sha = hashlib.sha256(self._baseline_bytes(source)).hexdigest()
            previous_mode = "active"
        current_sha = hashlib.sha256((self.root / source).read_bytes()).hexdigest()
        if kind == "add-major":
            mode, status, presence = "active", "proposed", "absent"
        elif kind == "additive-schema":
            mode, status, presence = "active", "proposed", "present"
        elif kind == "mode-transition":
            mode, status, presence = "quarantine-only", "deprecated", "present"
            current_sha = previous_sha
        else:
            mode, status, presence = "quarantine-only", "deprecated", "absent"
        value = {
            "contract_id": "alpha.event", "major": major,
            "baseline_commit": self.baseline, "expected_mode": mode,
            "expected_status": status, "expected_baseline_state": presence,
            "change_kind": kind, "expected_owner_module": "alpha-module",
            "expected_source": source, "expected_source_sha256": current_sha,
            "expected_previous_mode": previous_mode,
            "expected_previous_source_sha256": previous_sha,
            "quarantine_reason": None, "quarantine_evidence_sha256": None,
        }
        if kind == "introduce-quarantined-major":
            value["quarantine_reason"] = "historical-wire-import-no-baseline-major"
            value["quarantine_evidence_sha256"] = (
                INTAKE.quarantine_import_evidence_sha256(value)
            )
        return value

    def _build_intent(self):
        change = self._change()
        intent = {
            "schema_version": "dps.upgrade-intent/v2",
            "contract_id": "upgrade.intent/v2",
            "producer_module": "factory-upgrade-intake",
            "soul_id": None, "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "2" * 32,
            "idempotency_key": "idem_" + "3" * 64,
            "occurred_at": "2026-07-15T00:50:00Z",
            "privacy_class": "internal",
            "intent_id": "intent:impact0001",
            "auth_context_id": self.auth.context_id,
            "requester_auth_context_sha256": self.auth.requester_context_sha256,
            "requester_auth_receipt_id": self.auth.receipt_id,
            "requester_auth_nonce": self.auth.nonce,
            "baseline_commit": self.baseline,
            "manifest_ownership_sha256": self.ownership.snapshot_sha256,
            "manifest_ownership_receipt_id": self.ownership.receipt_id,
            "target_modules": ["alpha-module"],
            "requested_paths": sorted(
                ["Modules/alpha-module/src/domain.py", change["expected_source"]]
            ),
            "public_contract_changes": [change],
            "public_contract_changes_sha256": "0" * 64,
            "contract_change_claims_status": "UNVERIFIED_EXPECTATIONS",
            "baseline_verification_required": True,
            "approval_subject_sha256": "0" * 64,
            "upgrade_intent_sha256": "0" * 64,
            "requested_risk_tier": "R1",
            "requested_stage": self.stage,
            "requester": {
                "identity": self.auth.subject, "role": self.auth.role,
            },
            "authorization": {
                "status": "not-required", "approved_by": None,
                "approver_role": "not-applicable", "approval_scope": [],
                "approval_receipt_id": None, "approval_nonce": None,
                "approved_at": None, "approval_expires_at": None,
            },
        }
        intent["public_contract_changes_sha256"] = (
            INTAKE.public_contract_changes_sha256(
                intent["public_contract_changes"], intent["target_modules"],
                self.ownership, self.baseline, self.manifest_authority,
            )
        )
        intent["approval_subject_sha256"] = INTAKE.approval_subject_sha256(intent)
        intent["upgrade_intent_sha256"] = INTAKE.upgrade_intent_sha256(intent)
        return intent

    @staticmethod
    def _resolver_intent_metadata(capability):
        return {
            "payload_sha256": capability.payload_sha256,
            "upgrade_intent_sha256": capability.upgrade_intent_sha256,
            "producer_module": capability.producer_module,
            "contract_id": capability.contract_id,
            "major": capability.major,
            "peer_module": capability.peer_module,
            "audience": capability.audience,
            "trust_receipt_id": capability.trust_receipt_id,
            "trust_nonce": capability.trust_nonce,
            "issued_at": capability.issued_at,
            "verified_at": capability.verified_at,
            "expires_at": capability.expires_at,
            "requester_auth_expires_at": capability.requester_auth_expires_at,
            "manifest_ownership_expires_at": capability.manifest_ownership_expires_at,
            "approval_expires_at": capability.approval_expires_at,
        }

    @staticmethod
    def _resolver_receipt_metadata(capability):
        return {
            "receipt_sha256": capability.receipt_sha256,
            "receipt_id": capability.receipt_id,
            "producer_module": capability.producer_module,
            "contract_id": capability.contract_id,
            "major": capability.major,
            "issuer": capability.issuer,
            "audience": capability.audience,
            "issued_at": capability.issued_at,
            "expires_at": capability.expires_at,
            "nonce": capability.nonce,
            "generation": capability.generation,
            "status": capability.status,
        }

    def _seal_intent(self):
        attestation = self.intent_port.create_process_bound_attestation(
            self.intent_raw,
            source_capability=self._resolver_intent_metadata(self.resolver_intent),
            trust_receipt_id="impact-intent:" + hashlib.sha256(self.intent_raw).hexdigest()[:32],
            trust_nonce="nonce_" + hashlib.sha256(self.intent_raw).hexdigest()[:32],
            issued_at="2026-07-15T01:00:00Z",
            expires_at="2026-07-15T01:50:00Z",
        )
        return self.intent_authority.verify_and_seal(self.intent_raw, attestation)

    def _seal_receipt(self, value, *, source_capability, trust_receipt_id=None):
        raw = canonical_bytes(value)
        digest = hashlib.sha256(raw).hexdigest()
        attestation = self.receipt_port.create_process_bound_attestation(
            raw,
            source_capability=source_capability,
            trust_receipt_id=trust_receipt_id or "impact-receipt:" + digest[:32],
            trust_nonce="nonce_" + digest[:32],
            issued_at="2026-07-15T01:00:00Z",
            expires_at="2026-07-15T01:50:00Z",
        )
        return self.receipt_authority.verify_and_seal(raw, attestation)

    def forged_receipt_capability(self, mutation):
        value = copy.deepcopy(self.receipt_value)
        mutation(value)
        without_id = dict(value)
        without_id.pop("receipt_id")
        value["receipt_id"] = "instruction:" + hashlib.sha256(
            canonical_bytes(without_id)
        ).hexdigest()[:32]
        raw = canonical_bytes(value)
        digest = hashlib.sha256(raw).hexdigest()
        source = self._resolver_receipt_metadata(self.resolver_receipt)
        source.update(
            {
                "receipt_sha256": digest,
                "receipt_id": value["receipt_id"],
                "nonce": "nonce_" + digest[:32],
                "generation": source["generation"] + 100,
            }
        )
        return self._seal_receipt(value, source_capability=source)

    def analyze(self):
        return self.analyzer.analyze(
            self.intent_capability, self.receipt_capability, self.policy_capability
        )


class ImpactAnalyzerV2Tests(unittest.TestCase):
    def test_real_intake_resolver_impact_pipeline_covers_four_change_kinds(self):
        for kind in (
            "add-major", "additive-schema", "mode-transition",
            "introduce-quarantined-major",
        ):
            with self.subTest(kind=kind):
                fixture = PipelineFixture(kind)
                try:
                    plan = fixture.analyze()
                    self.assertEqual("module.change.plan/v2", plan["contract_id"])
                    self.assertEqual(["alpha-module"], plan["write_modules"])
                    self.assertEqual("UNVERIFIED_EXPECTATIONS", plan[
                        "source_contract_change_claims_status"
                    ])
                    self.assertTrue(plan["changeset_contract_verification_required"])
                    self.assertEqual("WAITING_EXTERNAL", plan["portable_trust_status"])
                    self.assertFalse(plan["release_eligible"])
                    self.assertFalse(plan["side_effects_authorized"])
                    self.assertEqual(0, plan["shadow_side_effect_count"])
                    self.assertEqual(
                        fixture.receipt_value["scope"], plan["instruction_scope"]
                    )
                    if kind == "add-major":
                        self.assertNotIn("exact-consumer", plan["instruction_scope"])
                    if kind in {"additive-schema", "mode-transition"}:
                        self.assertIn("exact-consumer", plan["instruction_scope"])
                    if kind == "introduce-quarantined-major":
                        self.assertEqual([], plan["dependency_edges"])
                finally:
                    fixture.close()

    def test_same_input_is_byte_stable_and_plan_hashes_bind_full_plan(self):
        fixture = PipelineFixture("additive-schema")
        try:
            first = fixture.analyze()
            second = fixture.analyze()
            self.assertEqual(canonical_bytes(first), canonical_bytes(second))
            without_hash = dict(first)
            supplied_hash = without_hash.pop("plan_sha256")
            self.assertEqual(hashlib.sha256(canonical_bytes(without_hash)).hexdigest(), supplied_hash)
            without_id = dict(without_hash)
            supplied_id = without_id.pop("plan_id")
            self.assertEqual(
                "change:" + hashlib.sha256(canonical_bytes(without_id)).hexdigest()[:32],
                supplied_id,
            )
            self.assertEqual(
                hashlib.sha256(
                    canonical_bytes(first["instruction_scope"])
                ).hexdigest(),
                first["instruction_scope_sha256"],
            )
            self.assertEqual(
                hashlib.sha256(
                    canonical_bytes(first["authorized_write_paths"])
                ).hexdigest(),
                first["authorized_write_paths_sha256"],
            )
            self.assertEqual(
                hashlib.sha256(canonical_bytes({
                    "write_modules": first["write_modules"],
                    "authorized_write_paths": first["authorized_write_paths"],
                })).hexdigest(),
                first["write_scope_sha256"],
            )
        finally:
            fixture.close()

    def test_multi_change_intent_uses_upstream_contract_tuple_order(self):
        fixture = PipelineFixture("additive-schema")
        try:
            intent = copy.deepcopy(fixture.intent_value)
            first = intent["public_contract_changes"][0]
            second = {
                "contract_id": "zeta.event",
                "major": 2,
                "baseline_commit": fixture.baseline,
                "expected_mode": "active",
                "expected_status": "proposed",
                "expected_baseline_state": "absent",
                "change_kind": "add-major",
                "expected_owner_module": "alpha-module",
                "expected_source": fixture._source("zeta.event", 2),
                "expected_source_sha256": "a" * 64,
                "expected_previous_mode": None,
                "expected_previous_source_sha256": None,
                "quarantine_reason": None,
                "quarantine_evidence_sha256": None,
            }
            intent["public_contract_changes"] = sorted(
                [second, first], key=INTAKE._contract_change_sort_key
            )
            intent["requested_paths"] = sorted(
                intent["requested_paths"] + [second["expected_source"]]
            )
            intent["public_contract_changes_sha256"] = (
                INTAKE.public_contract_changes_sha256(
                    intent["public_contract_changes"],
                    intent["target_modules"],
                    fixture.ownership,
                    fixture.baseline,
                    fixture.manifest_authority,
                )
            )
            intent["approval_subject_sha256"] = INTAKE.approval_subject_sha256(intent)
            intent["upgrade_intent_sha256"] = INTAKE.upgrade_intent_sha256(intent)

            self.assertNotEqual(
                intent["public_contract_changes"],
                sorted(intent["public_contract_changes"], key=canonical_bytes),
            )
            decoded = IMPACT._decode_intent(canonical_bytes(intent))
            self.assertEqual(
                ["alpha.event", "zeta.event"],
                [item["contract_id"] for item in decoded["public_contract_changes"]],
            )
        finally:
            fixture.close()

    def test_plain_mapping_lambda_direct_constructor_and_cross_authority_fail(self):
        fixture = PipelineFixture("additive-schema")
        try:
            with self.assertRaisesRegex(IMPACT.ImpactError, "exact process-bound"):
                fixture.analyzer.analyze(
                    fixture.intent_value,
                    fixture.receipt_capability,
                    fixture.policy_capability,
                )
            with self.assertRaises(IMPACT.ImpactError):
                IMPACT.IntentTrustAuthority(lambda *_args: True)
            with self.assertRaises(IMPACT.ImpactError):
                IMPACT.ReceiptTrustAuthority(lambda *_args: True)
            with self.assertRaises(IMPACT.ImpactError):
                IMPACT.ImpactPolicyTrustAuthority(lambda *_args: True)
            with self.assertRaises((IMPACT.ImpactError, TypeError, KeyError)):
                IMPACT.VerifiedIntentV2(
                    _issuer=object(), _issuer_token=object(), values={}
                )
            other = IMPACT.IntentTrustAuthority(fixture.intent_port)
            foreign_analyzer = IMPACT.ImpactAnalyzer(
                fixture.root,
                intent_authority=other,
                receipt_authority=fixture.receipt_authority,
                policy_authority=fixture.policy_authority,
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "not issued"):
                foreign_analyzer.analyze(
                    fixture.intent_capability,
                    fixture.receipt_capability,
                    fixture.policy_capability,
                )
        finally:
            fixture.close()

    def test_raw_swap_same_receipt_different_bytes_and_expiry_equality_fail(self):
        fixture = PipelineFixture("additive-schema")
        try:
            original = fixture.intent_capability.raw_bytes
            object.__setattr__(fixture.intent_capability, "raw_bytes", original + b" ")
            with self.assertRaisesRegex(IMPACT.ImpactError, "not issued"):
                fixture.analyze()
            object.__setattr__(fixture.intent_capability, "raw_bytes", original)

            policy = json.loads(fixture.policy_raw)
            policy["policy_id"] = "factory-impact-shadow-2026-07-15-replay"
            raw = canonical_bytes(policy)
            attestation = fixture.policy_port.create_process_bound_attestation(
                raw,
                trust_receipt_id="policy:impact-shadow0001",
                trust_nonce="nonce_" + "8" * 32,
                issued_at="2026-07-15T00:30:00Z",
                expires_at="2026-07-15T02:00:00Z",
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "replayed"):
                fixture.policy_authority.verify_and_seal(raw, attestation)

            nonce_replay = fixture.policy_port.create_process_bound_attestation(
                raw,
                trust_receipt_id="policy:impact-shadow0002",
                trust_nonce=fixture.policy_attestation["trust_nonce"],
                issued_at="2026-07-15T00:30:00Z",
                expires_at="2026-07-15T02:00:00Z",
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "nonce replayed"):
                fixture.policy_authority.verify_and_seal(raw, nonce_replay)

            intent_source = fixture._resolver_intent_metadata(
                fixture.resolver_intent
            )
            intent_source["verified_at"] = "2026-07-15T00:59:59Z"
            intent_source_replay = fixture.intent_port.create_process_bound_attestation(
                fixture.intent_raw,
                source_capability=intent_source,
                trust_receipt_id="impact-intent:source-replay0001",
                trust_nonce="nonce_" + "4" * 32,
                issued_at="2026-07-15T01:00:00Z",
                expires_at="2026-07-15T01:50:00Z",
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "source Intent"):
                fixture.intent_authority.verify_and_seal(
                    fixture.intent_raw, intent_source_replay
                )

            receipt_source = fixture._resolver_receipt_metadata(
                fixture.resolver_receipt
            )
            receipt_source["generation"] += 1
            receipt_source_replay = (
                fixture.receipt_port.create_process_bound_attestation(
                    fixture.receipt_raw,
                    source_capability=receipt_source,
                    trust_receipt_id="impact-receipt:source-replay0001",
                    trust_nonce="nonce_" + "5" * 32,
                    issued_at="2026-07-15T01:00:00Z",
                    expires_at="2026-07-15T01:50:00Z",
                )
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "source Receipt"):
                fixture.receipt_authority.verify_and_seal(
                    fixture.receipt_raw, receipt_source_replay
                )

            fixture.clock.advance_for_tests("2026-07-15T01:50:00Z")
            with self.assertRaisesRegex(IMPACT.ImpactError, "expired"):
                fixture.intent_authority.assert_issued(fixture.intent_capability)
        finally:
            fixture.close()

    def test_wrong_audience_producer_and_major_fail_even_with_valid_local_mac(self):
        fixture = PipelineFixture("additive-schema")
        try:
            source = fixture._resolver_intent_metadata(fixture.resolver_intent)
            base = fixture.intent_port.create_process_bound_attestation(
                fixture.intent_raw,
                source_capability=source,
                trust_receipt_id="impact-intent:wrong-binding0001",
                trust_nonce="nonce_" + "6" * 32,
                issued_at="2026-07-15T01:00:00Z",
                expires_at="2026-07-15T01:50:00Z",
            )
            key = b"impact-intent-local-process-key-32-bytes"
            for field, value in (
                ("audience", "dps.attacker"),
                ("producer_module", "attacker"),
                ("major", 3),
            ):
                with self.subTest(field=field):
                    attacked = copy.deepcopy(base)
                    attacked[field] = value
                    material = {
                        name: attacked[name]
                        for name in sorted(
                            IMPACT._INTENT_ATTESTATION_FIELDS - {"verification_mac"}
                        )
                    }
                    attacked["verification_mac"] = IMPACT._attestation_mac(
                        key,
                        "dps.factory-impact-analyzer.intent-trust/v2",
                        material,
                    )
                    with self.assertRaisesRegex(IMPACT.ImpactError, "binding mismatch"):
                        fixture.intent_authority.verify_and_seal(
                            fixture.intent_raw, attacked
                        )
            wrong_full_digest = fixture._resolver_intent_metadata(
                fixture.resolver_intent
            )
            wrong_full_digest["upgrade_intent_sha256"] = "f" * 64
            with self.assertRaisesRegex(IMPACT.ImpactError, "full digest mismatch"):
                fixture.intent_port.create_process_bound_attestation(
                    fixture.intent_raw,
                    source_capability=wrong_full_digest,
                    trust_receipt_id="impact-intent:wrong-source-digest0001",
                    trust_nonce="nonce_" + "c" * 32,
                    issued_at="2026-07-15T01:00:00Z",
                    expires_at="2026-07-15T01:50:00Z",
                )
        finally:
            fixture.close()

    def test_local_attestation_cannot_predate_its_source_capability(self):
        fixture = PipelineFixture("additive-schema")
        try:
            intent_attestation = fixture.intent_port.create_process_bound_attestation(
                fixture.intent_raw,
                source_capability=fixture._resolver_intent_metadata(
                    fixture.resolver_intent
                ),
                trust_receipt_id="impact-intent:early-local0001",
                trust_nonce="nonce_" + "a" * 32,
                issued_at="2026-07-15T00:59:59Z",
                expires_at="2026-07-15T01:50:00Z",
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "expired or inconsistent"):
                fixture.intent_authority.verify_and_seal(
                    fixture.intent_raw, intent_attestation
                )

            receipt_attestation = fixture.receipt_port.create_process_bound_attestation(
                fixture.receipt_raw,
                source_capability=fixture._resolver_receipt_metadata(
                    fixture.resolver_receipt
                ),
                trust_receipt_id="impact-receipt:early-local0001",
                trust_nonce="nonce_" + "b" * 32,
                issued_at="2026-07-15T00:59:59Z",
                expires_at="2026-07-15T01:50:00Z",
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "expired or inconsistent"):
                fixture.receipt_authority.verify_and_seal(
                    fixture.receipt_raw, receipt_attestation
                )
        finally:
            fixture.close()

    def test_scope_too_few_and_too_many_are_rejected_not_subset_accepted(self):
        fixture = PipelineFixture("additive-schema")
        try:
            too_few = fixture.forged_receipt_capability(
                lambda value: value.__setitem__("scope", ["alpha-module"])
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "exactly equal"):
                fixture.analyzer.analyze(
                    fixture.intent_capability, too_few, fixture.policy_capability
                )
            too_many = fixture.forged_receipt_capability(
                lambda value: value.__setitem__(
                    "scope", sorted(value["scope"] + ["factory-upgrade-intake"])
                )
            )
            with self.assertRaisesRegex(IMPACT.ImpactError, "exactly equal"):
                fixture.analyzer.analyze(
                    fixture.intent_capability, too_many, fixture.policy_capability
                )
        finally:
            fixture.close()

    def test_repository_toctou_and_index_only_drift_fail_currentness(self):
        fixture = PipelineFixture("additive-schema")
        try:
            real_snapshot = IMPACT._state_snapshot
            calls = {"count": 0}

            def racing_snapshot(root, baseline, receipt):
                result = real_snapshot(root, baseline, receipt)
                calls["count"] += 1
                if calls["count"] == 1:
                    fixture._write("Modules/alpha-module/src/race.py", "race = True\n")
                return result

            with mock.patch.object(IMPACT, "_state_snapshot", side_effect=racing_snapshot):
                with self.assertRaisesRegex(IMPACT.ImpactError, "fingerprint is stale"):
                    fixture.analyze()
        finally:
            fixture.close()

        fixture = PipelineFixture("additive-schema")
        try:
            fixture._git("add", "Modules/alpha-module/src/domain.py")
            with self.assertRaisesRegex(IMPACT.ImpactError, "fingerprint is stale"):
                fixture.analyze()
        finally:
            fixture.close()

    def test_head_and_capability_expiry_are_rechecked_before_return(self):
        fixture = PipelineFixture("additive-schema")
        try:
            fixture._git("commit", "--allow-empty", "-qm", "unexpected head drift")
            with self.assertRaisesRegex(IMPACT.ImpactError, "HEAD moved"):
                fixture.analyze()
        finally:
            fixture.close()

        fixture = PipelineFixture("additive-schema")
        try:
            real_snapshot = IMPACT._state_snapshot
            calls = {"count": 0}

            def expiring_snapshot(root, baseline, receipt):
                result = real_snapshot(root, baseline, receipt)
                calls["count"] += 1
                if calls["count"] == 2:
                    fixture.clock.advance_for_tests("2026-07-15T02:00:00Z")
                return result

            with mock.patch.object(
                IMPACT, "_state_snapshot", side_effect=expiring_snapshot
            ):
                with self.assertRaisesRegex(IMPACT.ImpactError, "expired"):
                    fixture.analyze()
        finally:
            fixture.close()

    def test_shadow_has_zero_side_effects_and_template_cannot_authorize_production(self):
        fixture = PipelineFixture("additive-schema", stage="shadow")
        try:
            plan = fixture.analyze()
            self.assertEqual("shadow", plan["planned_stage"])
            self.assertIn("shadow.no-side-effects", plan["required_checks"])
            self.assertIn("shadow.determinism", plan["required_checks"])
            self.assertFalse(plan["release_eligible"])
            self.assertFalse(plan["side_effects_authorized"])
            self.assertEqual(0, plan["shadow_side_effect_count"])
        finally:
            fixture.close()

        fixture = PipelineFixture("additive-schema")
        try:
            policy = json.loads(fixture.policy_raw)
            policy["policy_id"] = "x"
            with self.assertRaisesRegex(IMPACT.ImpactError, "policy id"):
                fixture.policy_port.create_process_bound_attestation(
                    canonical_bytes(policy),
                    trust_receipt_id="policy:short-id0001",
                    trust_nonce="nonce_" + "d" * 32,
                    issued_at="2026-07-15T00:30:00Z",
                    expires_at="2026-07-15T02:00:00Z",
                )

            policy = json.loads(fixture.policy_raw)
            policy["allowed_stages"].append("canary")
            policy["allowed_stages"].sort()
            # Even a correctly signed repository template cannot expand itself
            # into a production stage.
            with self.assertRaisesRegex(IMPACT.ImpactError, "stage boundary"):
                fixture.policy_port.create_process_bound_attestation(
                    canonical_bytes(policy),
                    trust_receipt_id="policy:production-escalation0001",
                    trust_nonce="nonce_" + "7" * 32,
                    issued_at="2026-07-15T00:30:00Z",
                    expires_at="2026-07-15T02:00:00Z",
                )
        finally:
            fixture.close()

    def test_policy_is_deeply_immutable_and_expectations_do_not_become_facts(self):
        fixture = PipelineFixture("mode-transition")
        try:
            with self.assertRaises(TypeError):
                fixture.policy_capability.roles["release_approver"] = ("attacker",)
            with self.assertRaises(TypeError):
                fixture.policy_capability.change_kind_risk_floor["mode-transition"] = "R0"
            with self.assertRaises(TypeError):
                fixture.policy_capability.trust_metadata["audience"] = "dps.attacker"
            for capability, authority in (
                (fixture.intent_capability, fixture.intent_authority),
                (fixture.receipt_capability, fixture.receipt_authority),
                (fixture.policy_capability, fixture.policy_authority),
            ):
                with self.subTest(capability=type(capability).__name__):
                    original = capability.trust_metadata
                    attacked = dict(original)
                    attacked["audience"] = "dps.attacker"
                    object.__setattr__(capability, "trust_metadata", attacked)
                    try:
                        with self.assertRaisesRegex(IMPACT.ImpactError, "not issued"):
                            authority.assert_issued(capability)
                    finally:
                        object.__setattr__(capability, "trust_metadata", original)
            plan = fixture.analyze()
            self.assertEqual("UNVERIFIED_EXPECTATIONS", plan[
                "source_contract_change_claims_status"
            ])
            self.assertTrue(plan["changeset_contract_verification_required"])
            self.assertEqual("R3", plan["effective_risk_tier"])
        finally:
            fixture.close()


if __name__ == "__main__":
    unittest.main()
