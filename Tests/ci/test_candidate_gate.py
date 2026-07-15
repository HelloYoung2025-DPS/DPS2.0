import copy
import importlib.util
import json
import hashlib
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "dps_candidate_gate_tests_subject", ROOT / "Tools/ci/run_candidate_gate.py"
)
SUBJECT = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = SUBJECT
SPEC.loader.exec_module(SUBJECT)


class CandidateGateHardeningTests(unittest.TestCase):
    def _contract_inventory_slice(self, count=1):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "CONTRACT_VERIFIED", policy
        )
        return policy, SUBJECT.replace(inventory, suites=inventory.suites[:count])

    def _write_candidate_inventory_module(
        self,
        root,
        module_id,
        *,
        provided,
        suites,
        create_contract_sources=True,
    ):
        module_root = root / "Modules" / module_id
        module_root.mkdir(parents=True)
        (module_root / "AGENTS.md").write_text(
            "# Synthetic candidate inventory instructions\n", encoding="utf-8"
        )
        manifest = {
            "module": {"id": module_id, "lifecycle": "active"},
            "contracts": {"provided": provided, "consumed": []},
            "tests": {"suites": suites},
        }
        (module_root / "module.yaml").write_text(
            json.dumps(manifest, sort_keys=True) + "\n", encoding="utf-8"
        )
        if create_contract_sources:
            for declaration in provided:
                source = declaration.get("source")
                if not isinstance(source, str):
                    continue
                source_path = root / source
                source_path.parent.mkdir(parents=True, exist_ok=True)
                source_path.write_text("{}\n", encoding="utf-8")
        return module_root

    def _discover_public_contract_mode_fixture(
        self, root, target_declaration, *, create_target_source=True
    ):
        baseline_suite = {
            "id": "baseline.contract",
            "type": "contract",
            "required": True,
            "evidenceLevel": "CONTRACT_VERIFIED",
            "command": "python -m unittest baseline",
        }
        baseline_root = self._write_candidate_inventory_module(
            root,
            "baseline",
            provided=[
                {
                    "contractId": "baseline.event",
                    "major": 1,
                    "source": "Modules/baseline/contracts/provided/baseline.v1.schema.json",
                    "status": "active",
                    "mode": "active",
                    "ownerModule": "baseline",
                }
            ],
            suites=[baseline_suite],
        )
        target_root = self._write_candidate_inventory_module(
            root,
            "target",
            provided=[target_declaration],
            suites=[
                {
                    "id": "target.unit",
                    "type": "unit",
                    "required": True,
                    "evidenceLevel": "REPOSITORY_STATIC_VERIFIED",
                    "command": "python -m unittest target",
                }
            ],
            create_contract_sources=create_target_source,
        )
        policy = SUBJECT.CandidatePolicy(
            contract={
                ("baseline", "baseline.contract"): SUBJECT.ContractPolicy(
                    module_id="baseline",
                    suite_id="baseline.contract",
                    test_target="Modules/baseline/tests/test_baseline.py",
                    test_category="Contract",
                    minimum_executed_tests=1,
                )
            },
            integration={},
            sha256=SUBJECT.sha256_text("synthetic candidate policy"),
        )
        with mock.patch.object(
            SUBJECT,
            "discover_registered_module_dirs",
            return_value=[baseline_root, target_root],
        ):
            return SUBJECT.discover_candidate_inventory(
                root, "CONTRACT_VERIFIED", policy
            )

    def _synthetic_phase0_payload(
        self,
        *,
        required=True,
        check_status="PASS",
        receipt_status="BOUND",
        formal=False,
    ):
        baseline = "a" * 40
        head = "b" * 40
        workspace = "c" * 64
        log = "fixture"
        check = {
            "id": "clean-checkout-evidence-boundary",
            "required": required,
            "status": check_status,
            "exit_code": 0 if check_status == "PASS" else 1,
            "log": log,
            "log_sha256": SUBJECT.sha256_text(log),
            "details": {
                "clean": formal,
                "diagnostic_workspace": not formal,
                "formal_evidence_eligible": formal,
            },
        }
        statuses = [check_status]
        payload = {
            "schema_version": "dps.phase0-evidence-bundle/v1",
            "gate": (
                "REPOSITORY_STATIC_VERIFIED"
                if formal
                else "WORKSPACE_DIAGNOSTIC_ONLY"
            ),
            "verification_level": (
                "REPOSITORY_STATIC_VERIFIED" if formal else None
            ),
            "overall_status": "PASS",
            "commit_sha": head if formal else None,
            "head_commit_observed": head,
            "baseline_commit": baseline,
            "workspace_sha256": workspace,
            "environment": {
                "evidence_mode": (
                    "REPOSITORY_STATIC_VERIFIED"
                    if formal
                    else "WORKSPACE_DIAGNOSTIC_ONLY"
                ),
                "workspace_clean": formal,
            },
            "instruction_receipt": {
                "status": receipt_status,
                "baseline_commit": baseline,
                "receipt_id": "instruction:fixture-0001",
            },
            "checks": [check],
            "test_evidence": [],
            "summary": {
                "total": 1,
                "required": 1 if required else 0,
                "passed": sum(value == "PASS" for value in statuses),
                "failed": sum(value == "FAIL" for value in statuses),
                "skipped": 0,
                "partial": 0,
                "not_run": 0,
                "infra_error": 0,
                "not_applicable": 0,
            },
        }
        payload["evidence_sha256"] = SUBJECT.sha256_text(
            SUBJECT.stable_json(payload)
        )
        return payload, baseline, head, workspace

    def test_candidate_level_is_schema_locked_to_null(self):
        schema = json.loads(
            (ROOT / SUBJECT.EVIDENCE_SCHEMA_PATH).read_text(encoding="utf-8")
        )
        self.assertEqual(
            {"type": "null"}, schema["properties"]["candidate_verification_level"]
        )

    def test_policy_binds_all_eligible_suites_and_keeps_static_contracts_blocked(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        contract = SUBJECT.discover_candidate_inventory(
            ROOT, "CONTRACT_VERIFIED", policy
        )
        integration = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        contract_keys = {
            (value.module_id, str(value.suite["id"]))
            for value in contract.suites
        }
        integration_keys = {
            (value.module_id, str(value.suite["id"]))
            for value in integration.suites
            if value.suite.get("type") == "integration"
        }
        self.assertEqual(set(policy.contract), contract_keys)
        self.assertEqual(set(policy.integration), integration_keys)
        static_contract_errors = (
            "factory-upgrade-intake.contract: evidenceLevel must be exactly "
            "CONTRACT_VERIFIED",
            "factory-upgrade-intake: public contract owner lacks required Contract suite",
        )
        self.assertEqual(static_contract_errors, contract.errors)
        self.assertEqual(
            ("factory-upgrade-intake",),
            contract.modules_without_contract,
        )
        self.assertEqual(
            static_contract_errors
            + tuple(
                module_id
                + ": minimumVerification requires a required Integration suite"
                for module_id in integration.modules_without_integration
            ),
            integration.errors,
        )
        self.assertEqual(
            len(policy.contract) + len(policy.integration),
            len(integration.suites),
        )
        self.assertEqual(
            len(contract.public_contract_inventory),
            contract.public_contract_count,
        )

    def test_only_active_mode_enters_candidate_public_contract_inventory(self):
        for mode in ("quarantine-only", "compat-read", "retired"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                inventory = self._discover_public_contract_mode_fixture(
                    root,
                    {
                        "contractId": "target.event",
                        "major": 1,
                        "source": "Modules/target/contracts/provided/missing.v1.schema.json",
                        "status": "retired" if mode == "retired" else "active",
                        "mode": mode,
                        "ownerModule": "target",
                    },
                    create_target_source=False,
                )
                self.assertEqual((), inventory.errors)
                self.assertEqual(1, inventory.public_contract_owner_count)
                self.assertEqual(1, inventory.public_contract_count)
                self.assertEqual(
                    ["baseline"],
                    [
                        value["owner_module"]
                        for value in inventory.public_contract_inventory
                    ],
                )
                self.assertEqual((), inventory.modules_without_contract)

    def test_missing_and_unknown_public_contract_modes_fail_closed(self):
        for mode, rendered in ((None, "missing"), ("future", "'future'")):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                declaration = {
                    "contractId": "target.event",
                    "major": 1,
                    "source": "Modules/target/contracts/provided/target.v1.schema.json",
                    "status": "active",
                    "ownerModule": "target",
                }
                if mode is not None:
                    declaration["mode"] = mode
                inventory = self._discover_public_contract_mode_fixture(
                    Path(temporary), declaration
                )
                self.assertEqual(
                    (
                        "target: contracts.provided[0] has unknown or missing "
                        "compatibility mode: " + rendered,
                    ),
                    inventory.errors,
                )
                self.assertEqual(1, inventory.public_contract_owner_count)
                self.assertEqual(1, inventory.public_contract_count)
                self.assertEqual((), inventory.modules_without_contract)

    def test_active_mode_cannot_hide_behind_retired_status(self):
        with tempfile.TemporaryDirectory() as temporary:
            inventory = self._discover_public_contract_mode_fixture(
                Path(temporary),
                {
                    "contractId": "target.event",
                    "major": 1,
                    "source": "Modules/target/contracts/provided/target.v1.schema.json",
                    "status": "retired",
                    "mode": "active",
                    "ownerModule": "target",
                },
            )
        self.assertEqual(2, inventory.public_contract_owner_count)
        self.assertEqual(2, inventory.public_contract_count)
        self.assertEqual(("target",), inventory.modules_without_contract)
        self.assertIn(
            "target: public contract owner lacks required Contract suite",
            inventory.errors,
        )

    def test_every_python_candidate_uses_isolated_discovery(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        for candidate in inventory.suites:
            plan = SUBJECT.parse_candidate_suite(ROOT, candidate)
            for invocation in plan.invocations:
                if invocation.kind != "python-unittest":
                    continue
                argv = list(invocation.argv)
                self.assertEqual(["-I", "-m", "unittest", "discover"], argv[1:5])

    def test_all_candidate_trust_paths_are_bound_by_production_resolver(self):
        resolver = SUBJECT._load_factory_resolver(ROOT)
        self.assertEqual(
            set(SUBJECT.CANDIDATE_TRUST_PATHS),
            set(resolver._CANDIDATE_TRUST_PATHS),
        )
        self.assertTrue(
            {
                "AGENTS.md",
                "Directory.Build.props",
                "Directory.Build.targets",
                "Directory.Packages.props",
                "Dps.slnx",
                "NuGet.Config",
                "global.json",
                "governance/modules/module-catalog.yaml",
                "governance/modules/dependency-graph.yaml",
                "governance/modules/compatibility.yaml",
                "governance/policies/risk-policy.yaml",
                "governance/policies/compatibility-policy.yaml",
                "Tools/ci/validate_repo.py",
                "scripts/release.sh",
            }.issubset(set(SUBJECT.CANDIDATE_TRUST_PATHS))
        )

    def test_locked_third_party_imports_originate_in_repository_venv(self):
        self.assertNotIn(str(SUBJECT.CI_DIRECTORY), SUBJECT.sys.path)
        expected = (ROOT / ".venv").resolve()
        for package, origin in SUBJECT._LOCKED_DEPENDENCY_ORIGINS.items():
            with self.subTest(package=package):
                Path(origin).resolve().relative_to(expected)

    def test_phase0_test_import_restores_ci_path_for_discovery_order(self):
        script = r"""
import importlib.util
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()
test_path = root / "Tests/ci/test_phase0_gate.py"
module_name = "dps_phase0_import_isolation_probe"
before = list(sys.path)
after = None
spec = importlib.util.spec_from_file_location(module_name, test_path)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
sys.modules[module_name] = module
try:
    spec.loader.exec_module(module)
    after = list(sys.path)
finally:
    sys.path[:] = before
    sys.modules.pop(module_name, None)
if after != before:
    raise SystemExit("test_phase0_gate import leaked sys.path entries")
"""
        completed = subprocess.run(
            [sys.executable, "-I", "-c", script, str(ROOT)],
            cwd=str(ROOT),
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stdout)

    def test_repository_local_psycopg_yaml_and_sourceless_pyc_are_rejected(self):
        for relative in (
            Path("psycopg.py"),
            Path("yaml.pyc"),
            Path("__pycache__/jsonschema.cpython-312.pyc"),
            Path("referencing/__init__.py"),
        ):
            with self.subTest(path=str(relative)), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"malicious-shadow")
                errors = SUBJECT._repository_import_shadow_errors(root)
                self.assertEqual(1, len(errors))
                package = (
                    relative.parts[-1].split(".")[0]
                    if relative.parts[0] == "__pycache__"
                    else relative.parts[0].split(".")[0]
                )
                self.assertIn(package, errors[0])

    def test_locked_git_ignores_ambient_path_and_git_directory(self):
        expected = subprocess.check_output(
            ["/usr/bin/git", "rev-parse", "HEAD^{commit}"],
            cwd=ROOT,
            text=True,
        ).strip()
        with mock.patch.dict(
            SUBJECT.os.environ,
            {"PATH": "/attacker", "GIT_DIR": "/attacker/repo", "GIT_CONFIG_COUNT": "9"},
            clear=False,
        ):
            actual = SUBJECT._candidate_git_output(
                ROOT, ["rev-parse", "HEAD^{commit}"]
            )
        self.assertEqual(expected, actual)

    def test_upgrade_intent_must_match_the_complete_deterministic_shape(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "CONTRACT_VERIFIED", policy
        )
        baseline = SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"])
        started_at = "2026-07-15T00:00:00+00:00"
        intent = SUBJECT._build_upgrade_intent(
            ROOT,
            baseline,
            baseline,
            started_at,
            inventory,
            (),
        )
        SUBJECT._validate_upgrade_intent_binding(
            ROOT,
            intent,
            baseline=baseline,
            head=baseline,
            started_at=started_at,
            inventory=inventory,
            changed_paths=(),
        )
        for field, replacement in (
            ("risk_tier", "R0"),
            ("requested_stage", "production"),
            ("producer_module", "control-plane-host"),
        ):
            with self.subTest(field=field):
                forged = copy.deepcopy(intent)
                forged[field] = replacement
                with self.assertRaises(SUBJECT.Phase0Error):
                    SUBJECT._validate_upgrade_intent_binding(
                        ROOT,
                        forged,
                        baseline=baseline,
                        head=baseline,
                        started_at=started_at,
                        inventory=inventory,
                        changed_paths=(),
                    )

    def test_baseline_trust_anchor_detects_a_same_commit_input_mutation(self):
        with tempfile.TemporaryDirectory(prefix="dps-candidate-trust-") as temporary:
            root = Path(temporary)
            subprocess.run(
                ["git", "init", "-q"], cwd=root, check=True
            )
            subprocess.run(
                ["git", "config", "user.email", "dps-test@example.invalid"],
                cwd=root,
                check=True,
            )
            subprocess.run(
                ["git", "config", "user.name", "DPS Test"],
                cwd=root,
                check=True,
            )
            trust_input = root / "Directory.Build.props"
            trust_input.write_text("<Project />\n", encoding="utf-8")
            subprocess.run(
                ["git", "add", "Directory.Build.props"], cwd=root, check=True
            )
            subprocess.run(
                ["git", "commit", "-qm", "baseline"], cwd=root, check=True
            )
            baseline = subprocess.check_output(
                ["git", "rev-parse", "HEAD"], cwd=root, text=True
            ).strip()
            with mock.patch.object(
                SUBJECT,
                "CANDIDATE_TRUST_PATHS",
                ("Directory.Build.props",),
            ):
                _, matched = SUBJECT._trust_anchor_inventory(root, baseline)
                self.assertTrue(matched)
                trust_input.write_text(
                    "<Project><PropertyGroup /></Project>\n", encoding="utf-8"
                )
                records, matched = SUBJECT._trust_anchor_inventory(root, baseline)
            self.assertFalse(matched)
            self.assertFalse(records[0]["matches_baseline"])

    def test_audit_contract_cannot_be_retargeted_to_production_source(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "CONTRACT_VERIFIED", policy
        )
        candidate = next(
            value for value in inventory.suites if value.module_id == "audit-metrics"
        )
        altered_suite = dict(candidate.suite)
        altered_suite["command"] = altered_suite["command"].replace(
            "Modules/audit-metrics/tests/Dps.AuditMetrics.Tests/Dps.AuditMetrics.Tests.csproj",
            "Modules/audit-metrics/src/Dps.AuditMetrics/Dps.AuditMetrics.csproj",
        )
        altered = SUBJECT.replace(candidate, suite=altered_suite)
        with self.assertRaises(SUBJECT.Phase0Error):
            plan = SUBJECT.parse_candidate_suite(ROOT, altered)
            SUBJECT.validate_candidate_plan(ROOT, altered, plan, policy)

    def test_policy_floor_is_injected_into_every_effective_plan(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        for candidate in inventory.suites:
            raw = SUBJECT.parse_candidate_suite(ROOT, candidate)
            plan, _, _, _, _ = SUBJECT.validate_candidate_plan(
                ROOT, candidate, raw, policy
            )
            binding = SUBJECT._policy_for_candidate(candidate, policy)
            test_invocations = [
                value for value in plan.invocations if value.kind != "restore"
            ]
            self.assertEqual(1, len(test_invocations))
            self.assertEqual(
                binding.minimum_executed_tests,
                test_invocations[0].minimum_tests,
            )

    def test_policy_floor_cannot_weaken_manifest_floor(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        candidate = next(
            value
            for value in inventory.suites
            if value.module_id == "binding"
            and value.suite["id"] == "binding.integration"
        )
        raw = SUBJECT.parse_candidate_suite(ROOT, candidate)
        manifest_floor = max(
            value.minimum_tests
            for value in raw.invocations
            if value.kind != "restore"
        )
        with self.assertRaisesRegex(
            SUBJECT.Phase0Error, "cannot weaken the module manifest floor"
        ):
            SUBJECT._apply_policy_floor(raw, manifest_floor - 1)

    def test_zenno_special_plan_requires_security_simulation_policy_binding(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        candidate = next(
            value
            for value in inventory.suites
            if value.suite["id"] == "zenno-bridge.auth-simulation"
        )
        SUBJECT.parse_candidate_suite(ROOT, candidate)
        altered = SUBJECT.replace(
            candidate,
            integration_policy=SUBJECT.replace(
                candidate.integration_policy,
                test_category="Integration",
            ),
        )
        with self.assertRaisesRegex(
            SUBJECT.Phase0Error, "exact SIMULATION/.*SecuritySimulation"
        ):
            SUBJECT.parse_candidate_suite(ROOT, altered)

    def test_integration_coverage_gaps_are_explicit_and_stable(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        expected = []
        for module_root in SUBJECT.discover_registered_module_dirs(ROOT):
            manifest = SUBJECT.load_json_compatible_yaml(module_root / "module.yaml")
            gates = manifest.get("deviceGates")
            level = gates.get("minimumVerification") if isinstance(gates, dict) else None
            requires_integration = SUBJECT.LEVEL_RANK.get(str(level), 0) >= (
                SUBJECT.LEVEL_RANK["INTEGRATION_VERIFIED"]
            )
            suites = manifest.get("tests", {}).get("suites", [])
            has_required_integration = any(
                isinstance(suite, dict)
                and suite.get("required") is True
                and suite.get("type") == "integration"
                and suite.get("evidenceLevel") == "INTEGRATION_VERIFIED"
                for suite in suites
            )
            if requires_integration and not has_required_integration:
                expected.append(module_root.name)
        self.assertEqual(tuple(sorted(expected)), inventory.modules_without_integration)

    def test_duplicate_unittest_summaries_cannot_raise_the_count_floor(self):
        phase0_runner = sys.modules["run_phase0_gate"]
        invocation = phase0_runner.TrustedInvocation(
            [sys.executable, "-I", "-m", "unittest"],
            "python-unittest",
            4,
        )
        count, reason = phase0_runner._executed_test_count(
            invocation,
            "Ran 1 test in 0.001s\n\nOK\nRan 999 tests in 0.001s\n\nOK\n",
        )
        self.assertEqual(0, count)
        self.assertIn("exactly one", str(reason))

    def test_isolated_python_ignores_shadow_unittest_module(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "unittest.py").write_text(
                "raise RuntimeError('shadow stdlib loaded')\n", encoding="utf-8"
            )
            (root / "test_sample.py").write_text(
                "import unittest\n"
                "class Sample(unittest.TestCase):\n"
                "    def test_ok(self): self.assertTrue(True)\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                [
                    sys.executable,
                    "-I",
                    "-m",
                    "unittest",
                    "discover",
                    "-s",
                    str(root),
                    "-p",
                    "test_sample.py",
                ],
                cwd=str(root),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                check=False,
            )
        self.assertEqual(0, completed.returncode, completed.stdout)
        self.assertIn("Ran 1 test", completed.stdout)

    def test_postgres_password_value_is_redacted_even_without_key_name(self):
        secret = "only-the-password-value-719"
        environment = {
            "DPS_TEST_POSTGRES": (
                "Host=127.0.0.1;Port=5432;Database=dps;Username=dps;Password="
                + secret
            ),
            "DPS_TEST_POSTGRES_URI": (
                "host=127.0.0.1 port=5432 dbname=dps user=dps password="
                + secret
            ),
        }
        redacted = SUBJECT._redact_log("driver leaked " + secret, environment)
        self.assertNotIn(secret, redacted)
        self.assertIn("REDACTED", redacted)

    def test_all_allowlisted_postgres_uri_passwords_are_redacted(self):
        secret = "dual-role-password-8821"
        environment = {
            "DPS_TEST_POSTGRES_ADMIN_URI": (
                "postgresql://admin:" + secret + "@127.0.0.1:5432/dps"
            ),
            "DPS_TEST_POSTGRES_RUNTIME_URI": (
                "host=127.0.0.1 dbname=dps user=runtime password=" + secret
            ),
        }
        redacted = SUBJECT._redact_log("driver leaked " + secret, environment)
        self.assertNotIn(secret, redacted)
        self.assertIn("REDACTED", redacted)

    def test_private_key_pem_is_redacted_even_if_test_prints_file_contents(self):
        pem = (
            "-----BEGIN PRIVATE KEY-----\n"
            "sensitive-base64-body\n"
            "-----END PRIVATE KEY-----"
        )
        redacted = SUBJECT._redact_log("leak:\n" + pem, {})
        self.assertNotIn("sensitive-base64-body", redacted)
        self.assertIn("REDACTED:PRIVATE_KEY_PEM", redacted)

    def test_candidate_environment_allowlist_matches_policy_schema(self):
        policy_schema = json.loads(
            (ROOT / SUBJECT.POLICY_SCHEMA_PATH).read_text(encoding="utf-8")
        )
        policy_allowed = set(
            policy_schema["properties"]["integrationSuites"]["items"]["properties"]
            ["requiredEnvironment"]["items"]["enum"]
        )
        evidence_schema = json.loads(
            (ROOT / SUBJECT.EVIDENCE_SCHEMA_PATH).read_text(encoding="utf-8")
        )
        evidence_allowed = set(
            evidence_schema["$defs"]["environmentKeys"]["items"]["enum"]
        )
        self.assertEqual(
            set(SUBJECT.ALLOWED_RUNTIME_ENVIRONMENT), policy_allowed
        )
        self.assertEqual(policy_allowed, evidence_allowed)

    def test_postgres_preflight_checks_every_declared_connection_target(self):
        runtime = {
            "DPS_PSQL": "/trusted/psql",
            "DPS_TEST_POSTGRES": (
                "Host=127.0.0.1;Port=5432;Database=dps;Username=runtime;Password=x"
            ),
            "DPS_TEST_POSTGRES_URI": (
                "host=127.0.0.1 port=5432 dbname=dps user=runtime password=x"
            ),
            "DPS_TEST_POSTGRES_ADMIN_URI": "postgresql://admin:x@127.0.0.1:5432/dps",
            "DPS_TEST_POSTGRES_RUNTIME_URI": "postgresql://runtime:x@127.0.0.1:5432/dps",
        }
        required = tuple(SUBJECT.POSTGRES_CONNECTION_ENVIRONMENT) + (
            "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE",
            "DPS_PSQL",
        )
        with mock.patch.object(
            SUBJECT,
            "_safe_repo_or_external_executable",
            return_value=Path("/trusted/psql"),
        ) as executable, mock.patch.object(
            SUBJECT,
            "_query_postgres_server_version",
            side_effect=["180004", "180004", "180004"],
        ) as query:
            version, check = SUBJECT.postgres_preflight(ROOT, runtime, required)

        self.assertEqual("180004", version)
        self.assertEqual("PASS", check["status"])
        self.assertEqual(3, check["details"]["connection_target_count"])
        executable.assert_called_once_with("/trusted/psql")
        self.assertEqual(3, query.call_count)

    def test_postgres_preflight_rejects_wrong_server_even_with_executable_psql(self):
        runtime = {
            "DPS_PSQL": "/trusted/fake-psql",
            "DPS_TEST_POSTGRES_URI": (
                "host=127.0.0.1 port=5432 dbname=dps user=dps password=x"
            ),
        }
        with mock.patch.object(
            SUBJECT,
            "_safe_repo_or_external_executable",
            return_value=Path("/trusted/fake-psql"),
        ), mock.patch.object(
            SUBJECT,
            "_query_postgres_server_version",
            return_value="170009",
        ):
            version, check = SUBJECT.postgres_preflight(
                ROOT,
                runtime,
                ("DPS_TEST_POSTGRES_URI", "DPS_PSQL"),
            )
        self.assertIsNone(version)
        self.assertEqual("INFRA_ERROR", check["status"])
        self.assertIn("180004", check["log"])

    def test_postgres_preflight_fails_when_declared_role_dsn_is_missing(self):
        runtime = {
            "DPS_PSQL": "/trusted/psql",
            "DPS_TEST_POSTGRES_ADMIN_URI": "postgresql://admin:x@127.0.0.1:5432/dps",
        }
        version, check = SUBJECT.postgres_preflight(
            ROOT,
            runtime,
            ("DPS_TEST_POSTGRES_ADMIN_URI", "DPS_TEST_POSTGRES_RUNTIME_URI"),
        )
        self.assertIsNone(version)
        self.assertEqual("INFRA_ERROR", check["status"])
        self.assertIn("DPS_TEST_POSTGRES_RUNTIME_URI", check["log"])

    def test_evidence_output_is_restricted_to_ignored_reports_ci(self):
        for unsafe in (
            Path("README.json"),
            Path("Reports/ci/Candidate.json"),
            Path("Reports/ci/candidate.json.publication.json"),
        ):
            with self.subTest(path=str(unsafe)), self.assertRaises(
                SUBJECT.Phase0Error
            ):
                SUBJECT._safe_evidence_path(ROOT, unsafe)
        accepted = SUBJECT._safe_evidence_path(
            ROOT, Path("Reports/ci/candidate-gate-test-output.json")
        )
        self.assertEqual(
            ROOT / "Reports/ci/candidate-gate-test-output.json", accepted
        )

    def test_json_evidence_read_rejects_symlink_and_binds_one_file_sha(self):
        with tempfile.TemporaryDirectory(prefix="dps-safe-json-read-") as temporary:
            root = Path(temporary)
            payload = b'{"status":"PASS"}\n'
            source = root / "source.json"
            source.write_bytes(payload)
            link = root / "link.json"
            link.symlink_to(source)
            with self.assertRaises(SUBJECT.Phase0Error):
                SUBJECT._load_json_object(link, "fixture")
            value, digest = SUBJECT._load_json_object_with_sha(source, "fixture")
            self.assertEqual({"status": "PASS"}, value)
            self.assertEqual(hashlib.sha256(payload).hexdigest(), digest)

    def test_phase0_prerequisite_is_scoped_to_candidate_evidence(self):
        contract = SUBJECT._safe_evidence_path(
            ROOT, Path("Reports/ci/contract-parallel-test.json")
        )
        integration = SUBJECT._safe_evidence_path(
            ROOT, Path("Reports/ci/integration-parallel-test.json")
        )
        contract_run_id = "1" * 32
        integration_run_id = "2" * 32
        contract_phase0 = SUBJECT._phase0_companion_evidence_path(
            ROOT, contract, contract_run_id
        )
        integration_phase0 = SUBJECT._phase0_companion_evidence_path(
            ROOT, integration, integration_run_id
        )
        self.assertEqual(
            ROOT
            / (
                "Reports/ci/phase0-prerequisites/"
                "contract-parallel-test-{0}.json".format(contract_run_id)
            ),
            contract_phase0,
        )
        self.assertEqual(
            ROOT
            / (
                "Reports/ci/phase0-prerequisites/"
                "integration-parallel-test-{0}.json".format(integration_run_id)
            ),
            integration_phase0,
        )
        self.assertNotEqual(contract_phase0, integration_phase0)
        with self.assertRaisesRegex(SUBJECT.Phase0Error, "run id"):
            SUBJECT._phase0_companion_evidence_path(
                ROOT, contract, "not-a-run-id"
            )
        with self.assertRaises(SUBJECT.Phase0Error):
            SUBJECT._safe_evidence_path(
                ROOT,
                Path("Reports/ci/phase0-prerequisites/contract-parallel-test.json"),
            )

    def test_default_candidate_evidence_uses_a_unique_run_directory(self):
        arguments = SUBJECT.parse_arguments(["--level", "contract"])
        self.assertIsNone(arguments.evidence)
        first_run_id = SUBJECT._new_publication_run_id()
        second_run_id = SUBJECT._new_publication_run_id()
        first = SUBJECT._default_candidate_evidence_path(
            "contract", first_run_id
        )
        second = SUBJECT._default_candidate_evidence_path(
            "contract", second_run_id
        )
        self.assertEqual(
            Path("Reports/ci/candidate-runs")
            / "contract"
            / first_run_id
            / "candidate-evidence.json",
            first,
        )
        self.assertNotEqual(first, second)
        publication = SUBJECT.EvidencePublication(
            ROOT / first, run_id=first_run_id
        )
        self.assertEqual(first_run_id, publication.run_id)
        with self.assertRaisesRegex(SUBJECT.Phase0Error, "run id"):
            SUBJECT._default_candidate_evidence_path(
                "contract", "not-a-run-id"
            )

    def test_candidate_evidence_rejects_internal_file_symlink(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            directory = Path(temporary)
            target = directory / "shared.json"
            target.write_text("{}\n", encoding="utf-8")
            link = directory / "candidate.json"
            link.symlink_to(target.name)
            with self.assertRaises(SUBJECT.Phase0Error):
                SUBJECT._safe_evidence_path(ROOT, link)

    def test_candidate_evidence_rejects_publication_marker_symlink(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            marker = publication_module._publication_marker_path(candidate)
            marker.symlink_to(ROOT / "README.md")
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "publication paths"):
                SUBJECT._safe_evidence_path(ROOT, candidate)

    def test_candidate_evidence_rejects_internal_directory_symlink(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            directory = Path(temporary)
            target = directory / "real"
            target.mkdir()
            link = directory / "linked"
            link.symlink_to(target.name, target_is_directory=True)
            with self.assertRaises(SUBJECT.Phase0Error):
                SUBJECT._safe_evidence_path(ROOT, link / "candidate.json")

    def test_candidate_evidence_rejects_symlink_to_tracked_file(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            link = Path(temporary) / "candidate.json"
            link.symlink_to(ROOT / "README.md")
            with self.assertRaises(SUBJECT.Phase0Error):
                SUBJECT._safe_evidence_path(ROOT, link)

    def test_candidate_evidence_requires_lowercase_json_suffix(self):
        with self.assertRaises(SUBJECT.Phase0Error):
            SUBJECT._safe_evidence_path(
                ROOT, Path("Reports/ci/candidate-case-collision.JSON")
            )

    def test_parallel_companion_writes_remain_distinct(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            directory = Path(temporary)
            candidates = [
                SUBJECT._safe_evidence_path(ROOT, directory / "contract.json"),
                SUBJECT._safe_evidence_path(ROOT, directory / "integration.json"),
            ]
            companions = [
                SUBJECT._phase0_companion_evidence_path(
                    ROOT, value, str(index + 1) * 32
                )
                for index, value in enumerate(candidates)
            ]
            with ThreadPoolExecutor(max_workers=2) as executor:
                list(
                    executor.map(
                        lambda item: SUBJECT.write_evidence(
                            item[1], {"writer": item[0]}
                        ),
                        enumerate(companions),
                    )
                )
            self.assertEqual(
                [{"writer": 0}, {"writer": 1}],
                [json.loads(value.read_text(encoding="utf-8")) for value in companions],
            )
            for value in companions:
                committed, digest = SUBJECT._load_committed_json_object_with_sha(
                    value, "companion evidence"
                )
                self.assertIn("writer", committed)
                self.assertEqual(
                    hashlib.sha256(value.read_bytes()).hexdigest(), digest
                )

    def test_committed_reader_rejects_plain_or_tampered_json(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            candidate.write_text('{"status":"PASS"}\n', encoding="utf-8")
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "publication marker"):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

            SUBJECT.write_evidence(candidate, {"status": "PASS"})
            value, digest = SUBJECT._load_committed_json_object_with_sha(
                candidate, "candidate evidence"
            )
            self.assertEqual({"status": "PASS"}, value)
            self.assertEqual(hashlib.sha256(candidate.read_bytes()).hexdigest(), digest)

            candidate.write_text('{"status":"FAIL"}\n', encoding="utf-8")
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "COMMITTED integrity"):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

    def test_committed_reader_rejects_marker_symlink(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            SUBJECT.write_evidence(candidate, {"status": "PASS"})
            marker = publication_module._publication_marker_path(candidate)
            marker.unlink()
            marker.symlink_to(candidate.name)
            with self.assertRaises(SUBJECT.Phase0Error):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

    def test_committed_reader_detects_marker_inode_swap(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            directory = Path(temporary)
            candidate = directory / "candidate.json"
            SUBJECT.write_evidence(candidate, {"status": "PASS"})
            marker = publication_module._publication_marker_path(candidate)
            original_read = publication_module._read_regular_file_at
            swapped = {"value": False}

            def swap_after_marker_read(descriptor, name, label, maximum_bytes=None):
                arguments = (descriptor, name, label)
                if maximum_bytes is None:
                    result = original_read(*arguments)
                else:
                    result = original_read(
                        *arguments, maximum_bytes=maximum_bytes
                    )
                if name == marker.name and not swapped["value"]:
                    replacement = directory / "replacement-marker.json"
                    replacement.write_bytes(marker.read_bytes())
                    replacement.replace(marker)
                    swapped["value"] = True
                return result

            with mock.patch.object(
                publication_module,
                "_read_regular_file_at",
                side_effect=swap_after_marker_read,
            ):
                with self.assertRaisesRegex(SUBJECT.Phase0Error, "marker changed"):
                    SUBJECT._load_committed_json_object_with_sha(
                        candidate, "candidate evidence"
                    )

    def test_same_path_concurrent_publication_is_exclusive(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            with SUBJECT.EvidencePublication(candidate) as first:
                with self.assertRaises(BlockingIOError):
                    with SUBJECT.EvidencePublication(candidate):
                        self.fail("a concurrent writer acquired the same evidence path")
                first.stage({"writer": "first"})
                first.commit()
            committed, _ = SUBJECT._load_committed_json_object_with_sha(
                candidate, "candidate evidence"
            )
            self.assertEqual("first", committed["writer"])
            with self.assertRaisesRegex(FileExistsError, "immutable"):
                SUBJECT.write_evidence(
                    candidate, {"writer": "later-sequential-run"}
                )
            committed, _ = SUBJECT._load_committed_json_object_with_sha(
                candidate, "candidate evidence"
            )
            self.assertEqual("first", committed["writer"])

    def test_normal_early_exit_is_durably_aborted_and_retryable(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            publication = SUBJECT.EvidencePublication(candidate)
            with publication:
                pass
            self.assertFalse(publication.claim_path.exists())
            marker = json.loads(
                publication_module._publication_marker_path(candidate).read_text(
                    encoding="utf-8"
                )
            )
            self.assertEqual("ABORTED", marker["status"])

            with SUBJECT.EvidencePublication(candidate) as retry:
                retry.stage({"writer": "retry"})
                retry.commit()
            committed, _ = SUBJECT._load_committed_json_object_with_sha(
                candidate, "candidate evidence"
            )
            self.assertEqual("retry", committed["writer"])

    def test_business_exception_after_stage_aborts_without_quarantine(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            publication = SUBJECT.EvidencePublication(candidate)
            with self.assertRaisesRegex(ValueError, "business failure"):
                with publication:
                    publication.stage({"status": "FAIL"})
                    raise ValueError("business failure")
            self.assertFalse(publication.claim_path.exists())
            marker = json.loads(
                publication_module._publication_marker_path(candidate).read_text(
                    encoding="utf-8"
                )
            )
            self.assertEqual("ABORTED", marker["status"])

    def test_commit_directory_fsync_failure_keeps_claim_and_blocks_reader(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            publication = SUBJECT.EvidencePublication(candidate)
            with publication:
                publication.stage({"status": "PASS"})
                original_fsync = publication_module.os.fsync
                calls = {"count": 0}

                def fail_directory_sync(descriptor):
                    calls["count"] += 1
                    if calls["count"] == 2:
                        raise OSError("directory sync failed")
                    return original_fsync(descriptor)

                with mock.patch.object(
                    publication_module.os,
                    "fsync",
                    side_effect=fail_directory_sync,
                ):
                    with self.assertRaisesRegex(OSError, "directory sync failed"):
                        publication.commit()
            self.assertTrue(publication.claim_path.exists())
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "manual recovery"):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

    def test_payload_rename_failure_keeps_claim_and_blocks_reader(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            publication = SUBJECT.EvidencePublication(candidate)
            with publication:
                with mock.patch.object(
                    publication_module.os,
                    "rename",
                    side_effect=OSError("rename failed"),
                ):
                    with self.assertRaisesRegex(OSError, "rename failed"):
                        publication.stage({"status": "PASS"})
            self.assertTrue(publication.claim_path.exists())
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "manual recovery"):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

    def test_payload_fsync_failure_keeps_claim_and_blocks_reader(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            publication = SUBJECT.EvidencePublication(candidate)
            with publication:
                with mock.patch.object(
                    publication_module.os,
                    "fsync",
                    side_effect=OSError("payload sync failed"),
                ):
                    with self.assertRaisesRegex(OSError, "payload sync failed"):
                        publication.stage({"status": "PASS"})
            self.assertTrue(publication.claim_path.exists())
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "manual recovery"):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

    def test_commit_marker_swap_before_claim_release_fails_closed(self):
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            publication = SUBJECT.EvidencePublication(candidate)
            with publication:
                publication.stage({"status": "PASS"})
                original_read = publication_module._read_regular_file_at

                def corrupt_marker_read(descriptor, name, label, maximum_bytes=None):
                    if label == "COMMITTED evidence marker":
                        return b'{}\n', (1, 1, 3)
                    arguments = (descriptor, name, label)
                    if maximum_bytes is None:
                        return original_read(*arguments)
                    return original_read(
                        *arguments, maximum_bytes=maximum_bytes
                    )

                with mock.patch.object(
                    publication_module,
                    "_read_regular_file_at",
                    side_effect=corrupt_marker_read,
                ):
                    with self.assertRaisesRegex(
                        SUBJECT.Phase0Error, "marker changed"
                    ):
                        publication.commit()
            self.assertTrue(publication.claim_path.exists())
            with self.assertRaisesRegex(SUBJECT.Phase0Error, "manual recovery"):
                SUBJECT._load_committed_json_object_with_sha(
                    candidate, "candidate evidence"
                )

    def test_evidence_write_does_not_follow_post_validation_directory_swap(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            holder = Path(temporary)
            run_directory = holder / "run"
            run_directory.mkdir()
            candidate = SUBJECT._safe_evidence_path(
                ROOT, run_directory / "candidate.json"
            )
            original_directory = holder / "run-original"
            run_directory.rename(original_directory)
            outside = holder / "outside"
            outside.mkdir()
            run_directory.symlink_to(outside.name, target_is_directory=True)
            with self.assertRaises(OSError):
                SUBJECT.write_evidence(candidate, {"status": "must-not-redirect"})
            self.assertFalse((outside / "candidate.json").exists())

    def test_evidence_write_does_not_create_directory_through_swapped_symlink(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            holder = Path(temporary)
            run_directory = holder / "run"
            run_directory.mkdir()
            candidate = SUBJECT._safe_evidence_path(
                ROOT, run_directory / "new-child" / "candidate.json"
            )
            original_directory = holder / "run-original"
            run_directory.rename(original_directory)
            outside = holder / "outside"
            outside.mkdir()
            run_directory.symlink_to(outside.name, target_is_directory=True)
            with self.assertRaises(OSError):
                SUBJECT.write_evidence(candidate, {"status": "must-not-create"})
            self.assertFalse((outside / "new-child").exists())

    def test_evidence_write_fails_closed_without_no_follow_primitives(self):
        phase0_module = sys.modules["phase0"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            candidate = Path(temporary) / "candidate.json"
            with mock.patch.object(phase0_module.os, "supports_dir_fd", set()):
                with self.assertRaises(OSError) as raised:
                    SUBJECT.write_evidence(candidate, {"status": "must-fail-closed"})
            self.assertEqual(phase0_module.errno.ENOTSUP, raised.exception.errno)
            self.assertFalse(candidate.exists())

    def test_evidence_write_closes_directory_descriptor_on_cleanup_error(self):
        phase0_module = sys.modules["phase0"]
        publication_module = sys.modules["run_phase0_gate"]
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            directory = Path(temporary)
            candidate = directory / "candidate.json"
            descriptor = phase0_module.os.open(
                directory,
                phase0_module.os.O_RDONLY | phase0_module.os.O_DIRECTORY,
            )
            with mock.patch.object(
                publication_module,
                "_open_directory_no_follow",
                return_value=descriptor,
            ), mock.patch.object(
                publication_module.os,
                "unlink",
                side_effect=PermissionError("cleanup denied"),
            ):
                with self.assertRaises(PermissionError):
                    SUBJECT.write_evidence(candidate, {"status": "written"})
            with self.assertRaises(OSError):
                phase0_module.os.fstat(descriptor)

    def test_empty_phase0_check_inventory_is_never_accepted(self):
        forged = {
            "evidence_sha256": "0" * 64,
            "checks": [],
            "overall_status": "PASS",
        }
        self.assertFalse(
            SUBJECT._phase0_payload_valid(
                forged,
                root=ROOT,
                baseline="0" * 40,
                head="0" * 40,
                workspace_digest_value="0" * 64,
                diagnostic=True,
            )
        )

    def test_phase0_prerequisite_cannot_downgrade_required_check(self):
        payload, baseline, head, workspace = self._synthetic_phase0_payload(
            required=False, check_status="FAIL"
        )
        with mock.patch.object(
            SUBJECT,
            "_expected_phase0_check_ids",
            return_value={"clean-checkout-evidence-boundary"},
        ), mock.patch.object(
            SUBJECT, "_load_json_object", return_value={}
        ), mock.patch.object(
            SUBJECT, "validate_json_schema", return_value=[]
        ), mock.patch.object(
            SUBJECT,
            "validate_instruction_receipt",
            side_effect=lambda root, receipt: (True, "BOUND", dict(receipt)),
        ):
            self.assertFalse(
                SUBJECT._phase0_payload_valid(
                    payload,
                    root=ROOT,
                    baseline=baseline,
                    head=head,
                    workspace_digest_value=workspace,
                    diagnostic=True,
                )
            )

    def test_phase0_prerequisite_rejects_stale_instruction_receipt(self):
        payload, baseline, head, workspace = self._synthetic_phase0_payload(
            receipt_status="STALE"
        )
        with mock.patch.object(
            SUBJECT,
            "_expected_phase0_check_ids",
            return_value={"clean-checkout-evidence-boundary"},
        ), mock.patch.object(
            SUBJECT, "_load_json_object", return_value={}
        ), mock.patch.object(
            SUBJECT, "validate_json_schema", return_value=[]
        ), mock.patch.object(
            SUBJECT,
            "validate_instruction_receipt",
            side_effect=lambda root, receipt: (True, "BOUND", dict(receipt)),
        ):
            self.assertFalse(
                SUBJECT._phase0_payload_valid(
                    payload,
                    root=ROOT,
                    baseline=baseline,
                    head=head,
                    workspace_digest_value=workspace,
                    diagnostic=True,
                )
            )

    def test_diagnostic_phase0_payload_cannot_be_relabelled_formal(self):
        payload, baseline, head, workspace = self._synthetic_phase0_payload()
        payload["verification_level"] = "REPOSITORY_STATIC_VERIFIED"
        payload["commit_sha"] = head
        payload["evidence_sha256"] = SUBJECT.sha256_text(
            SUBJECT.stable_json(
                {
                    key: value
                    for key, value in payload.items()
                    if key != "evidence_sha256"
                }
            )
        )
        with mock.patch.object(
            SUBJECT,
            "_expected_phase0_check_ids",
            return_value={"clean-checkout-evidence-boundary"},
        ):
            self.assertFalse(
                SUBJECT._phase0_payload_valid(
                    payload,
                    root=ROOT,
                    baseline=baseline,
                    head=head,
                    workspace_digest_value=workspace,
                    diagnostic=False,
                )
            )

    def test_canonical_formal_phase0_payload_is_accepted(self):
        payload, baseline, head, workspace = self._synthetic_phase0_payload(
            formal=True
        )
        check = payload["checks"][0]
        check_id = check["id"]
        receipt_id = payload["instruction_receipt"]["receipt_id"]
        payload["test_evidence"] = [
            {
                "evidence_id": "phase0:{0}:{1}".format(
                    SUBJECT.sha256_text(check_id + baseline)[:16], check_id
                ),
                "test_id": check_id,
                "required": True,
                "status": "PASS",
                "exit_code": 0,
                "verification_level": "REPOSITORY_STATIC_VERIFIED",
                "baseline_commit": baseline,
                "instruction_receipt_id": receipt_id,
                "runner_identity": "dps-phase0-gate",
                "artifacts": [
                    {
                        "path": "embedded:checks/{0}/log".format(check_id),
                        "sha256": check["log_sha256"],
                    }
                ],
            }
        ]
        payload["evidence_sha256"] = SUBJECT.sha256_text(
            SUBJECT.stable_json(
                {
                    key: value
                    for key, value in payload.items()
                    if key != "evidence_sha256"
                }
            )
        )
        with mock.patch.object(
            SUBJECT,
            "_expected_phase0_check_ids",
            return_value={check_id},
        ), mock.patch.object(
            SUBJECT, "_load_json_object", return_value={}
        ), mock.patch.object(
            SUBJECT, "validate_json_schema", return_value=[]
        ), mock.patch.object(
            SUBJECT,
            "validate_instruction_receipt",
            side_effect=lambda root, receipt: (True, "BOUND", dict(receipt)),
        ):
            self.assertTrue(
                SUBJECT._phase0_payload_valid(
                    payload,
                    root=ROOT,
                    baseline=baseline,
                    head=head,
                    workspace_digest_value=workspace,
                    diagnostic=False,
                )
            )

    def test_weak_receipt_empty_suites_forgery_is_rejected(self):
        head = SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"])
        snapshot = SUBJECT._workspace_snapshot(ROOT, head)
        forged = {
            "schema_version": SUBJECT.SCHEMA_VERSION,
            "gate": SUBJECT.GATE_NAME,
            "mode": "WORKSPACE_DIAGNOSTIC_ONLY",
            "diagnostic_requested": True,
            "requested_verification_level": "CONTRACT_VERIFIED",
            "candidate_verification_level": None,
            "verification_level": None,
            "signed": False,
            "formal_evidence_eligible": False,
            "overall_status": "PASS",
            "commit_sha": None,
            "head_commit_observed": head,
            "baseline_commit": head,
            "workspace": {
                "clean_start": snapshot["clean"],
                "clean_end": snapshot["clean"],
                "clean_post_write": snapshot["clean"],
                "digest_start": snapshot["digest"],
                "digest_end": snapshot["digest"],
                "digest_post_write": snapshot["digest"],
                "dirty_entry_count_start": snapshot["dirty_entry_count"],
                "dirty_entry_count_end": snapshot["dirty_entry_count"],
                "dirty_entry_count_post_write": snapshot["dirty_entry_count"],
            },
            "started_at": "2026-07-14T00:00:00+00:00",
            "finished_at": "2026-07-14T00:00:01+00:00",
            "environment": {
                "python": "3.12.13",
                "python_executable": sys.executable,
                "python_executable_realpath": str(Path(sys.executable).resolve()),
                "requirements_ci_sha256": "0" * 64,
                "toolchain_lock_sha256": "0" * 64,
                "python_packages": {},
                "dotnet_sdk": "10.0.301",
                "forwarded_environment_keys": [],
                "postgres_server_version_num": None,
            },
            "upgrade_intent": {},
            "changed_paths": [],
            "trust_anchors": [
                {
                    "path": "AGENTS.md",
                    "baseline_sha256": "0" * 64,
                    "current_sha256": "0" * 64,
                    "matches_baseline": True,
                }
            ],
            "instruction_receipt": {"status": "BOUND"},
            "phase0_prerequisite": {},
            "inventory": {
                "module_count": 1,
                "public_contract_owner_count": 0,
                "public_contract_count": 0,
                "public_contract_inventory": [],
                "public_contract_inventory_sha256": "0" * 64,
                "selected_suite_count": 0,
                "contract_suite_count": 0,
                "integration_suite_count": 0,
                "modules_without_contract_coverage": [],
                "modules_without_integration_coverage": [],
                "manifest_inventory_sha256": "0" * 64,
                "policy_sha256": "0" * 64,
            },
            "checks": [],
            "suites": [],
            "formal_test_evidence": [],
            "summary": {
                "required": 0,
                "pass": 0,
                "fail": 0,
                "infra_error": 0,
                "other_non_pass": 0,
            },
            "limitations": ["forged"],
        }
        forged["evidence_sha256"] = SUBJECT.sha256_text(
            SUBJECT.stable_json(forged)
        )
        with self.assertRaises(SUBJECT.Phase0Error):
            SUBJECT.validate_candidate_evidence(forged, ROOT)

    def test_suite_execution_phase0_error_becomes_infra_error_and_continues(self):
        policy, inventory = self._contract_inventory_slice(count=2)
        secret = "standalone-suite-secret-9021"
        runtime_environment = {
            "DPS_TEST_POSTGRES": (
                "Host=127.0.0.1;Port=5432;Database=dps;Username=dps;Password="
                + secret
            )
        }
        calls = 0

        def execute(_root, plan, timeout_seconds):
            nonlocal calls
            calls += 1
            if calls == 1:
                raise SUBJECT.Phase0Error(
                    "unknown trusted environment contained " + secret
                )
            minimum = plan.invocations[-1].minimum_tests
            return {
                "status": "PASS",
                "exit_code": 0,
                "log": "trusted second suite passed",
                "details": {
                    "executed_tests": minimum,
                    "minimum_tests": minimum,
                },
            }

        with mock.patch.object(SUBJECT, "execute_manifest_suite", side_effect=execute):
            results = SUBJECT.execute_candidate_suites(
                ROOT,
                inventory,
                policy,
                SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"]),
                True,
                "instruction:" + "1" * 32,
                runtime_environment,
                False,
                30,
            )

        self.assertEqual(2, calls)
        self.assertEqual(["INFRA_ERROR", "PASS"], [item["status"] for item in results])
        failed = results[0]
        self.assertEqual(127, failed["exit_code"])
        self.assertEqual(0, failed["executed_tests"])
        self.assertIsNotNone(failed["effective_argv_sha256"])
        self.assertIsNotNone(failed["test_target_sha256"])
        self.assertIsNotNone(failed["test_tree_sha256"])
        self.assertEqual([], failed["forwarded_environment_keys"])
        self.assertNotIn(secret, failed["log"])
        self.assertIn("raw suite output withheld", failed["log"])
        self.assertEqual(
            SUBJECT.sha256_text(failed["log"]), failed["log_sha256"]
        )

    def test_passing_suite_raw_output_is_never_embedded_in_candidate_evidence(self):
        policy, inventory = self._contract_inventory_slice()
        pem_fragment = "sensitive-private-key-base64-fragment"
        decoded_password = "url decoded password value"
        raw_output = pem_fragment + "\n" + decoded_password

        def execute(_root, plan, timeout_seconds):
            minimum = plan.invocations[-1].minimum_tests
            return {
                "status": "PASS",
                "exit_code": 0,
                "log": raw_output,
                "details": {
                    "executed_tests": minimum,
                    "minimum_tests": minimum,
                },
            }

        with mock.patch.object(SUBJECT, "execute_manifest_suite", side_effect=execute):
            result = SUBJECT.execute_candidate_suites(
                ROOT,
                inventory,
                policy,
                SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"]),
                True,
                "instruction:" + "4" * 32,
                {},
                False,
                30,
            )[0]

        self.assertEqual("PASS", result["status"])
        self.assertNotIn(pem_fragment, result["log"])
        self.assertNotIn(decoded_password, result["log"])
        self.assertIn(SUBJECT.sha256_text(raw_output), result["log"])
        self.assertIn("raw suite output withheld", result["log"])

    def test_missing_non_postgres_required_environment_never_executes_suite(self):
        policy = SUBJECT.load_candidate_policy(ROOT)
        inventory = SUBJECT.discover_candidate_inventory(
            ROOT, "INTEGRATION_VERIFIED", policy
        )
        candidate = next(
            value
            for value in inventory.suites
            if value.module_id == "binding"
            and value.suite["id"] == "binding.integration"
        )
        key = (candidate.module_id, str(candidate.suite["id"]))
        binding = policy.integration[key]
        hardened_binding = SUBJECT.replace(
            binding,
            minimum_executed_tests=42,
            required_environment=(
                "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE",
                "DPS_TEST_POSTGRES",
            ),
        )
        policy = SUBJECT.replace(
            policy,
            integration={**policy.integration, key: hardened_binding},
        )
        candidate = SUBJECT.replace(
            candidate, integration_policy=hardened_binding
        )
        inventory = SUBJECT.replace(inventory, suites=(candidate,))
        runtime_environment = {
            "DPS_TEST_POSTGRES": (
                "Host=127.0.0.1;Port=5432;Database=dps;Username=dps;Password=x"
            )
        }

        with mock.patch.object(SUBJECT, "execute_manifest_suite") as execute:
            results = SUBJECT.execute_candidate_suites(
                ROOT,
                inventory,
                policy,
                SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"]),
                True,
                "instruction:" + "3" * 32,
                runtime_environment,
                True,
                30,
            )

        execute.assert_not_called()
        self.assertEqual(1, len(results))
        self.assertEqual("INFRA_ERROR", results[0]["status"])
        self.assertEqual(127, results[0]["exit_code"])
        self.assertEqual(0, results[0]["executed_tests"])
        self.assertEqual(
            ["DPS_TEST_POSTGRES"], results[0]["forwarded_environment_keys"]
        )
        self.assertIn(
            "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE", results[0]["log"]
        )

    def test_os_and_value_execution_errors_become_infra_error(self):
        policy, inventory = self._contract_inventory_slice()
        for error in (
            OSError("executor unavailable"),
            ValueError("invalid plan"),
            RuntimeError("runtime failed"),
            KeyError("missing detail"),
            TypeError("bad detail"),
            subprocess.SubprocessError("subprocess failed"),
        ):
            with self.subTest(error=type(error).__name__), mock.patch.object(
                SUBJECT, "execute_manifest_suite", side_effect=error
            ):
                results = SUBJECT.execute_candidate_suites(
                    ROOT,
                    inventory,
                    policy,
                    SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"]),
                    True,
                    "instruction:" + "2" * 32,
                    {},
                    False,
                    30,
                )
            self.assertEqual(1, len(results))
            self.assertEqual("INFRA_ERROR", results[0]["status"])
            self.assertEqual(127, results[0]["exit_code"])

    def test_process_control_exceptions_are_not_swallowed(self):
        policy, inventory = self._contract_inventory_slice()
        for error in (KeyboardInterrupt(), SystemExit(9)):
            with self.subTest(error=type(error).__name__), mock.patch.object(
                SUBJECT, "execute_manifest_suite", side_effect=error
            ), self.assertRaises(type(error)):
                SUBJECT.execute_candidate_suites(
                    ROOT,
                    inventory,
                    policy,
                    SUBJECT._candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"]),
                    True,
                    "instruction:" + "3" * 32,
                    {},
                    False,
                    30,
                )


if __name__ == "__main__":
    unittest.main()
