import copy
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


MODULE_ROOT = pathlib.Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "instruction_resolver", MODULE_ROOT / "src" / "instruction_resolver.py"
)
RESOLVER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = RESOLVER
SPEC.loader.exec_module(RESOLVER)


def canonical_bytes(value):
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")


def domain_sha256(domain, value):
    return hashlib.sha256(
        b"DPS\x00" + domain.encode("ascii") + b"\x00" + canonical_bytes(value)
    ).hexdigest()


def bind_intent_hashes(intent):
    intent["approval_subject_sha256"] = domain_sha256(
        "dps.upgrade-intent/v2/approval-subject",
        {
            key: value
            for key, value in intent.items()
            if key
            not in {
                "authorization",
                "approval_subject_sha256",
                "upgrade_intent_sha256",
            }
        },
    )
    intent["upgrade_intent_sha256"] = domain_sha256(
        "dps.upgrade-intent/v2/full-intent",
        {
            key: value
            for key, value in intent.items()
            if key != "upgrade_intent_sha256"
        },
    )
    return intent


class InstructionResolverTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self._write("AGENTS.md", "root instructions")
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
                self._provided("instruction.receipt", 1, "quarantine-only", "deprecated"),
                self._provided("instruction.receipt", 2, "active"),
            ],
            consumes=[
                self._consumed("upgrade.intent", 1, "factory-upgrade-intake", "quarantine-only", "deprecated"),
                self._consumed("upgrade.intent", 2, "factory-upgrade-intake", "active"),
            ],
            dependencies=["factory-upgrade-intake"],
        )
        self._module(
            "planner",
            provides=[
                self._provided("action.proposal", 1, "active"),
                self._provided("action.proposal", 2, "active"),
            ],
        )
        self._module(
            "policy-approval",
            consumes=[self._consumed("action.proposal", 1, "planner", "active")],
            dependencies=["planner"],
        )
        self._module(
            "v2-consumer",
            consumes=[self._consumed("action.proposal", 2, "planner", "active")],
            dependencies=["planner"],
        )
        self._module("beta", provides=[self._provided("beta.event", 1, "active")])

        self._git("init", "-q")
        self._git("config", "user.email", "factory@example.invalid")
        self._git("config", "user.name", "Factory Test")
        self._git("add", ".")
        self._git("commit", "-qm", "baseline")
        self.baseline = self._git("rev-parse", "HEAD")
        self._write("Modules/planner/src/domain.py", "changed = True\n")
        self.clock = RESOLVER.TrustedUtcClock.fixed_for_tests(
            "2026-07-15T01:00:00Z"
        )
        self.verifier_port = RESOLVER.UpgradeIntentVerifierPort(
            b"resolver-test-verification-key-32-bytes-minimum", self.clock
        )
        self.trust_authority = RESOLVER.UpgradeIntentTrustAuthority(
            self.verifier_port
        )
        self.resolver = RESOLVER.InstructionResolver(
            self.root, trust_authority=self.trust_authority
        )
        self.capabilities = {}

    def tearDown(self):
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
        return subprocess.check_output(
            ["/usr/bin/git", *args], cwd=self.root, text=True
        ).strip()

    def _contract_source(self, owner, contract_id, major):
        return f"Modules/{owner}/contracts/provided/{contract_id}.v{major}.schema.json"

    def _module(self, module_id, provides=None, consumes=None, dependencies=None):
        provides = copy.deepcopy(provides or [])
        consumes = copy.deepcopy(consumes or [])
        dependencies = dependencies or []
        root = f"Modules/{module_id}"
        self._write(
            f"{root}/AGENTS.md",
            "manifest contract compatibility tests canary rollout rollback communication",
        )
        provided_items = []
        for item in provides:
            source = self._contract_source(module_id, item["contractId"], item["major"])
            self._write(
                source,
                json.dumps(
                    {
                        "title": f"{item['contractId']}/v{item['major']}",
                        "properties": {
                            "producer_module": {"const": module_id}
                        },
                    }
                ),
            )
            provided_items.append(
                {
                    "contractId": item["contractId"],
                    "major": item["major"],
                    "source": source,
                    "status": item["status"],
                    "mode": item["mode"],
                    "ownerModule": module_id,
                }
            )
        consumed_items = []
        for item in consumes:
            consumed_items.append(
                {
                    "contractId": item["contractId"],
                    "major": item["major"],
                    "source": self._contract_source(
                        item["ownerModule"], item["contractId"], item["major"]
                    ),
                    "status": item["status"],
                    "mode": item["mode"],
                    "ownerModule": item["ownerModule"],
                }
            )
        manifest = {
            "module": {"id": module_id},
            "paths": {"owned": [f"{root}/**"]},
            "contracts": {"provided": provided_items, "consumed": consumed_items},
            "communication": {"inbound": [], "outbound": []},
            "dependencies": [
                {
                    "moduleId": item,
                    "versionRange": ">=0.1.0",
                    "required": True,
                    "reason": "test",
                }
                for item in dependencies
            ],
        }
        self._write(f"{root}/module.yaml", json.dumps(manifest, indent=2))
        self._write(f"{root}/src/domain.py", "baseline = True\n")
        self._write(f"{root}/tests/README.md", "tests")
        self._write(f"{root}/operations/README.md", "rollback")

    def _manifest(self, module_id):
        return json.loads(
            (self.root / f"Modules/{module_id}/module.yaml").read_text(encoding="utf-8")
        )

    def _write_manifest(self, module_id, manifest):
        self._write(
            f"Modules/{module_id}/module.yaml", json.dumps(manifest, indent=2)
        )

    def _source_sha(self, source):
        return hashlib.sha256((self.root / source).read_bytes()).hexdigest()

    def _baseline_source_sha(self, source):
        value = subprocess.check_output(
            ["/usr/bin/git", "show", f"{self.baseline}:{source}"], cwd=self.root
        )
        return hashlib.sha256(value).hexdigest()

    def _change(
        self,
        contract_id,
        major,
        mode,
        change_kind,
        owner="planner",
        previous_mode=None,
    ):
        source = self._contract_source(owner, contract_id, major)
        previous_sha = (
            None
            if change_kind in {"add-major", "introduce-quarantined-major"}
            else self._baseline_source_sha(source)
        )
        expected_sha = (
            self._source_sha(source) if (self.root / source).is_file() else "a" * 64
        )
        if change_kind == "introduce-quarantined-major":
            expected_status = "deprecated"
            baseline_state = "absent"
            quarantine_reason = "historical-wire-import-no-baseline-major"
        elif change_kind == "add-major":
            expected_status = "proposed"
            baseline_state = "absent"
            quarantine_reason = None
        elif change_kind == "mode-transition":
            expected_status = "retired" if mode == "retired" else "deprecated"
            baseline_state = "present"
            quarantine_reason = None
        else:
            expected_status = "proposed"
            baseline_state = "present"
            quarantine_reason = None
        value = {
            "contract_id": contract_id,
            "major": major,
            "baseline_commit": self.baseline,
            "expected_mode": mode,
            "expected_status": expected_status,
            "expected_baseline_state": baseline_state,
            "change_kind": change_kind,
            "expected_owner_module": owner,
            "expected_source": source,
            "expected_source_sha256": expected_sha,
            "expected_previous_mode": previous_mode,
            "expected_previous_source_sha256": previous_sha,
            "quarantine_reason": quarantine_reason,
            "quarantine_evidence_sha256": None,
        }
        if change_kind == "introduce-quarantined-major":
            value["quarantine_evidence_sha256"] = domain_sha256(
                "dps.upgrade-intent/v2/quarantine-import-evidence",
                {
                    "baseline_commit": self.baseline,
                    "contract_id": contract_id,
                    "major": major,
                    "expected_source": source,
                    "expected_source_sha256": expected_sha,
                    "quarantine_reason": quarantine_reason,
                },
            )
        return value

    def _intent(self, target="planner", changes=None):
        changes = copy.deepcopy(changes or [])
        changes.sort(
            key=lambda value: (
                value["contract_id"],
                value["major"],
                value["baseline_commit"],
                value["expected_mode"],
                value["expected_status"],
                value["expected_baseline_state"],
                value["change_kind"],
                value["expected_owner_module"],
                value["expected_source"],
                value["expected_source_sha256"],
                value["expected_previous_mode"] or "",
                value["expected_previous_source_sha256"] or "",
                value["quarantine_reason"] or "",
                value["quarantine_evidence_sha256"] or "",
            )
        )
        manifest_ownership_sha256 = "3" * 64
        requested_paths = sorted(
            {f"Modules/{target}/src/domain.py"}.union(
                value["expected_source"] for value in changes
            )
        )
        intent = {
            "schema_version": "dps.upgrade-intent/v2",
            "contract_id": "upgrade.intent/v2",
            "producer_module": "factory-upgrade-intake",
            "soul_id": None,
            "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32,
            "idempotency_key": "idem_" + "2" * 64,
            "occurred_at": "2026-07-15T00:00:00Z",
            "privacy_class": "internal",
            "intent_id": "intent:0002",
            "auth_context_id": "authctx:0002",
            "requester_auth_context_sha256": "4" * 64,
            "requester_auth_receipt_id": "auth:receipt-0002",
            "requester_auth_nonce": "nonce_" + "5" * 32,
            "baseline_commit": self.baseline,
            "manifest_ownership_sha256": manifest_ownership_sha256,
            "manifest_ownership_receipt_id": "manifest:ownership-0002",
            "target_modules": [target],
            "requested_paths": requested_paths,
            "public_contract_changes": changes,
            "public_contract_changes_sha256": domain_sha256(
                "dps.upgrade-intent/v2/public-contract-changes",
                {
                    "baseline_commit": self.baseline,
                    "manifest_ownership_sha256": manifest_ownership_sha256,
                    "public_contract_changes": changes,
                },
            ),
            "contract_change_claims_status": "UNVERIFIED_EXPECTATIONS",
            "baseline_verification_required": True,
            "approval_subject_sha256": "0" * 64,
            "upgrade_intent_sha256": "0" * 64,
            "requested_risk_tier": "R1",
            "requested_stage": "development",
            "requester": {
                "identity": "factory-module-implementer",
                "role": "module-implementer",
            },
            "authorization": {
                "status": "not-required",
                "approved_by": None,
                "approver_role": "not-applicable",
                "approval_scope": [],
                "approval_receipt_id": None,
                "approval_nonce": None,
                "approved_at": None,
                "approval_expires_at": None,
            },
        }
        return bind_intent_hashes(intent)

    def _capability(self, intent=None):
        intent = intent or self._intent()
        raw = canonical_bytes(intent)
        payload_sha = hashlib.sha256(raw).hexdigest()
        existing = self.capabilities.get(payload_sha)
        if existing is not None:
            return existing
        attestation = self.verifier_port.create_process_bound_attestation(
            raw,
            trust_receipt_id="trust:" + payload_sha[:32],
            trust_nonce="nonce_" + payload_sha[:32],
            issued_at="2026-07-15T00:30:00Z",
            expires_at="2026-07-15T01:30:00Z",
            requester_auth_expires_at="2026-07-15T02:00:00Z",
            manifest_ownership_expires_at="2026-07-15T02:00:00Z",
            approval_expires_at=intent["authorization"]["approval_expires_at"],
        )
        capability = self.trust_authority.verify_and_seal(raw, attestation)
        self.capabilities[payload_sha] = capability
        return capability

    def _resolve(self, intent=None):
        return self.resolver.resolve(
            self._capability(intent),
            agent_identity="factory-module-implementer",
            agent_role="module-implementer",
        )

    def _validate(self, receipt, intent=None):
        return self.resolver.validate(receipt, self._capability(intent))

    def test_exact_major_indexes_preserve_mode_source_owner_and_status(self):
        records = RESOLVER.load_module_records(self.root)
        planner = records["planner"]
        self.assertEqual(
            {("action.proposal", 1), ("action.proposal", 2)}, set(planner.provided)
        )
        declaration = planner.provided[("action.proposal", 1)]
        self.assertEqual("active", declaration.mode)
        self.assertEqual("planner", declaration.owner_module)
        self.assertEqual("proposed", declaration.status)
        self.assertTrue(declaration.source.endswith(".v1.schema.json"))

    def test_v1_change_binds_only_v1_policy_consumer(self):
        manifest = self._manifest("planner")
        manifest["contracts"]["provided"][0]["mode"] = "quarantine-only"
        manifest["contracts"]["provided"][0]["status"] = "deprecated"
        self._write_manifest("planner", manifest)
        change = self._change(
            "action.proposal", 1, "quarantine-only", "mode-transition", previous_mode="active"
        )
        receipt = self._resolve(self._intent(changes=[change]))
        self.assertEqual(["planner", "policy-approval"], receipt["scope"])
        self.assertNotIn("v2-consumer", receipt["scope"])
        self.assertIn(
            "Modules/policy-approval/AGENTS.md",
            [item["path"] for item in receipt["instructions"]],
        )

    def test_v2_change_binds_only_declared_v2_consumer(self):
        source = self._contract_source("planner", "action.proposal", 2)
        self._write(source, '{"title":"action.proposal/v2","additive":true}\n')
        change = self._change(
            "action.proposal", 2, "active", "additive-schema", previous_mode="active"
        )
        receipt = self._resolve(self._intent(changes=[change]))
        self.assertEqual(["planner", "v2-consumer"], receipt["scope"])
        self.assertNotIn("policy-approval", receipt["scope"])

    def test_v2_receipt_binds_canonical_contract_and_governance_facts(self):
        receipt = self._resolve()
        self.assertEqual("dps.instruction-receipt/v2", receipt["schema_version"])
        self.assertEqual("instruction.receipt/v2", receipt["contract_id"])
        self.assertEqual(
            {"contract_id": "upgrade.intent", "major": 2, "mode": "active"},
            receipt["source_intent_contract"],
        )
        self.assertEqual(
            "UNVERIFIED_EXPECTATIONS",
            receipt["source_contract_change_claims_status"],
        )
        self.assertEqual([], receipt["bound_contract_change_expectations"])
        self.assertEqual([], receipt["verified_baseline_contract_facts"])
        self.assertTrue(receipt["baseline_verification_required"])
        self.assertTrue(receipt["changeset_contract_verification_required"])
        self.assertEqual("AGENTS.md", receipt["instructions"][0]["path"])
        governance = [item["path"] for item in receipt["governance"]]
        self.assertIn("governance/modules/compatibility.yaml", governance)
        self.assertIn("governance/policies/compatibility-policy.yaml", governance)
        self.assertTrue(receipt["contract_declarations"])

    def test_same_inputs_and_timestamp_are_deterministic(self):
        self.assertEqual(self._resolve(), self._resolve())

    def test_full_intent_auth_and_scope_digest_are_bound_into_the_receipt(self):
        intent = self._intent()
        receipt = self._resolve(intent)
        self.assertEqual(intent["target_modules"], receipt["requested_target_modules"])
        self.assertEqual(intent["requested_paths"], receipt["authorized_write_paths"])
        self.assertEqual(
            hashlib.sha256(canonical_bytes(intent)).hexdigest(),
            receipt["source_intake_payload_sha256"],
        )
        self.assertEqual(
            "dps.factory-instruction-resolver",
            receipt["source_intake_audience"],
        )
        self.assertTrue(
            set(receipt["requested_target_modules"]).issubset(set(receipt["scope"]))
        )
        changed = self._intent()
        changed["requested_paths"].append("Modules/planner/src/alternate.py")
        changed["requested_paths"].sort()
        changed["requester_auth_context_sha256"] = "6" * 64
        bind_intent_hashes(changed)
        ok, reason, stale = self._validate(receipt, changed)
        self.assertFalse(ok)
        self.assertEqual("STALE", stale["status"])
        self.assertIn("bound content", reason)
        self.assertNotEqual(
            receipt["source_upgrade_intent_sha256"],
            changed["upgrade_intent_sha256"],
        )

        invalid = self._intent()
        invalid["upgrade_intent_sha256"] = "0" * 64
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "full upgrade intent"):
            self._resolve(invalid)

    def test_plain_mapping_and_replaceable_verifier_are_rejected(self):
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "not issued by this trust authority"
        ):
            self.resolver.resolve(
                self._intent(),
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
            )
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "not a callback"):
            RESOLVER.UpgradeIntentTrustAuthority(lambda *_args: True)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "fixed trust authority"):
            RESOLVER.VerifiedUpgradeIntentV2(
                _issuer=None, _issuer_token=None
            )

    def test_capability_cannot_cross_authorities_or_change_raw_bytes(self):
        capability = self._capability()
        other_clock = RESOLVER.TrustedUtcClock.fixed_for_tests(
            "2026-07-15T01:00:00Z"
        )
        other_port = RESOLVER.UpgradeIntentVerifierPort(b"z" * 32, other_clock)
        other_authority = RESOLVER.UpgradeIntentTrustAuthority(other_port)
        other_resolver = RESOLVER.InstructionResolver(
            self.root, trust_authority=other_authority
        )
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "not issued by this trust authority"
        ):
            other_resolver.resolve(
                capability,
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
            )

        replacement = self._intent()
        replacement["trace_id"] = "trace_" + "9" * 32
        bind_intent_hashes(replacement)
        object.__setattr__(capability, "raw_bytes", canonical_bytes(replacement))
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "not issued by this trust authority"
        ):
            self.resolver.resolve(
                capability,
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
            )

    def test_cached_receipt_never_bypasses_attestation_verification(self):
        intent = self._intent()
        raw = canonical_bytes(intent)
        payload_sha = hashlib.sha256(raw).hexdigest()
        values = {
            "trust_receipt_id": "trust:" + payload_sha[:32],
            "trust_nonce": "nonce_" + payload_sha[:32],
            "issued_at": "2026-07-15T00:30:00Z",
            "expires_at": "2026-07-15T01:30:00Z",
            "requester_auth_expires_at": "2026-07-15T02:00:00Z",
            "manifest_ownership_expires_at": "2026-07-15T02:00:00Z",
            "approval_expires_at": None,
        }
        attestation = self.verifier_port.create_process_bound_attestation(
            raw, **values
        )
        capability = self.trust_authority.verify_and_seal(raw, attestation)
        self.assertIs(
            capability,
            self.trust_authority.verify_and_seal(raw, dict(attestation)),
        )

        with self.assertRaisesRegex(RESOLVER.ResolutionError, "invalid shape"):
            self.trust_authority.verify_and_seal(
                raw, {"trust_receipt_id": values["trust_receipt_id"]}
            )

        rebound_values = dict(values)
        rebound_values["trust_nonce"] = "nonce_" + "7" * 32
        rebound = self.verifier_port.create_process_bound_attestation(
            raw, **rebound_values
        )
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "different authority binding"
        ):
            self.trust_authority.verify_and_seal(raw, rebound)

    def test_trust_auth_manifest_and_approval_expiry_are_strict(self):
        intent = self._intent()
        raw = canonical_bytes(intent)
        for field in (
            "expires_at",
            "requester_auth_expires_at",
            "manifest_ownership_expires_at",
        ):
            values = {
                "trust_receipt_id": "trust:" + field.replace("_", "-") + "0001",
                "trust_nonce": "nonce_" + hashlib.sha256(field.encode()).hexdigest()[:32],
                "issued_at": "2026-07-15T00:30:00Z",
                "expires_at": "2026-07-15T01:30:00Z",
                "requester_auth_expires_at": "2026-07-15T02:00:00Z",
                "manifest_ownership_expires_at": "2026-07-15T02:00:00Z",
                "approval_expires_at": None,
            }
            values[field] = "2026-07-15T01:00:00Z"
            attestation = self.verifier_port.create_process_bound_attestation(
                raw, **values
            )
            with self.subTest(field=field), self.assertRaisesRegex(
                RESOLVER.ResolutionError, "expired"
            ):
                self.trust_authority.verify_and_seal(raw, attestation)

        approved = self._intent()
        approved["requested_risk_tier"] = "R3"
        approved["requested_stage"] = "canary"
        approved["authorization"] = {
            "status": "approved",
            "approved_by": "human-approver",
            "approver_role": "human-release-approver",
            "approval_scope": ["canary"],
            "approval_receipt_id": "approval:receipt-0002",
            "approval_nonce": "nonce_" + "8" * 32,
            "approved_at": "2026-07-15T00:30:00Z",
            "approval_expires_at": "2026-07-15T01:00:00Z",
        }
        bind_intent_hashes(approved)
        approved_raw = canonical_bytes(approved)
        attestation = self.verifier_port.create_process_bound_attestation(
            approved_raw,
            trust_receipt_id="trust:approval-expiry-0002",
            trust_nonce="nonce_" + "9" * 32,
            issued_at="2026-07-15T00:30:00Z",
            expires_at="2026-07-15T01:30:00Z",
            requester_auth_expires_at="2026-07-15T02:00:00Z",
            manifest_ownership_expires_at="2026-07-15T02:00:00Z",
            approval_expires_at="2026-07-15T01:00:00Z",
        )
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "expired"):
            self.trust_authority.verify_and_seal(approved_raw, attestation)

    def test_verifier_rejects_noncanonical_duplicate_and_bom_wire(self):
        intent = self._intent()
        attacks = [
            json.dumps(intent, indent=2, sort_keys=True).encode("utf-8"),
            b"\xef\xbb\xbf" + canonical_bytes(intent),
            canonical_bytes(intent)[:-1]
            + b',"schema_version":"dps.upgrade-intent/v2"}',
        ]
        for raw in attacks:
            with self.subTest(raw=raw[:12]), self.assertRaises(
                RESOLVER.ResolutionError
            ):
                self.verifier_port.create_process_bound_attestation(
                    raw,
                    trust_receipt_id="trust:bad-wire-0002",
                    trust_nonce="nonce_" + "a" * 32,
                    issued_at="2026-07-15T00:30:00Z",
                    expires_at="2026-07-15T01:30:00Z",
                    requester_auth_expires_at="2026-07-15T02:00:00Z",
                    manifest_ownership_expires_at="2026-07-15T02:00:00Z",
                    approval_expires_at=None,
                )

        unroutable = self._intent()
        unroutable["authorization"]["status"] = "pending"
        bind_intent_hashes(unroutable)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "not routable"):
            self._resolve(unroutable)

    def test_future_add_major_expectation_is_bound_but_only_baseline_absence_is_verified(self):
        change = self._change(
            "future.event", 1, "active", "add-major", owner="planner"
        )
        receipt = self._resolve(self._intent(changes=[change]))
        self.assertEqual([change], receipt["bound_contract_change_expectations"])
        self.assertEqual(
            "UNVERIFIED_EXPECTATIONS",
            receipt["source_contract_change_claims_status"],
        )
        self.assertNotIn("verified_contract_changes", receipt)
        self.assertEqual(
            [
                {
                    "contract_id": "future.event",
                    "major": 1,
                    "baseline_commit": self.baseline,
                    "presence": "absent",
                    "owner_module": None,
                    "source": None,
                    "source_sha256": None,
                    "mode": None,
                    "status": None,
                    "family_owner_module": None,
                    "consumer_modules": [],
                }
            ],
            receipt["verified_baseline_contract_facts"],
        )

    def test_historical_major_can_only_be_introduced_quarantined_from_absent_baseline(self):
        change = self._change(
            "historical.wire",
            1,
            "quarantine-only",
            "introduce-quarantined-major",
            owner="planner",
        )
        receipt = self._resolve(self._intent(changes=[change]))
        self.assertEqual([change], receipt["bound_contract_change_expectations"])
        self.assertEqual(
            "absent", receipt["verified_baseline_contract_facts"][0]["presence"]
        )

        forged = copy.deepcopy(change)
        forged["change_kind"] = "mode-transition"
        forged["expected_baseline_state"] = "present"
        forged["expected_previous_mode"] = "active"
        forged["expected_previous_source_sha256"] = forged[
            "expected_source_sha256"
        ]
        forged["quarantine_reason"] = None
        forged["quarantine_evidence_sha256"] = None
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "baseline state is false"):
            self._resolve(self._intent(changes=[forged]))

    def test_quarantined_introduction_rejects_any_runtime_communication_edge(self):
        source = self._contract_source("planner", "historical.wire", 1)
        self._write(source, '{"title":"historical.wire/v1"}\n')
        manifest = self._manifest("planner")
        manifest["contracts"]["provided"].append(
            {
                "contractId": "historical.wire",
                "major": 1,
                "source": source,
                "status": "deprecated",
                "mode": "quarantine-only",
                "ownerModule": "planner",
            }
        )
        manifest["communication"]["outbound"].append(
            self._edge("policy-approval", "historical.wire", 1, "outbound")
        )
        self._write_manifest("planner", manifest)
        change = self._change(
            "historical.wire",
            1,
            "quarantine-only",
            "introduce-quarantined-major",
            owner="planner",
        )
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "runtime communication"):
            self._resolve(self._intent(changes=[change]))

    def test_quarantined_introduction_rejects_an_active_current_producer(self):
        source = self._contract_source("planner", "historical.wire", 1)
        self._write(source, '{"title":"historical.wire/v1"}\n')
        manifest = self._manifest("planner")
        manifest["contracts"]["provided"].append(
            {
                "contractId": "historical.wire",
                "major": 1,
                "source": source,
                "status": "proposed",
                "mode": "active",
                "ownerModule": "planner",
            }
        )
        self._write_manifest("planner", manifest)
        change = self._change(
            "historical.wire",
            1,
            "quarantine-only",
            "introduce-quarantined-major",
            owner="planner",
        )
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "active or mismatched producer"
        ):
            self._resolve(self._intent(changes=[change]))

    def test_quarantined_introduction_rejects_active_current_consumer_without_edge(self):
        source = self._contract_source("planner", "historical.wire", 1)
        self._write(source, '{"title":"historical.wire/v1"}\n')
        provider = self._manifest("planner")
        provider["contracts"]["provided"].append(
            {
                "contractId": "historical.wire",
                "major": 1,
                "source": source,
                "status": "deprecated",
                "mode": "quarantine-only",
                "ownerModule": "planner",
            }
        )
        self._write_manifest("planner", provider)
        consumer = self._manifest("policy-approval")
        consumer["contracts"]["consumed"].append(
            {
                "contractId": "historical.wire",
                "major": 1,
                "source": source,
                "status": "proposed",
                "mode": "active",
                "ownerModule": "planner",
            }
        )
        self._write_manifest("policy-approval", consumer)
        change = self._change(
            "historical.wire",
            1,
            "quarantine-only",
            "introduce-quarantined-major",
            owner="planner",
        )
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "active or mismatched consumers"
        ):
            self._resolve(self._intent(changes=[change]))

    def test_hidden_exact_major_communication_consumer_fails_closed(self):
        provider = self._manifest("planner")
        provider["communication"]["outbound"].append(
            self._edge("v2-consumer", "action.proposal", 2, "outbound")
        )
        self._write_manifest("planner", provider)
        hidden_consumer = self._manifest("v2-consumer")
        hidden_consumer["contracts"]["consumed"] = []
        hidden_consumer["communication"]["inbound"].append(
            self._edge("planner", "action.proposal", 2, "inbound")
        )
        self._write_manifest("v2-consumer", hidden_consumer)
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "undeclared exact contract"
        ):
            self._resolve(self._intent())

    def test_one_sided_exact_major_communication_fails_closed(self):
        provider = self._manifest("planner")
        provider["communication"]["outbound"].append(
            self._edge("v2-consumer", "action.proposal", 2, "outbound")
        )
        self._write_manifest("planner", provider)
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "lacks reciprocal exact-major"
        ):
            self._resolve(self._intent())

    def test_reciprocal_communication_semantics_mismatch_fails_closed(self):
        provider = self._manifest("planner")
        provider["communication"]["outbound"].append(
            self._edge("v2-consumer", "action.proposal", 2, "outbound")
        )
        self._write_manifest("planner", provider)
        consumer = self._manifest("v2-consumer")
        inbound = self._edge("planner", "action.proposal", 2, "inbound")
        inbound["timeoutMs"] = 6000
        consumer["communication"]["inbound"].append(inbound)
        self._write_manifest("v2-consumer", consumer)
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "reciprocal communication semantics mismatch"
        ):
            self._resolve(self._intent())

    def test_duplicate_exact_major_communication_fails_closed(self):
        provider = self._manifest("planner")
        edge = self._edge("v2-consumer", "action.proposal", 2, "outbound")
        provider["communication"]["outbound"].extend([edge, copy.deepcopy(edge)])
        self._write_manifest("planner", provider)
        consumer = self._manifest("v2-consumer")
        consumer["communication"]["inbound"].append(
            self._edge("planner", "action.proposal", 2, "inbound")
        )
        self._write_manifest("v2-consumer", consumer)
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "duplicate exact communication"
        ):
            self._resolve(self._intent())

    def test_reversed_schema_producer_communication_fails_closed(self):
        provider = self._manifest("planner")
        provider["communication"]["inbound"].append(
            self._edge("v2-consumer", "action.proposal", 2, "inbound")
        )
        self._write_manifest("planner", provider)
        consumer = self._manifest("v2-consumer")
        consumer["communication"]["outbound"].append(
            self._edge("planner", "action.proposal", 2, "outbound")
        )
        self._write_manifest("v2-consumer", consumer)
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "direction conflicts with exact Schema producer"
        ):
            self._resolve(self._intent())

    def test_preserve_producer_on_inbound_or_real_producer_fails_closed(self):
        provider = self._manifest("planner")
        outbound = self._edge(
            "v2-consumer", "action.proposal", 2, "outbound"
        )
        outbound["preserveProducer"] = True
        provider["communication"]["outbound"].append(outbound)
        self._write_manifest("planner", provider)
        consumer = self._manifest("v2-consumer")
        consumer["communication"]["inbound"].append(
            self._edge("planner", "action.proposal", 2, "inbound")
        )
        self._write_manifest("v2-consumer", consumer)
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "preserveProducer is relay-only"
        ):
            self._resolve(self._intent())

    def test_missing_and_unknown_mode_fail_closed(self):
        manifest = self._manifest("planner")
        del manifest["contracts"]["provided"][0]["mode"]
        self._write_manifest("planner", manifest)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "frozen Manifest shape"):
            RESOLVER.load_module_records(self.root)

        manifest["contracts"]["provided"][0]["mode"] = "pretend-active"
        self._write_manifest("planner", manifest)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "compatibility mode"):
            RESOLVER.load_module_records(self.root)

    def test_provided_compat_read_fails_closed(self):
        manifest = self._manifest("planner")
        manifest["contracts"]["provided"][0]["mode"] = "compat-read"
        self._write_manifest("planner", manifest)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "cannot use compat-read"):
            RESOLVER.load_module_records(self.root)

        manifest["contracts"]["provided"][0]["mode"] = "active"
        self._write_manifest("planner", manifest)
        change = self._change(
            "action.proposal",
            1,
            "compat-read",
            "mode-transition",
            previous_mode="active",
        )
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "invalid mode"):
            self._resolve(self._intent(changes=[change]))

    def test_duplicate_exact_major_fails_closed(self):
        manifest = self._manifest("planner")
        manifest["contracts"]["provided"].append(
            copy.deepcopy(manifest["contracts"]["provided"][0])
        )
        self._write_manifest("planner", manifest)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "duplicate provided"):
            RESOLVER.load_module_records(self.root)

    def test_unknown_consumer_major_fails_closed(self):
        manifest = self._manifest("policy-approval")
        item = manifest["contracts"]["consumed"][0]
        item["major"] = 3
        item["source"] = self._contract_source("planner", "action.proposal", 3)
        self._write(item["source"], "{}")
        self._write_manifest("policy-approval", manifest)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "no exact owner"):
            RESOLVER.load_module_records(self.root)

    def test_v1_intent_and_receipt_are_quarantine_only(self):
        intent = self._intent()
        intent["schema_version"] = "dps.upgrade-intent/v1"
        intent["contract_id"] = "upgrade.intent/v1"
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "quarantine-only"):
            self._resolve(intent)

        receipt = self._resolve().canonical_receipt()
        receipt["schema_version"] = "dps.instruction-receipt/v1"
        receipt["contract_id"] = "instruction.receipt/v1"
        ok, reason, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertIn("issued instruction receipt capability", reason)
        self.assertIsNone(stale)

    def test_nonbound_or_unrecomputable_receipt_never_fabricates_stale(self):
        receipt = self._resolve().canonical_receipt()
        receipt["status"] = "STALE"
        ok, reason, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertIn("issued instruction receipt capability", reason)
        self.assertIsNone(stale)

        receipt = self._resolve()
        (self.root / "Modules/planner/AGENTS.md").unlink()
        ok, reason, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertIn("AGENTS", reason)
        self.assertIsNone(stale)

    def test_duplicate_public_change_identity_fails_even_if_equal(self):
        source = self._contract_source("planner", "action.proposal", 2)
        self._write(source, '{"title":"action.proposal/v2","additive":true}\n')
        change = self._change(
            "action.proposal", 2, "active", "additive-schema", previous_mode="active"
        )
        intent = self._intent(changes=[change, change])
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "duplicate public"):
            self._resolve(intent)

    def test_multiple_public_changes_must_use_the_exact_canonical_order(self):
        changes = []
        for major in (1, 2):
            source = self._contract_source("planner", "action.proposal", major)
            self._write(
                source,
                '{"title":"action.proposal/v%d","additive":true}\n' % major,
            )
            changes.append(
                self._change(
                    "action.proposal",
                    major,
                    "active",
                    "additive-schema",
                    previous_mode="active",
                )
            )
        intent = self._intent(changes=changes)
        intent["public_contract_changes"].reverse()
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "canonical order"):
            self._resolve(intent)

    def test_change_digest_and_baseline_facts_fail_closed(self):
        source = self._contract_source("planner", "action.proposal", 2)
        self._write(source, '{"title":"action.proposal/v2","additive":true}\n')
        change = self._change(
            "action.proposal", 2, "active", "additive-schema", previous_mode="active"
        )
        change["expected_previous_source_sha256"] = "f" * 64
        intent = self._intent(changes=[change])
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "baseline facts"):
            self._resolve(intent)

        intent = self._intent()
        intent["public_contract_changes_sha256"] = "0" * 64
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "sha256 mismatch"):
            self._resolve(intent)

        invalid_transition = self._change(
            "action.proposal",
            1,
            "quarantine-only",
            "mode-transition",
            previous_mode="active",
        )
        invalid_transition["expected_status"] = "active"
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "mode-transition"):
            self._resolve(self._intent(changes=[invalid_transition]))

        invalid_additive = self._change(
            "action.proposal",
            1,
            "active",
            "additive-schema",
            previous_mode="active",
        )
        invalid_additive["expected_status"] = "retired"
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "retired status"):
            self._resolve(self._intent(changes=[invalid_additive]))

    def test_bound_file_change_and_diff_expansion_make_receipt_stale(self):
        receipt = self._resolve()
        self._write("Modules/planner/AGENTS.md", "changed instructions")
        ok, reason, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertEqual("STALE", stale["status"])
        self.assertIn("changed", reason)

        receipt = self._resolve()
        self._write("Modules/planner/src/extra.py", "new = True\n")
        ok, _, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertEqual("STALE", stale["status"])

    def test_index_only_mutation_changes_diff_fingerprint(self):
        receipt = self._resolve()
        before_bytes = (
            self.root / "Modules/planner/src/domain.py"
        ).read_bytes()
        self._git("add", "Modules/planner/src/domain.py")
        self.assertEqual(
            before_bytes,
            (self.root / "Modules/planner/src/domain.py").read_bytes(),
        )
        ok, reason, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertEqual("STALE", stale["status"])
        self.assertIn("bound content or diff scope changed", reason)

    def test_callers_cannot_override_or_truncate_the_git_diff(self):
        receipt = self._resolve()
        self._write("Modules/planner/src/omitted.py", "hidden = True\n")
        with self.assertRaises(TypeError):
            self.resolver.validate(
                receipt,
                self._capability(),
                changed_paths=["Modules/planner/src/domain.py"],
            )
        with self.assertRaises(TypeError):
            self.resolver.resolve(
                self._capability(),
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
                changed_paths=["Modules/planner/src/domain.py"],
            )

        ok, _, stale = self._validate(receipt)
        self.assertFalse(ok)
        self.assertEqual("STALE", stale["status"])

    def test_binding_fails_when_diff_expands_during_snapshot(self):
        initial = RESOLVER._changed_paths(self.root, self.baseline)
        extra = "Modules/planner/src/race.py"
        calls = 0

        def racing_diff(root, baseline):
            nonlocal calls
            calls += 1
            if calls == 1:
                return initial
            self._write(extra, "raced = True\n")
            return tuple(sorted(set(initial).union({extra})))

        with mock.patch.object(RESOLVER, "_changed_paths", side_effect=racing_diff):
            with self.assertRaisesRegex(
                RESOLVER.ResolutionError, "diff changed during instruction binding"
            ):
                self._resolve()

    def test_binding_fails_when_a_bound_file_changes_during_snapshot(self):
        original = RESOLVER._bound_file
        mutated = False

        def racing_bound(root, baseline, relative_path, order):
            nonlocal mutated
            result = original(root, baseline, relative_path, order)
            if relative_path == "AGENTS.md" and not mutated:
                mutated = True
                self._write("AGENTS.md", "changed after first bound read")
            return result

        with mock.patch.object(RESOLVER, "_bound_file", side_effect=racing_bound):
            with self.assertRaisesRegex(
                RESOLVER.ResolutionError, "changed during instruction binding"
            ):
                self._resolve()

    def test_duplicate_extra_and_canonical_byte_tamper_receipts_fail(self):
        for mutate in (
            lambda value: value["manifests"].append(copy.deepcopy(value["manifests"][0])),
            lambda value: value.__setitem__("implementation_passed", True),
            lambda value: value["instructions"].reverse(),
            lambda value: value["instructions"][0].__setitem__("sha256", "0" * 64),
            lambda value: value["authorized_write_paths"].append(
                "Modules/planner/src/not-requested.py"
            ),
            lambda value: value["authorized_write_paths"].pop(),
            lambda value: value["requested_target_modules"].append("beta"),
        ):
            receipt = self._resolve().canonical_receipt()
            mutate(receipt)
            ok, reason, stale = self._validate(receipt)
            self.assertFalse(ok)
            self.assertIsNone(stale)
            self.assertIn("issued instruction receipt capability", reason)

    def test_wrong_contract_ownership_traversal_and_symlink_fail_closed(self):
        change = self._change(
            "action.proposal", 1, "active", "additive-schema", previous_mode="active"
        )
        change["expected_source"] = (
            "Modules/beta/contracts/provided/unknown.event.v1.schema.json"
        )
        change["expected_source_sha256"] = "f" * 64
        intent = self._intent(changes=[change])
        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "requested paths.*target modules"
        ):
            self._resolve(intent)

        intent = self._intent()
        intent["requested_paths"] = ["Modules/planner/../beta/src/domain.py"]
        with self.assertRaises(RESOLVER.ResolutionError):
            self._resolve(intent)

        outside = self.root.parent / "outside-factory-test"
        outside.write_text("outside", encoding="utf-8")
        link = self.root / "Modules/planner/src/link.py"
        link.symlink_to(outside)
        try:
            with self.assertRaisesRegex(RESOLVER.ResolutionError, "symlink"):
                self._resolve()
        finally:
            outside.unlink(missing_ok=True)

    def test_identity_must_use_canonical_prefix_and_opaque_charset(self):
        intent = self._intent()
        intent["device_binding_id"] = "db_illegal.dot"
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "identity"):
            self._resolve(intent)

    def test_receipt_currentness_requires_the_issuing_resolver_capability(self):
        capability = self._resolve()
        ok, reason, current = self._validate(capability)
        self.assertTrue(ok, reason)
        self.assertIs(capability, current)

        forged = capability.canonical_receipt()
        forged["agent_identity"] = "attacker"
        forged["scope"] = sorted(set(forged["scope"]) | {"beta"})
        material = dict(forged)
        material.pop("receipt_id")
        forged["receipt_id"] = "instruction:" + hashlib.sha256(
            canonical_bytes(material)
        ).hexdigest()[:32]
        ok, reason, derived = self._validate(forged)
        self.assertFalse(ok)
        self.assertIn("issued instruction receipt capability", reason)
        self.assertIsNone(derived)

        with self.assertRaisesRegex(
            RESOLVER.ResolutionError, "fixed receipt authority"
        ):
            RESOLVER.VerifiedInstructionReceiptV2(
                _issuer=None, _issuer_token=None
            )

        other_resolver = RESOLVER.InstructionResolver(
            self.root, trust_authority=self.trust_authority
        )
        ok, reason, derived = other_resolver.validate(
            capability, self._capability()
        )
        self.assertFalse(ok)
        self.assertIn("not issued by this Resolver authority", reason)
        self.assertIsNone(derived)

        for field, replacement in (
            ("receipt_sha256", "0" * 64),
            ("receipt_id", "instruction:" + "0" * 32),
            ("producer_module", "factory-upgrade-intake"),
            ("contract_id", "instruction.receipt.invalid"),
            ("major", 3),
            ("issuer", "attacker"),
            ("audience", "attacker"),
            ("issued_at", "2026-07-15T00:59:59Z"),
            ("expires_at", "2026-07-15T02:00:00Z"),
            ("nonce", "nonce_" + "0" * 32),
            ("generation", capability.generation + 1),
            ("status", "STALE"),
        ):
            original = getattr(capability, field)
            object.__setattr__(capability, field, replacement)
            try:
                ok, reason, derived = self._validate(capability)
                self.assertFalse(ok, field)
                self.assertIn("not issued by this Resolver authority", reason)
                self.assertIsNone(derived)
            finally:
                object.__setattr__(capability, field, original)

        swapped = capability.canonical_receipt()
        swapped["agent_identity"] = "swapped-attacker"
        object.__setattr__(capability, "raw_bytes", canonical_bytes(swapped))
        ok, reason, derived = self._validate(capability)
        self.assertFalse(ok)
        self.assertIn("not issued by this Resolver authority", reason)
        self.assertIsNone(derived)

    def test_trust_state_has_quota_prunes_expiry_and_rejects_old_replay(self):
        clock = RESOLVER.TrustedUtcClock.fixed_for_tests(
            "2026-07-15T01:00:00Z"
        )
        verifier = RESOLVER.UpgradeIntentVerifierPort(
            b"bounded-trust-verifier-key-32-bytes", clock,
            max_active_records=1,
        )
        authority = RESOLVER.UpgradeIntentTrustAuthority(
            verifier, max_active_capabilities=1
        )
        first_intent = self._intent()
        first_raw = canonical_bytes(first_intent)
        first_attestation = verifier.create_process_bound_attestation(
            first_raw,
            trust_receipt_id="trust:bounded-first-0002",
            trust_nonce="nonce_" + "1" * 32,
            issued_at="2026-07-15T00:59:00Z",
            expires_at="2026-07-15T01:05:00Z",
            requester_auth_expires_at="2026-07-15T01:05:00Z",
            manifest_ownership_expires_at="2026-07-15T01:05:00Z",
            approval_expires_at=None,
        )
        first = authority.verify_and_seal(first_raw, first_attestation)

        second_intent = self._intent()
        second_intent["intent_id"] = "intent:bounded-second-0002"
        second_intent["trace_id"] = "trace_" + "8" * 32
        bind_intent_hashes(second_intent)
        second_raw = canonical_bytes(second_intent)
        second_attestation = verifier.create_process_bound_attestation(
            second_raw,
            trust_receipt_id="trust:bounded-second-0002",
            trust_nonce="nonce_" + "2" * 32,
            issued_at="2026-07-15T01:00:00Z",
            expires_at="2026-07-15T01:20:00Z",
            requester_auth_expires_at="2026-07-15T01:20:00Z",
            manifest_ownership_expires_at="2026-07-15T01:20:00Z",
            approval_expires_at=None,
        )
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "quota"):
            authority.verify_and_seal(second_raw, second_attestation)

        clock.advance_for_tests("2026-07-15T01:05:00Z")
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "expired"):
            authority.verify_and_seal(first_raw, first_attestation)
        second = authority.verify_and_seal(second_raw, second_attestation)
        self.assertEqual(second_raw, second.raw_bytes)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "not issued"):
            authority.assert_issued(first)

    def test_receipt_capability_quota_prunes_at_expiry_equality(self):
        resolver = RESOLVER.InstructionResolver(
            self.root,
            trust_authority=self.trust_authority,
            receipt_capability_quota=1,
        )
        first_intent_capability = self._capability()
        first_receipt = resolver.resolve(
            first_intent_capability,
            agent_identity="factory-module-implementer",
            agent_role="module-implementer",
        )
        second_intent = self._intent()
        second_intent["intent_id"] = "intent:receipt-quota-0002"
        second_intent["trace_id"] = "trace_" + "6" * 32
        bind_intent_hashes(second_intent)
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "quota"):
            resolver.resolve(
                self._capability(second_intent),
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
            )

        self.clock.advance_for_tests("2026-07-15T01:30:00Z")
        ok, reason, derived = resolver.validate(
            first_receipt, first_intent_capability
        )
        self.assertFalse(ok)
        self.assertIn("not issued by this Resolver authority", reason)
        self.assertIsNone(derived)

        third_intent = self._intent()
        third_intent["intent_id"] = "intent:after-expiry-0002"
        third_intent["trace_id"] = "trace_" + "7" * 32
        bind_intent_hashes(third_intent)
        third_raw = canonical_bytes(third_intent)
        third_sha = hashlib.sha256(third_raw).hexdigest()
        third_intent_capability = self.trust_authority.verify_and_seal(
            third_raw,
            self.verifier_port.create_process_bound_attestation(
                third_raw,
                trust_receipt_id="trust:" + third_sha[:32],
                trust_nonce="nonce_" + third_sha[:32],
                issued_at="2026-07-15T01:30:00Z",
                expires_at="2026-07-15T02:30:00Z",
                requester_auth_expires_at="2026-07-15T03:00:00Z",
                manifest_ownership_expires_at="2026-07-15T03:00:00Z",
                approval_expires_at=None,
            ),
        )
        replacement = resolver.resolve(
            third_intent_capability,
            agent_identity="factory-module-implementer",
            agent_role="module-implementer",
        )
        self.assertEqual(2, replacement.generation)

    def test_receipt_agent_identity_and_time_are_not_caller_controlled(self):
        with self.assertRaisesRegex(RESOLVER.ResolutionError, "agent identity"):
            self.resolver.resolve(
                self._capability(),
                agent_identity="BAD SPACE",
                agent_role="module-implementer",
            )
        with self.assertRaises(TypeError):
            self.resolver.resolve(
                self._capability(),
                agent_identity="factory-module-implementer",
                agent_role="module-implementer",
                resolved_at="not-date",
            )


if __name__ == "__main__":
    unittest.main()
