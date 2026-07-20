#!/usr/bin/env python3
"""Adversarial fixtures for the unique Phase 0 gate."""

from __future__ import annotations

import json
import hashlib
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = ROOT / "Tools" / "ci"
_ORIGINAL_IMPORT_PATH = list(sys.path)
try:
    if str(CI_DIRECTORY) not in sys.path:
        sys.path.insert(0, str(CI_DIRECTORY))

    import phase0 as phase0_module  # noqa: E402
    from phase0 import (  # noqa: E402
        CommandResult,
        Phase0Error,
        RELEASE_BOM_COMMIT_READER,
        build_compatibility_matrix,
        build_compatibility_snapshot,
        check_from_command,
        evaluate_checks,
        new_check,
        node_version,
        load_module_records_without_schema,
        run_command,
        resolve_instruction_receipt,
        validate_ci_integrity,
        validate_governance,
        validate_instruction_receipt,
        validate_json_schema,
    )
    from run_phase0_gate import (  # noqa: E402
        EvidencePublication,
        _default_phase0_evidence_path,
        _load_committed_json_object_with_sha,
        _new_publication_run_id,
        _publication_marker_path,
        _restore_failure_is_infrastructure,
        _safe_phase0_output_path,
        _trusted_dotnet_executable,
        _trusted_node_executable,
        _trusted_test_environment,
        _trusted_test_environment_scope,
        evidence_classification,
        enforce_unittest_evidence,
        execute_manifest_suite,
        parse_manifest_suite_command,
        run_locked_solution_build,
        run_required_module_static_tests,
        write_evidence,
        workspace_cleanliness_check,
    )
finally:
    sys.path[:] = _ORIGINAL_IMPORT_PATH
    del _ORIGINAL_IMPORT_PATH


def run_git(root: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", *arguments],
        cwd=str(root),
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=True,
    )
    return completed.stdout.strip()


def agents_text(module_id: str) -> str:
    return """---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: {0}
manifest: ./module.yaml
applies_to: .
---

# {0}

This fixture may not weaken the root policy.
Read module.yaml, contracts, compatibility, tests, communication, canary rollout,
and rollback evidence before changing this module.
""".format(module_id)


def module_manifest(
    module_id: str,
    owned_paths=None,
    dependencies=None,
    provides=None,
    consumes=None,
):
    def with_default_modes(items):
        result = []
        for item in items or []:
            if isinstance(item, dict):
                normalized = dict(item)
                normalized.setdefault("mode", "active")
                result.append(normalized)
            else:
                result.append(item)
        return result

    return {
        "apiVersion": "dps.module/v1",
        "kind": "Module",
        "metadata": {"id": module_id},
        "ownership": {"paths": owned_paths or ["Modules/{0}/**".format(module_id)]},
        "dependencies": {"runtime": dependencies or []},
        "contracts": {
            "provides": with_default_modes(provides),
            "consumes": with_default_modes(consumes),
        },
    }


def communication_edge(
    peer: str, contract_id: str, direction: str, major: int = 1
):
    return {
        "peer_module": peer,
        "contract_id": contract_id,
        "major": major,
        "direction": direction,
        "transport": "event",
        "timeout": "2s",
        "retry_policy": "same-idempotency-key",
        "idempotency_key": "idempotency_key",
        "auth_scope": "module:" + peer,
        "failure_mode": "fail-closed",
    }


def common_contract_schema(contract_id: str, producer: str, major: int = 1):
    properties = {
        "schema_version": {"const": "{0}.0.0".format(major)},
        "contract_id": {"const": contract_id + "/v{0}".format(major)},
        "producer_module": {"const": producer},
        "soul_id": {
            "type": ["string", "null"],
            "pattern": "^soul_[a-f0-9]{64}$(?![\\s\\S])",
        },
        "device_binding_id": {
            "type": ["string", "null"],
            "pattern": "^db_[a-f0-9]{32}$(?![\\s\\S])",
        },
        "platform_account_id": {
            "type": ["string", "null"],
            "pattern": "^pa_[a-f0-9]{32}$(?![\\s\\S])",
        },
        "trace_id": {"type": "string", "pattern": "^trace_[a-f0-9]{32}$(?![\\s\\S])"},
        "idempotency_key": {"type": "string", "pattern": "^idem_[a-f0-9]{64}$(?![\\s\\S])"},
        "occurred_at": {"type": "string"},
        "privacy_class": {"type": "string"},
    }
    return {
        "type": "object",
        "additionalProperties": False,
        "required": sorted(properties),
        "properties": properties,
    }


class RepositoryFixture:
    def __init__(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="dps-phase0-")
        self.root = Path(self.temporary.name)
        (self.root / "Modules").mkdir(parents=True)
        (self.root / "AGENTS.md").write_text("# Root policy\n", encoding="utf-8")
        policy_source = ROOT / "governance/policies/compatibility-policy.yaml"
        policy_path = self.root / "governance/policies/compatibility-policy.yaml"
        policy_path.parent.mkdir(parents=True, exist_ok=True)
        policy_path.write_bytes(policy_source.read_bytes())
        run_git(self.root, "init", "-q")
        run_git(self.root, "config", "user.email", "phase0@example.invalid")
        run_git(self.root, "config", "user.name", "Phase0 Fixture")

    def add_module(self, module_id: str, manifest=None):
        module_root = self.root / "Modules" / module_id
        module_root.mkdir(parents=True, exist_ok=True)
        layout_files = {
            "src/implementation.py": "VALUE = 1\n",
            "contracts/provided/.gitkeep": "tracked\n",
            "contracts/consumed/.gitkeep": "tracked\n",
            "tests/test_smoke.py": "import unittest\n\nclass Smoke(unittest.TestCase):\n    def test_ok(self):\n        self.assertTrue(True)\n",
            "migrations/README.md": "No migrations.\n",
            "operations/README.md": "No operations.\n",
            "CHANGELOG.md": "# Changelog\n",
        }
        for relative_path, content in layout_files.items():
            path = module_root / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
        (module_root / "AGENTS.md").write_text(
            agents_text(module_id), encoding="utf-8"
        )
        (module_root / "module.yaml").write_text(
            json.dumps(manifest or module_manifest(module_id), indent=2) + "\n",
            encoding="utf-8",
        )
        return module_root

    def commit(self):
        run_git(self.root, "add", "-A")
        run_git(self.root, "commit", "-qm", "fixture baseline")
        return run_git(self.root, "rev-parse", "HEAD")

    def close(self):
        self.temporary.cleanup()


class GovernanceSnapshotCanonicalBytesTests(unittest.TestCase):
    def test_equivalent_but_noncanonical_json_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="dps-snapshot-bytes-") as temporary:
            root = Path(temporary)
            relative_path = "governance/modules/compatibility.yaml"
            path = root / relative_path
            path.parent.mkdir(parents=True)
            expected = {"schemaVersion": "fixture/v1", "rows": [{"b": 2, "a": 1}]}
            path.write_text(
                json.dumps(expected, sort_keys=True, separators=(",", ":")),
                encoding="utf-8",
            )
            with mock.patch.object(
                phase0_module,
                "governance_snapshots",
                return_value={relative_path: expected},
            ):
                with self.assertRaisesRegex(Phase0Error, "non-canonical"):
                    phase0_module.validate_governance_snapshots(root, {})

    def test_generator_canonical_bytes_are_accepted(self):
        with tempfile.TemporaryDirectory(prefix="dps-snapshot-bytes-") as temporary:
            root = Path(temporary)
            relative_path = "governance/modules/compatibility.yaml"
            path = root / relative_path
            path.parent.mkdir(parents=True)
            expected = {"schemaVersion": "fixture/v1", "rows": [{"b": 2, "a": 1}]}
            path.write_text(
                json.dumps(expected, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            with mock.patch.object(
                phase0_module,
                "governance_snapshots",
                return_value={relative_path: expected},
            ):
                result = phase0_module.validate_governance_snapshots(root, {})
            self.assertEqual([relative_path], result["files"])


class GovernanceAdversarialTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()

    def tearDown(self):
        self.fixture.close()

    def write_contract_schema(
        self, source: str, contract_id: str, producer: str, major: int = 1
    ):
        path = self.fixture.root / source
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            json.dumps(common_contract_schema(contract_id, producer, major)),
            encoding="utf-8",
        )

    @staticmethod
    def contract_item(
        contract_id: str,
        major: int,
        source: str,
        owner: str,
        mode: str,
    ):
        return {
            "contractId": contract_id,
            "major": major,
            "source": source,
            "status": "proposed",
            "ownerModule": owner,
            "mode": mode,
        }

    def test_valid_module_governance_passes(self):
        self.fixture.add_module("alpha")
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual(["alpha"], result["modules"])

    def test_missing_agents_is_rejected(self):
        module_root = self.fixture.root / "Modules" / "alpha"
        module_root.mkdir()
        (module_root / "module.yaml").write_text(
            json.dumps(module_manifest("alpha")), encoding="utf-8"
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "missing AGENTS"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_unregistered_kebab_module_directory_is_rejected(self):
        payload = self.fixture.root / "Modules" / "rogue-module" / "src" / "payload.py"
        payload.parent.mkdir(parents=True)
        payload.write_text("VALUE = 1\n", encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "missing AGENTS"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_invalid_manifest_is_rejected(self):
        module_root = self.fixture.add_module("alpha")
        (module_root / "module.yaml").write_text("not: json\n", encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "JSON-compatible YAML"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_nested_agents_is_rejected(self):
        module_root = self.fixture.add_module("alpha")
        nested = module_root / "src"
        nested.mkdir(exist_ok=True)
        (nested / "AGENTS.md").write_text("# forbidden\n", encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "nested AGENTS"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_missing_standard_module_layout_is_rejected(self):
        module_root = self.fixture.add_module("alpha")
        (module_root / "migrations" / "README.md").unlink()
        (module_root / "migrations").rmdir()
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "missing standard module layout"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_placeholder_only_src_is_rejected(self):
        module_root = self.fixture.add_module("alpha")
        (module_root / "src" / "implementation.py").unlink()
        (module_root / "src" / ".gitkeep").write_text("tracked\n", encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "no substantive implementation"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_standard_layout_symlink_is_rejected(self):
        module_root = self.fixture.add_module("alpha")
        (module_root / "operations" / "README.md").unlink()
        (module_root / "operations").rmdir()
        outside = self.fixture.root / "outside-operations"
        outside.mkdir()
        (outside / "README.md").write_text("outside\n", encoding="utf-8")
        (module_root / "operations").symlink_to(outside, target_is_directory=True)
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "unsafe standard module layout"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_ignored_only_standard_directory_is_not_reproducible(self):
        module_root = self.fixture.add_module("alpha")
        (module_root / "operations" / "README.md").unlink()
        (module_root / "operations" / "local.tmp").write_text(
            "ignored\n", encoding="utf-8"
        )
        (self.fixture.root / ".gitignore").write_text(
            "Modules/alpha/operations/*\n", encoding="utf-8"
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "not reproducible from repository files"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_missing_runtime_entrypoint_is_rejected(self):
        manifest = module_manifest("alpha")
        manifest["runtime"] = {"entrypoints": ["Modules/alpha/src/missing.py"]}
        self.fixture.add_module("alpha", manifest)
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "runtime.entrypoints path is missing"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_missing_artifact_build_path_is_rejected(self):
        manifest = module_manifest("alpha")
        manifest["artifacts"] = [
            {
                "id": "alpha.service",
                "kind": "service",
                "build": "python3 Modules/alpha/src/missing.py",
            }
        ]
        self.fixture.add_module("alpha", manifest)
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, r"artifact\[0\].*path is missing"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_cross_module_src_project_reference_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        beta_project = beta / "src" / "Beta.csproj"
        beta_project.write_text("<Project />\n", encoding="utf-8")
        alpha_project = alpha / "src" / "Alpha.csproj"
        alpha_project.write_text(
            '<Project><ItemGroup><ProjectReference Include="../../beta/src/Beta.csproj" /></ItemGroup></Project>\n',
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "cross-module production ProjectReference"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_test_project_may_reference_another_module_source(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "src" / "Beta.csproj").write_text("<Project />\n", encoding="utf-8")
        (alpha / "tests" / "Alpha.Tests.csproj").write_text(
            '<Project><ItemGroup><ProjectReference Include="../../beta/src/Beta.csproj" /></ItemGroup></Project>\n',
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual(["alpha", "beta"], result["modules"])

    def test_source_project_may_reference_provider_contract_project(self):
        alpha = self.fixture.add_module("alpha")
        beta_manifest = module_manifest("beta")
        beta_manifest["artifacts"] = [
            {
                "id": "beta.contracts",
                "kind": "contract-pack",
                "build": "dotnet build Modules/beta/contracts/provided/Beta.Contracts.csproj",
            }
        ]
        beta = self.fixture.add_module("beta", beta_manifest)
        contract = beta / "contracts" / "provided" / "Beta.Contracts.csproj"
        contract.write_text("<Project />\n", encoding="utf-8")
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><ProjectReference Include="../../beta/contracts/provided/Beta.Contracts.csproj" /></ItemGroup></Project>\n',
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual(["alpha", "beta"], result["modules"])

    def test_cross_module_friend_assembly_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (alpha / "src" / "Alpha.csproj").write_text(
            "<Project><PropertyGroup><AssemblyName>Alpha</AssemblyName></PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        (alpha / "src" / "AssemblyInfo.cs").write_text(
            'using System.Runtime.CompilerServices;\n[assembly: InternalsVisibleTo("Beta.Tests")]\n',
            encoding="utf-8",
        )
        (beta / "tests" / "Beta.Tests.csproj").write_text(
            "<Project><PropertyGroup><AssemblyName>Beta.Tests</AssemblyName></PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "source InternalsVisibleTo"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_same_module_test_friend_assembly_is_allowed(self):
        alpha = self.fixture.add_module("alpha")
        (alpha / "src" / "Alpha.csproj").write_text(
            "<Project><ItemGroup><InternalsVisibleTo Include=\"Alpha.Tests\" /></ItemGroup></Project>\n",
            encoding="utf-8",
        )
        (alpha / "tests" / "Alpha.Tests.csproj").write_text(
            "<Project><PropertyGroup><AssemblyName>Alpha.Tests</AssemblyName></PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual(["alpha"], result["modules"])

    def test_verbatim_or_computed_source_friend_is_rejected(self):
        for payload in (
            '[assembly: InternalsVisibleTo(@"Beta.Tests")]\n',
            '[assembly: InternalsVisibleTo("Alpha.Tests" + ".Evil")]\n',
            '[assembly: InternalsVisibleTo(nameof(Alpha) + ".Tests")]\n',
        ):
            with self.subTest(payload=payload):
                alpha = self.fixture.add_module("alpha")
                (alpha / "src" / "Alpha.csproj").write_text(
                    "<Project />\n", encoding="utf-8"
                )
                (alpha / "src" / "AssemblyInfo.cs").write_text(
                    payload, encoding="utf-8"
                )
                self.fixture.commit()
                with self.assertRaisesRegex(Phase0Error, "source InternalsVisibleTo"):
                    validate_governance(self.fixture.root, require_schema=False)
                self.fixture.close()
                self.fixture = RepositoryFixture()

    def test_msbuild_friend_indirection_in_contract_project_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "tests" / "Beta.Tests.csproj").write_text(
            "<Project />\n", encoding="utf-8"
        )
        contract = alpha / "contracts" / "provided"
        contract.mkdir(parents=True, exist_ok=True)
        (contract / "Alpha.Contracts.csproj").write_text(
            "<Project><ItemGroup><AssemblyAttribute "
            "Include=\"System.Runtime.CompilerServices.InternalsVisibleToAttribute\">"
            "<_Parameter1>Beta.Tests</_Parameter1></AssemblyAttribute></ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "AssemblyAttribute indirection"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_production_reference_to_other_module_test_project_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        target = beta / "tests" / "Beta.Tests.csproj"
        target.write_text("<Project />\n", encoding="utf-8")
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><ProjectReference Include="../../beta/tests/Beta.Tests.csproj" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "production ProjectReference"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_production_compile_include_cannot_escape_module(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "src" / "Friend.cs").write_text("class Friend {}\n", encoding="utf-8")
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><Compile Include="../../beta/src/Friend.cs" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "Compile Include escapes"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_production_import_cannot_escape_module(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "src" / "Build.targets").write_text(
            "<Project />\n", encoding="utf-8"
        )
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><Import Project="../../beta/src/Build.targets" /></Project>\n',
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "Import escapes"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_unicode_escaped_friend_identifier_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        (alpha / "src" / "Alpha.csproj").write_text(
            "<Project />\n", encoding="utf-8"
        )
        (alpha / "src" / "AssemblyInfo.cs").write_text(
            '[assembly: Internals\\u0056isibleTo("Beta.Tests")]\n',
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "source InternalsVisibleTo"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_unrelated_unicode_escape_in_source_is_allowed(self):
        alpha = self.fixture.add_module("alpha")
        (alpha / "src" / "Alpha.csproj").write_text(
            "<Project />\n", encoding="utf-8"
        )
        (alpha / "src" / "Localized.cs").write_text(
            'class Localized { const string Value = "\\u4e2d"; }\n',
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual(["alpha"], result["modules"])

    def test_cross_module_consumed_contract_project_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        consumed = beta / "contracts" / "consumed" / "Beta.Internal.csproj"
        consumed.write_text("<Project />\n", encoding="utf-8")
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><ProjectReference Include="../../beta/contracts/consumed/Beta.Internal.csproj" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "production ProjectReference"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_backslash_compile_escape_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "src" / "Friend.cs").write_text("class Friend {}\n", encoding="utf-8")
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><Compile Include="..\\..\\beta\\src\\Friend.cs" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "Compile Include escapes"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_windows_absolute_compile_path_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><Compile Include="C:\\outside\\Friend.cs" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "repository-relative"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_local_compile_symlink_escape_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "src" / "Friend.cs").write_text("class Friend {}\n", encoding="utf-8")
        (alpha / "src" / "linked").symlink_to(beta / "src", target_is_directory=True)
        (alpha / "src" / "Alpha.csproj").write_text(
            '<Project><ItemGroup><Compile Include="linked/Friend.cs" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "may not traverse a symlink"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_root_directory_build_props_friend_is_governed(self):
        alpha = self.fixture.add_module("alpha")
        beta = self.fixture.add_module("beta")
        (beta / "tests" / "Beta.Tests.csproj").write_text(
            "<Project />\n", encoding="utf-8"
        )
        (alpha / "Directory.Build.props").write_text(
            '<Project><ItemGroup><InternalsVisibleTo Include="Beta.Tests" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "InternalsVisibleTo"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_dynamic_assembly_attribute_type_is_rejected(self):
        alpha = self.fixture.add_module("alpha")
        (alpha / "Directory.Build.props").write_text(
            '<Project><ItemGroup><AssemblyAttribute Include="$(FriendAttribute)">'
            "<_Parameter1>Alpha.Tests</_Parameter1></AssemblyAttribute>"
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "type must be a literal"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_overlapping_ownership_is_rejected(self):
        self.fixture.add_module(
            "alpha", module_manifest("alpha", owned_paths=["shared/**"])
        )
        self.fixture.add_module(
            "beta", module_manifest("beta", owned_paths=["shared/**"])
        )
        shared = self.fixture.root / "shared"
        shared.mkdir()
        (shared / "runtime.py").write_text("VALUE = 1\n", encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "multiple module owners"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_future_prefix_overlap_is_rejected_without_existing_file(self):
        self.fixture.add_module(
            "alpha", module_manifest("alpha", owned_paths=["future/**"])
        )
        self.fixture.add_module(
            "beta", module_manifest("beta", owned_paths=["future/subsystem/**"])
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "ownership patterns overlap"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_unknown_dependency_is_rejected(self):
        self.fixture.add_module(
            "alpha", module_manifest("alpha", dependencies=["missing-module"])
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "unknown dependencies"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_dependency_cycle_is_rejected(self):
        self.fixture.add_module(
            "alpha", module_manifest("alpha", dependencies=["beta"])
        )
        self.fixture.add_module(
            "beta", module_manifest("beta", dependencies=["alpha"])
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "dependency cycle"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_multiple_contract_owners_are_rejected(self):
        contract = [{"id": "soul.resolved/v1", "versions": [1]}]
        self.fixture.add_module(
            "alpha", module_manifest("alpha", provides=contract)
        )
        self.fixture.add_module("beta", module_manifest("beta", provides=contract))
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "multiple owners"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_contract_major_mode_is_required_and_unknown_mode_fails_closed(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        for mode, message in ((None, "unknown or missing"), ("future", "unknown or missing")):
            with self.subTest(mode=mode):
                item = self.contract_item("example.event", 1, source, "alpha", "active")
                if mode is None:
                    del item["mode"]
                else:
                    item["mode"] = mode
                manifest = module_manifest("alpha", provides=[item])
                if mode is None:
                    del manifest["contracts"]["provides"][0]["mode"]
                self.fixture.add_module("alpha", manifest)
                self.write_contract_schema(source, "example.event", "alpha")
                self.fixture.commit()
                with self.assertRaisesRegex(Phase0Error, message):
                    validate_governance(self.fixture.root, require_schema=False)
                self.fixture.close()
                self.fixture = RepositoryFixture()

    def test_compat_read_is_forbidden_for_provided_contracts(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        item = self.contract_item(
            "example.event", 1, source, "alpha", "compat-read"
        )
        self.fixture.add_module("alpha", module_manifest("alpha", provides=[item]))
        self.write_contract_schema(source, "example.event", "alpha")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "provided contract .* cannot use compat-read"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_v1_quarantine_v2_active_never_satisfies_previous_runnable_window(self):
        v1 = "Modules/alpha/contracts/provided/example.v1.schema.json"
        v2 = "Modules/alpha/contracts/provided/example.v2.schema.json"
        provided = [
            self.contract_item("example.event", 1, v1, "alpha", "quarantine-only"),
            self.contract_item("example.event", 2, v2, "alpha", "active"),
        ]
        consumed = [
            self.contract_item("example.event", 1, v1, "alpha", "quarantine-only"),
            self.contract_item("example.event", 2, v2, "alpha", "active"),
        ]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound", 1),
            communication_edge("beta", "example.event", "outbound", 2),
        ]
        self.fixture.add_module("alpha", alpha)
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound", 1),
            communication_edge("alpha", "example.event", "inbound", 2),
        ]
        self.fixture.add_module(
            "beta",
            beta,
        )
        self.write_contract_schema(v1, "example.event", "alpha", 1)
        self.write_contract_schema(v2, "example.event", "alpha", 2)
        self.fixture.commit()

        records = load_module_records_without_schema(self.fixture.root)
        row = build_compatibility_matrix(records)[0]
        snapshot = build_compatibility_snapshot(records)
        snapshot_schema = json.loads(
            (ROOT / "governance/verification/f9-compatibility-matrix.v2.schema.json")
            .read_text(encoding="utf-8")
        )
        self.assertEqual([], validate_json_schema(snapshot, snapshot_schema))
        self.assertEqual(
            hashlib.sha256(
                (self.fixture.root / "governance/policies/compatibility-policy.yaml")
                .read_bytes()
            ).hexdigest(),
            snapshot["policySha256"],
        )
        self.assertFalse(snapshot["independentDeployable"])
        self.assertTrue(snapshot["compatibilityGroupRequired"])
        self.assertFalse(snapshot["candidateGreenEligible"])
        previous = row["declaration_matrix"][
            "previous_producer_to_current_consumer"
        ]
        self.assertEqual("quarantine-only", previous["producer_mode"])
        self.assertFalse(previous["runnable"])
        self.assertEqual("FAIL", previous["result"])
        self.assertFalse(row["declaration_deployable"])
        self.assertFalse(row["independent_deployable"])
        self.assertTrue(row["compatibility_group_required"])
        self.assertFalse(row["candidate_green_eligible"])
        self.assertEqual(
            {"N/N": "NOT_RUN", "N/N-1": "NOT_RUN", "N-1/N": "NOT_RUN", "N-1/N-1": "NOT_RUN"},
            row["execution_combinations"],
        )
        validated = validate_governance(self.fixture.root, require_schema=False)
        self.assertTrue(
            validated["compatibility_matrix"][0]["compatibility_group_required"]
        )

    def test_compat_read_is_readable_but_never_runnable_or_candidate_green(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [self.contract_item("example.event", 1, source, "alpha", "active")]
        consumed = [
            self.contract_item("example.event", 1, source, "alpha", "compat-read")
        ]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound")
        ]
        self.fixture.add_module("alpha", alpha)
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound")
        ]
        self.fixture.add_module(
            "beta", beta
        )
        self.write_contract_schema(source, "example.event", "alpha")
        self.fixture.commit()

        row = build_compatibility_matrix(
            load_module_records_without_schema(self.fixture.root)
        )[0]
        current = row["declaration_matrix"][
            "current_producer_to_current_consumer"
        ]
        self.assertTrue(current["readable"])
        self.assertFalse(current["runnable"])
        self.assertFalse(row["declaration_deployable"])
        self.assertFalse(row["candidate_green_eligible"])
        snapshot = build_compatibility_snapshot(
            load_module_records_without_schema(self.fixture.root)
        )
        schema = json.loads(
            (ROOT / "governance/verification/f9-compatibility-matrix.v2.schema.json")
            .read_text(encoding="utf-8")
        )
        self.assertEqual([], validate_json_schema(snapshot, schema))
        self.assertTrue(snapshot["declarationMatrix"][0]["readCompatible"])

    def test_unknown_n_plus_1_is_rejected_and_cannot_be_candidate_green(self):
        v2 = "Modules/alpha/contracts/provided/example.v2.schema.json"
        v3 = "Modules/alpha/contracts/provided/example.v3.schema.json"
        provided = [self.contract_item("example.event", 2, v2, "alpha", "active")]
        consumed = [self.contract_item("example.event", 3, v3, "alpha", "active")]
        self.fixture.add_module("alpha", module_manifest("alpha", provides=provided))
        self.fixture.add_module(
            "beta", module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        )
        self.write_contract_schema(v2, "example.event", "alpha", 2)
        self.fixture.commit()

        row = build_compatibility_matrix(
            load_module_records_without_schema(self.fixture.root)
        )[0]
        self.assertEqual("REJECT", row["unknown_N_plus_1"])
        self.assertFalse(row["candidate_green_eligible"])
        with self.assertRaisesRegex(Phase0Error, "contract major without owner"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_retired_major_remains_owned_but_is_not_active_or_deployable(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [self.contract_item("example.event", 1, source, "alpha", "retired")]
        consumed = [self.contract_item("example.event", 1, source, "alpha", "retired")]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound")
        ]
        self.fixture.add_module("alpha", alpha)
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound")
        ]
        self.fixture.add_module(
            "beta", beta
        )
        self.write_contract_schema(source, "example.event", "alpha")
        self.fixture.commit()

        records = load_module_records_without_schema(self.fixture.root)
        row = build_compatibility_matrix(records)[0]
        self.assertIsNone(row["current_active_major"])
        self.assertFalse(row["declaration_deployable"])
        self.assertFalse(row["candidate_green_eligible"])
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual("alpha", result["contract_major_owners"]["example.event/v1"])

    def test_contract_missing_common_envelope_is_rejected(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        contract = [
            {
                "contractId": "example.event",
                "major": 1,
                "source": source,
                "status": "proposed",
                "ownerModule": "alpha",
                "mode": "quarantine-only",
            }
        ]
        module_root = self.fixture.add_module(
            "alpha", module_manifest("alpha", provides=contract)
        )
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        contract_path.write_text(
            json.dumps(
                {
                    "type": "object",
                    "required": ["contract_id"],
                    "properties": {
                        "contract_id": {"const": "example.event/v1"}
                    },
                }
            ),
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "lacks common"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_contract_owner_and_producer_are_distinct_concepts(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        contract = [
            {
                "contractId": "example.command",
                "major": 1,
                "source": source,
                "status": "proposed",
                "ownerModule": "alpha",
                "mode": "quarantine-only",
            }
        ]
        module_root = self.fixture.add_module(
            "alpha", module_manifest("alpha", provides=contract)
        )
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        properties = {
            "soul_id": {
                "type": ["string", "null"],
                "pattern": "^soul_[a-f0-9]{64}$(?![\\s\\S])",
            },
            "device_binding_id": {
                "type": ["string", "null"],
                "pattern": "^db_[a-f0-9]{32}$(?![\\s\\S])",
            },
            "platform_account_id": {
                "type": ["string", "null"],
                "pattern": "^pa_[a-f0-9]{32}$(?![\\s\\S])",
            },
        }
        properties.update(
            {
                "schema_version": {"const": "1.0.0"},
                "contract_id": {"const": "example.command/v1"},
                "producer_module": {"const": "external"},
                "trace_id": {"type": "string", "pattern": "^trace_[a-f0-9]{32}$(?![\\s\\S])"},
                "idempotency_key": {"type": "string", "pattern": "^idem_[a-f0-9]{64}$(?![\\s\\S])"},
                "occurred_at": {"type": "string"},
                "privacy_class": {"type": "string"},
            }
        )
        contract_path.write_text(
            json.dumps(
                {
                    "type": "object",
                    "additionalProperties": False,
                    "required": sorted(properties),
                    "properties": properties,
                }
            ),
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual({"example.command": "alpha"}, result["contract_owners"])

    def test_contract_producer_must_be_constrained(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        contract = [
            {
                "contractId": "example.event",
                "major": 1,
                "source": source,
                "status": "proposed",
                "ownerModule": "alpha",
            }
        ]
        self.fixture.add_module("alpha", module_manifest("alpha", provides=contract))
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        properties = {field: {"type": "string"} for field in (
            "schema_version", "producer_module", "soul_id", "device_binding_id",
            "platform_account_id", "trace_id", "idempotency_key", "occurred_at",
            "privacy_class",
        )}
        properties["contract_id"] = {"const": "example.event/v1"}
        contract_path.write_text(
            json.dumps(
                {
                    "type": "object",
                    "additionalProperties": False,
                    "required": sorted(properties),
                    "properties": properties,
                }
            ),
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "producer_module const or enum"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_contract_identity_uuid_drift_is_rejected(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        contract = [
            {
                "contractId": "example.event",
                "major": 1,
                "source": source,
                "status": "proposed",
                "ownerModule": "alpha",
            }
        ]
        self.fixture.add_module("alpha", module_manifest("alpha", provides=contract))
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        schema = common_contract_schema("example.event", "alpha")
        schema["properties"]["soul_id"] = {"type": "string", "format": "uuid"}
        contract_path.write_text(json.dumps(schema), encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "must constrain soul_id"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_all_opaque_identifier_patterns_are_exact_and_absolute(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        contract = [{
            "contractId": "example.event",
            "major": 1,
            "source": source,
            "status": "proposed",
            "ownerModule": "alpha",
        }]
        self.fixture.add_module("alpha", module_manifest("alpha", provides=contract))
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        baseline = common_contract_schema("example.event", "alpha")
        contract_path.write_text(json.dumps(baseline), encoding="utf-8")
        self.fixture.commit()

        loose_patterns = {
            "soul_id": "^soul_[a-f0-9]{64}$",
            "device_binding_id": "^db_[A-Za-z0-9_-]{1,125}$",
            "platform_account_id": "^pa_[A-Za-z0-9_-]{1,125}$",
            "trace_id": "^trace_.+$",
            "idempotency_key": "^idem_.+$",
        }
        for field_name, loose_pattern in loose_patterns.items():
            with self.subTest(field_name=field_name):
                candidate = json.loads(json.dumps(baseline))
                candidate["properties"][field_name]["pattern"] = loose_pattern
                contract_path.write_text(json.dumps(candidate), encoding="utf-8")
                with self.assertRaisesRegex(Phase0Error, "must constrain " + field_name):
                    validate_governance(self.fixture.root, require_schema=False)

    def test_internal_communication_requires_reciprocal_edge(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound")
        ]
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        contract_path.write_text(
            json.dumps(common_contract_schema("example.event", "alpha")),
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "lacks reciprocal"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_reciprocal_communication_and_producer_direction_pass(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound")
        ]
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound")
        ]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        contract_path.write_text(
            json.dumps(common_contract_schema("example.event", "alpha")),
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual({"example.event": "alpha"}, result["contract_owners"])
        row = result["compatibility_matrix"][0]
        self.assertEqual("alpha", row["owner_module"])
        self.assertEqual("alpha", row["producer_module"])
        self.assertEqual("alpha", row["transport_sender_module"])
        self.assertEqual("beta", row["transport_receiver_module"])
        self.assertEqual(
            "schema-producer-is-transport-sender",
            row["producer_resolution"],
        )
        self.assertTrue(row["reciprocal_resolved"])
        self.assertRegex(row["communication_pair_sha256"], r"^[a-f0-9]{64}$")

    def test_runtime_producer_is_resolved_from_schema_not_contract_owner(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.command", "major": 1, "source": source}]
        produced_by_beta = [
            {"contractId": "example.command", "major": 1, "source": source}
        ]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.command", "inbound")
        ]
        beta = module_manifest(
            "beta", dependencies=["alpha"], consumes=produced_by_beta
        )
        beta["communication"] = [
            communication_edge("alpha", "example.command", "outbound")
        ]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        self.write_contract_schema(source, "example.command", "beta")
        self.fixture.commit()

        result = validate_governance(self.fixture.root, require_schema=False)
        row = result["compatibility_matrix"][0]
        self.assertEqual("alpha", row["owner_module"])
        self.assertEqual("beta", row["producer_module"])
        self.assertEqual("beta", row["transport_sender_module"])
        self.assertEqual("alpha", row["consumer_module"])
        self.assertTrue(row["independent_deployable"])

    def test_module_auth_scope_must_target_declared_peer(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        alpha = module_manifest("alpha", provides=provided)
        outbound = communication_edge("beta", "example.event", "outbound")
        outbound["auth_scope"] = "module:alpha"
        alpha["communication"] = [outbound]
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [communication_edge("alpha", "example.event", "inbound")]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        contract_path.write_text(
            json.dumps(common_contract_schema("example.event", "alpha")),
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "auth scope must target peer"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_reciprocal_transport_and_timeout_must_match(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        for field, value, message in (
            ("transport", "command", "transport conflicts"),
            ("timeout", "3s", "timeout conflicts"),
        ):
            with self.subTest(field=field):
                alpha = module_manifest("alpha", provides=provided)
                alpha["communication"] = [communication_edge("beta", "example.event", "outbound")]
                beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
                inbound = communication_edge("alpha", "example.event", "inbound")
                inbound[field] = value
                beta["communication"] = [inbound]
                self.fixture.add_module("alpha", alpha)
                self.fixture.add_module("beta", beta)
                contract_path = self.fixture.root / source
                contract_path.parent.mkdir(parents=True, exist_ok=True)
                contract_path.write_text(
                    json.dumps(common_contract_schema("example.event", "alpha")),
                    encoding="utf-8",
                )
                self.fixture.commit()
                with self.assertRaisesRegex(Phase0Error, message):
                    validate_governance(self.fixture.root, require_schema=False)
                self.fixture.close()
                self.fixture = RepositoryFixture()

    def test_communication_direction_must_match_constrained_producer(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound")
        ]
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound")
        ]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        contract_path.write_text(
            json.dumps(common_contract_schema("example.event", "beta")),
            encoding="utf-8",
        )
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "direction conflicts"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_explicit_consuming_relay_may_preserve_original_producer(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("relay", "example.event", "outbound")
        ]
        relay = module_manifest("relay", dependencies=["alpha"], consumes=consumed)
        relay_in = communication_edge("alpha", "example.event", "inbound")
        relay_out = communication_edge("gamma", "example.event", "outbound")
        relay_out["preserveProducer"] = True
        relay["communication"] = [relay_in, relay_out]
        gamma = module_manifest(
            "gamma", dependencies=["alpha", "relay"], consumes=consumed
        )
        gamma["communication"] = [
            communication_edge("relay", "example.event", "inbound")
        ]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("relay", relay)
        self.fixture.add_module("gamma", gamma)
        contract_path = self.fixture.root / source
        contract_path.parent.mkdir(parents=True, exist_ok=True)
        contract_path.write_text(
            json.dumps(common_contract_schema("example.event", "alpha")),
            encoding="utf-8",
        )
        self.fixture.commit()
        result = validate_governance(self.fixture.root, require_schema=False)
        self.assertEqual({"example.event": "alpha"}, result["contract_owners"])
        relay_row = next(
            row
            for row in result["compatibility_matrix"]
            if row["transport_sender_module"] == "relay"
            and row["consumer_module"] == "gamma"
        )
        self.assertEqual("alpha", relay_row["producer_module"])
        self.assertEqual(
            "schema-producer-preserved-by-relay",
            relay_row["producer_resolution"],
        )
        self.assertTrue(relay_row["transport_preserves_producer"])
        self.assertTrue(relay_row["reciprocal_resolved"])
        self.assertRegex(
            relay_row["communication_pair_sha256"], r"^[a-f0-9]{64}$"
        )

    def test_preserve_producer_is_relay_only(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [{"contractId": "example.event", "major": 1, "source": source}]
        consumed = [{"contractId": "example.event", "major": 1, "source": source}]
        alpha = module_manifest("alpha", provides=provided)
        outbound = communication_edge("beta", "example.event", "outbound")
        outbound["preserveProducer"] = True
        alpha["communication"] = [outbound]
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound")
        ]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        self.write_contract_schema(source, "example.event", "alpha")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "preserveProducer is relay-only"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_compatibility_policy_is_mandatory_and_cannot_be_weakened(self):
        self.fixture.add_module("alpha")
        policy_path = (
            self.fixture.root / "governance/policies/compatibility-policy.yaml"
        )
        policy_path.unlink()
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "compatibility policy is required"):
            validate_governance(self.fixture.root, require_schema=False)

        self.fixture.close()
        self.fixture = RepositoryFixture()
        self.fixture.add_module("alpha")
        policy_path = (
            self.fixture.root / "governance/policies/compatibility-policy.yaml"
        )
        policy = json.loads(policy_path.read_text(encoding="utf-8"))
        policy["contractMajorModes"]["modes"]["quarantine-only"][
            "countsToward"
        ]["candidateGreen"] = True
        policy_path.write_text(json.dumps(policy), encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(
            Phase0Error, "quarantine-only.countsToward.candidateGreen"
        ):
            validate_governance(self.fixture.root, require_schema=False)

        self.fixture.close()
        self.fixture = RepositoryFixture()
        self.fixture.add_module("alpha")
        policy_path = (
            self.fixture.root / "governance/policies/compatibility-policy.yaml"
        )
        policy = json.loads(policy_path.read_text(encoding="utf-8"))
        policy["contractMajorModes"]["modes"]["retired"][
            "wireAction"
        ] = "decode-and-use"
        policy_path.write_text(json.dumps(policy), encoding="utf-8")
        self.fixture.commit()
        with self.assertRaisesRegex(Phase0Error, "retired.wireAction"):
            validate_governance(self.fixture.root, require_schema=False)

    def test_v2_schema_rejects_role_hash_reciprocity_and_empty_matrix_attacks(self):
        source = "Modules/alpha/contracts/provided/example.v1.schema.json"
        provided = [self.contract_item("example.event", 1, source, "alpha", "active")]
        consumed = [self.contract_item("example.event", 1, source, "alpha", "active")]
        alpha = module_manifest("alpha", provides=provided)
        alpha["communication"] = [
            communication_edge("beta", "example.event", "outbound")
        ]
        beta = module_manifest("beta", dependencies=["alpha"], consumes=consumed)
        beta["communication"] = [
            communication_edge("alpha", "example.event", "inbound")
        ]
        self.fixture.add_module("alpha", alpha)
        self.fixture.add_module("beta", beta)
        self.write_contract_schema(source, "example.event", "alpha")
        records = load_module_records_without_schema(self.fixture.root)
        snapshot = build_compatibility_snapshot(records)
        schema = json.loads(
            (ROOT / "governance/verification/f9-compatibility-matrix.v2.schema.json")
            .read_text(encoding="utf-8")
        )
        self.assertEqual([], validate_json_schema(snapshot, schema))

        attacks = []
        invalid_hash = json.loads(json.dumps(snapshot))
        invalid_hash["policySha256"] = "0" * 63
        attacks.append(invalid_hash)

        fake_reciprocal = json.loads(json.dumps(snapshot))
        fake_reciprocal["declarationMatrix"][0]["reciprocalResolved"] = False
        attacks.append(fake_reciprocal)

        fake_relay = json.loads(json.dumps(snapshot))
        fake_relay["declarationMatrix"][0][
            "producerResolution"
        ] = "schema-producer-preserved-by-relay"
        attacks.append(fake_relay)

        empty_green = json.loads(json.dumps(snapshot))
        empty_green["declarationMatrix"] = []
        empty_green["candidateGreenEligible"] = True
        attacks.append(empty_green)

        unresolved_green = json.loads(json.dumps(snapshot))
        row = unresolved_green["declarationMatrix"][0]
        row["producerResolution"] = "unresolved"
        row["communicationPairSha256"] = None
        row["reciprocalResolved"] = False
        attacks.append(unresolved_green)

        fake_unresolved_read = json.loads(json.dumps(snapshot))
        row = fake_unresolved_read["declarationMatrix"][0]
        row["producerResolution"] = "unresolved"
        row["communicationPairSha256"] = None
        row["reciprocalResolved"] = False
        row["executionClass"] = "unresolved-communication"
        row["readCompatible"] = True
        row["runnable"] = False
        row["deployable"] = False
        row["independentDeployable"] = False
        row["activeProducerConsumer"] = False
        row["candidateGreenEligible"] = False
        fake_unresolved_read["independentDeployable"] = False
        fake_unresolved_read["candidateGreenEligible"] = False
        attacks.append(fake_unresolved_read)

        for index, candidate in enumerate(attacks):
            with self.subTest(attack=index):
                self.assertTrue(validate_json_schema(candidate, schema))


class ManifestSuiteRunnerTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()
        self.module_root = self.fixture.add_module("alpha")

    def tearDown(self):
        self.fixture.close()

    @staticmethod
    def suite(command: str, test_type: str = "unit"):
        return {
            "id": "alpha." + test_type,
            "type": test_type,
            "required": True,
            "command": command,
            "environment": "synthetic fixture",
            "evidenceLevel": "REPOSITORY_STATIC_VERIFIED",
        }

    def parse(self, command: str, test_type: str = "unit"):
        return parse_manifest_suite_command(
            self.fixture.root,
            self.module_root,
            "alpha",
            self.suite(command, test_type),
        )

    def write_audited_dotnet_script(self):
        (self.fixture.root / "NuGet.Config").write_text(
            "<?xml version=\"1.0\"?><configuration><packageSources><clear />"
            "</packageSources></configuration>\n",
            encoding="utf-8",
        )
        script = self.module_root / "operations" / "test.sh"
        script.write_text(
            r"""#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "Usage: $0 <Unit|Contract|Integration>" >&2
  exit 64
fi

case "$1" in
  Unit|Contract|Integration)
    suite_category="$1"
    ;;
  *)
    echo "Unknown suite category: $1" >&2
    exit 64
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/alpha/tests/Alpha.Tests.csproj --locked-mode
bash scripts/dotnet-pinned.sh test Modules/alpha/tests/Alpha.Tests.csproj --configuration Release --no-restore -- \
  --filter-trait "Category=$suite_category" \
  --minimum-expected-tests 1 \
  --fail-skips on
""",
            encoding="utf-8",
        )
        return script

    def write_category_floor_dotnet_script(self):
        (self.fixture.root / "NuGet.Config").write_text(
            "<?xml version=\"1.0\"?><configuration><packageSources><clear />"
            "</packageSources></configuration>\n",
            encoding="utf-8",
        )
        script = self.module_root / "operations" / "test.sh"
        script.write_text(
            r"""#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "Usage: $0 <Unit|Contract|Integration>" >&2
  exit 64
fi

case "$1" in
  Unit)
    suite_category="$1"
    minimum_expected_tests=3
    ;;
  Contract)
    suite_category="$1"
    minimum_expected_tests=2
    ;;
  Integration)
    suite_category="$1"
    minimum_expected_tests=4
    ;;
  *)
    echo "Unknown suite category: $1" >&2
    exit 64
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/alpha/tests/Alpha.Tests.csproj --locked-mode
bash scripts/dotnet-pinned.sh test Modules/alpha/tests/Alpha.Tests.csproj --configuration Release --no-restore -- \
  --filter-trait "Category=$suite_category" \
  --minimum-expected-tests "$minimum_expected_tests" \
  --fail-skips on
""",
            encoding="utf-8",
        )
        return script

    def test_valid_unittest_is_executed_and_counted(self):
        plan = self.parse(
            "python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py'"
        )
        check = execute_manifest_suite(self.fixture.root, plan)
        self.assertEqual("PASS", check["status"])
        self.assertEqual(1, check["details"]["executed_tests"])

    def test_shell_metacharacter_is_rejected(self):
        with self.assertRaisesRegex(Phase0Error, "metacharacters"):
            self.parse(
                "python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py' ; touch /tmp/pwned"
            )

    def test_unknown_environment_prefix_is_rejected(self):
        with self.assertRaisesRegex(Phase0Error, "unknown manifest test environment"):
            self.parse(
                "SECRET_TOKEN=value python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py'"
            )

    def test_manifest_cannot_declare_trusted_executor_postgres_environment(self):
        for key in (
            "DPS_TEST_POSTGRES",
            "DPS_TEST_POSTGRES_URI",
            "DPS_PSQL",
        ):
            with self.subTest(key=key), self.assertRaisesRegex(
                Phase0Error, "unknown manifest test environment variable: " + key
            ):
                self.parse(
                    key
                    + "=untrusted python3 -m unittest discover "
                    + "-s Modules/alpha/tests -p 'test_*.py'"
                )

    def test_unknown_executable_is_rejected(self):
        with self.assertRaisesRegex(Phase0Error, "unknown or untrusted"):
            self.parse("env python3 Modules/alpha/tests/test_smoke.py", "static")

    def test_path_traversal_is_rejected(self):
        outside = self.fixture.root / "outside.py"
        outside.write_text("print('PASS')\n", encoding="utf-8")
        with self.assertRaisesRegex(Phase0Error, "contained repository-relative"):
            self.parse("python3 ../outside.py", "static")

    def test_symlink_escape_is_rejected(self):
        outside = self.fixture.root / "outside.py"
        outside.write_text("print('PASS')\n", encoding="utf-8")
        link = self.module_root / "tests" / "escape.py"
        link.symlink_to(outside)
        with self.assertRaisesRegex(Phase0Error, "symlink"):
            self.parse("python3 Modules/alpha/tests/escape.py", "static")

    def test_zero_unittests_fails_even_with_exit_zero(self):
        (self.module_root / "tests" / "test_smoke.py").write_text(
            "VALUE = 1\n", encoding="utf-8"
        )
        plan = self.parse(
            "python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py'"
        )
        check = execute_manifest_suite(self.fixture.root, plan)
        self.assertEqual("FAIL", check["status"])
        self.assertEqual(0, check["details"]["executed_tests"])

    def test_skipped_unittest_fails_even_with_exit_zero(self):
        (self.module_root / "tests" / "test_smoke.py").write_text(
            "import unittest\n\n"
            "class Smoke(unittest.TestCase):\n"
            "    @unittest.skip('not evidence')\n"
            "    def test_skipped(self):\n"
            "        pass\n",
            encoding="utf-8",
        )
        plan = self.parse(
            "python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py'"
        )
        check = execute_manifest_suite(self.fixture.root, plan)
        self.assertEqual("FAIL", check["status"])
        self.assertIn("skipped", check["log"].casefold())

    def test_plain_stdout_pass_is_not_test_evidence(self):
        script = self.module_root / "tests" / "fake_pass.py"
        script.write_text("print('PASS')\n", encoding="utf-8")
        plan = self.parse("python3 Modules/alpha/tests/fake_pass.py", "static")
        check = execute_manifest_suite(self.fixture.root, plan)
        self.assertEqual("FAIL", check["status"])
        self.assertEqual(0, check["details"]["executed_tests"])

    def test_json_tool_is_not_semantic_contract_evidence(self):
        schema = self.module_root / "contracts" / "provided" / "sample.schema.json"
        schema.write_text('{"type":"object"}\n', encoding="utf-8")
        with self.assertRaisesRegex(Phase0Error, "not semantic contract test evidence"):
            self.parse(
                "python3.12 -m json.tool "
                "Modules/alpha/contracts/provided/sample.schema.json",
                "static",
            )

    def test_structured_partial_is_not_pass(self):
        script = self.module_root / "tests" / "partial.py"
        script.write_text(
            "import json\nprint(json.dumps({'status': 'PARTIAL', 'test_type': 'static', "
            "'verification_level': 'REPOSITORY_STATIC_VERIFIED'}))\n",
            encoding="utf-8",
        )
        plan = self.parse("python3 Modules/alpha/tests/partial.py", "static")
        check = execute_manifest_suite(self.fixture.root, plan)
        self.assertEqual("FAIL", check["status"])

    def test_timeout_is_infrastructure_error(self):
        (self.module_root / "tests" / "test_smoke.py").write_text(
            "import time\nimport unittest\n\n"
            "class Smoke(unittest.TestCase):\n"
            "    def test_slow(self):\n"
            "        time.sleep(1)\n",
            encoding="utf-8",
        )
        plan = self.parse(
            "python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py'"
        )
        check = execute_manifest_suite(self.fixture.root, plan, timeout_seconds=0.05)
        self.assertEqual("INFRA_ERROR", check["status"])

    def test_recursive_phase0_command_is_rejected(self):
        with self.assertRaisesRegex(Phase0Error, "recursive Phase0"):
            parse_manifest_suite_command(
                self.fixture.root,
                self.module_root,
                "alpha",
                self.suite("python Tools/ci/run_phase0_gate.py", "static"),
            )

    def test_unsafe_bash_test_script_is_rejected(self):
        script = self.module_root / "operations" / "test.sh"
        script.write_text("#!/bin/bash\necho PASS\n", encoding="utf-8")
        with self.assertRaisesRegex(Phase0Error, "trusted fixed template"):
            self.parse("bash Modules/alpha/operations/test.sh Unit")

    def test_bash_suite_cannot_pass_from_a_different_test_category(self):
        project = self.module_root / "tests" / "Alpha.Tests.csproj"
        project.write_text("<Project />\n", encoding="utf-8")
        self.write_audited_dotnet_script()
        wrapper = self.fixture.root / "scripts" / "dotnet-pinned.sh"
        wrapper.parent.mkdir()
        wrapper.write_text(
            "#!/usr/bin/env bash\n"
            "if [[ \"${1:-}\" == restore ]]; then exit 0; fi\n"
            "if [[ \"$*\" == *Category=Unit* ]]; then\n"
            "  printf 'Test run summary: Passed!\\n  total: 0\\n  skipped: 0\\n'\n"
            "else\n"
            "  printf 'Test run summary: Passed!\\n  total: 1\\n  skipped: 0\\n'\n"
            "fi\n",
            encoding="utf-8",
        )
        plan = self.parse("bash Modules/alpha/operations/test.sh Unit")
        restore_effective = list(plan.invocations[0].argv)
        self.assertIn("-p:RestoreUseStaticGraphEvaluation=true", restore_effective)
        self.assertIn("-p:NuGetAudit=true", restore_effective)
        self.assertIn("-p:NuGetAuditMode=all", restore_effective)
        self.assertIn("--configfile", restore_effective)
        effective = list(plan.invocations[-1].argv)
        self.assertIn("Category=Unit", effective)
        self.assertIn("--minimum-expected-tests", effective)
        self.assertIn("--fail-skips", effective)
        check = execute_manifest_suite(self.fixture.root, plan)
        self.assertEqual("FAIL", check["status"])
        self.assertEqual(0, check["details"]["executed_tests"])

    def test_bash_category_must_match_declared_suite_type(self):
        self.write_audited_dotnet_script()
        with self.assertRaisesRegex(Phase0Error, "category must exactly match"):
            self.parse("bash Modules/alpha/operations/test.sh Contract")

    def test_bash_suite_preserves_strict_category_specific_floor(self):
        project = self.module_root / "tests" / "Alpha.Tests.csproj"
        project.write_text("<Project />\n", encoding="utf-8")
        self.write_category_floor_dotnet_script()
        wrapper = self.fixture.root / "scripts" / "dotnet-pinned.sh"
        wrapper.parent.mkdir()
        wrapper.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")

        plan = self.parse("bash Modules/alpha/operations/test.sh Unit")

        invocation = plan.invocations[-1]
        self.assertEqual(3, invocation.minimum_tests)
        floor_index = list(invocation.argv).index("--minimum-expected-tests") + 1
        self.assertEqual("3", invocation.argv[floor_index])

    def test_bash_suite_rejects_non_literal_category_floor(self):
        project = self.module_root / "tests" / "Alpha.Tests.csproj"
        project.write_text("<Project />\n", encoding="utf-8")
        script = self.write_category_floor_dotnet_script()
        script.write_text(
            script.read_text(encoding="utf-8").replace(
                "minimum_expected_tests=3",
                "minimum_expected_tests=${DPS_WEAK_FLOOR:-3}",
            ),
            encoding="utf-8",
        )
        wrapper = self.fixture.root / "scripts" / "dotnet-pinned.sh"
        wrapper.parent.mkdir()
        wrapper.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")

        with self.assertRaisesRegex(Phase0Error, "invalid category test floor"):
            self.parse("bash Modules/alpha/operations/test.sh Unit")

    def test_bash_suite_rejects_extra_argument(self):
        self.write_audited_dotnet_script()
        with self.assertRaisesRegex(Phase0Error, "exactly one category"):
            self.parse("bash Modules/alpha/operations/test.sh Unit unexpected")

    def test_dotnet_without_minimum_and_fail_skips_is_rejected(self):
        wrapper = self.fixture.root / "scripts" / "dotnet-pinned.sh"
        wrapper.parent.mkdir()
        wrapper.write_text("#!/bin/bash\nexit 0\n", encoding="utf-8")
        project = self.module_root / "tests" / "Alpha.Tests.csproj"
        project.write_text("<Project />\n", encoding="utf-8")
        command = (
            "scripts/dotnet-pinned.sh restore Modules/alpha/tests/Alpha.Tests.csproj --locked-mode "
            "&& scripts/dotnet-pinned.sh test Modules/alpha/tests/Alpha.Tests.csproj "
            "--configuration Release --no-restore"
        )
        with self.assertRaisesRegex(Phase0Error, "runner arguments"):
            self.parse(command)

    def test_missing_required_static_suite_generates_failure(self):
        manifest = module_manifest("alpha")
        manifest["tests"] = {"suites": []}
        (self.module_root / "module.yaml").write_text(
            json.dumps(manifest), encoding="utf-8"
        )
        checks = run_required_module_static_tests(self.fixture.root)
        self.assertEqual(1, len(checks))
        self.assertEqual("FAIL", checks[0]["status"])
        self.assertIn("no required", checks[0]["log"])

    def test_required_suite_missing_command_generates_failure(self):
        suite = self.suite("ignored")
        suite["command"] = None
        manifest = module_manifest("alpha")
        manifest["tests"] = {"suites": [suite]}
        (self.module_root / "module.yaml").write_text(
            json.dumps(manifest), encoding="utf-8"
        )
        checks = run_required_module_static_tests(self.fixture.root)
        self.assertEqual(1, len(checks))
        self.assertEqual("FAIL", checks[0]["status"])
        self.assertIn("command is missing", checks[0]["log"])

    def test_duplicate_required_suite_id_generates_failure(self):
        suite = self.suite(
            "python3 -m unittest discover -s Modules/alpha/tests -p 'test_*.py'"
        )
        manifest = module_manifest("alpha")
        manifest["tests"] = {"suites": [suite, dict(suite)]}
        (self.module_root / "module.yaml").write_text(
            json.dumps(manifest), encoding="utf-8"
        )
        checks = run_required_module_static_tests(self.fixture.root)
        self.assertEqual(1, len(checks))
        self.assertEqual("FAIL", checks[0]["status"])
        self.assertIn("duplicate suite ids", checks[0]["log"])

    def test_solution_omitting_project_fails_before_build(self):
        project = self.module_root / "src" / "Alpha.csproj"
        project.write_text("<Project />\n", encoding="utf-8")
        (self.fixture.root / "Dps.slnx").write_text(
            "<Solution />\n", encoding="utf-8"
        )
        wrapper = self.fixture.root / "scripts" / "dotnet-pinned.sh"
        wrapper.parent.mkdir()
        wrapper.write_text("#!/bin/bash\nexit 0\n", encoding="utf-8")
        check = run_locked_solution_build(self.fixture.root)
        self.assertEqual("FAIL", check["status"])
        self.assertIn("omitted from solution", check["log"])


class InstructionReceiptTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()
        self.module_root = self.fixture.add_module("alpha")
        self.baseline = self.fixture.commit()

    def tearDown(self):
        self.fixture.close()

    def test_receipt_binds_root_and_impacted_module_in_order(self):
        (self.module_root / "src.py").write_text("VALUE = 2\n", encoding="utf-8")
        receipt = resolve_instruction_receipt(self.fixture.root, self.baseline)
        self.assertEqual(["alpha"], receipt["scope"])
        self.assertEqual(
            ["AGENTS.md", "Modules/alpha/AGENTS.md"],
            [entry["path"] for entry in receipt["instructions"]],
        )
        valid, _, _ = validate_instruction_receipt(self.fixture.root, receipt)
        self.assertTrue(valid)

    def test_changed_instruction_invalidates_receipt(self):
        receipt = resolve_instruction_receipt(self.fixture.root, self.baseline)
        (self.fixture.root / "AGENTS.md").write_text(
            "# Root policy changed\n", encoding="utf-8"
        )
        valid, message, _ = validate_instruction_receipt(self.fixture.root, receipt)
        self.assertFalse(valid)
        self.assertIn("stale", message)

    def test_expanded_diff_invalidates_receipt(self):
        (self.module_root / "first.py").write_text("FIRST = 1\n", encoding="utf-8")
        receipt = resolve_instruction_receipt(self.fixture.root, self.baseline)
        (self.module_root / "second.py").write_text("SECOND = 2\n", encoding="utf-8")
        valid, message, _ = validate_instruction_receipt(self.fixture.root, receipt)
        self.assertFalse(valid)
        self.assertIn("stale", message)

    def test_receipt_conforms_to_governance_schema(self):
        schema_directory = self.fixture.root / "governance" / "schemas"
        schema_directory.mkdir(parents=True)
        schema_directory.joinpath("phase0-instruction-receipt.schema.json").write_text(
            ROOT.joinpath(
                "governance", "schemas", "phase0-instruction-receipt.schema.json"
            ).read_text(encoding="utf-8"),
            encoding="utf-8",
        )
        receipt = resolve_instruction_receipt(self.fixture.root, self.baseline)
        self.assertEqual("BOUND", receipt["status"])
        self.assertTrue(receipt["receipt_id"].startswith("instruction:"))


class GateTruthTests(unittest.TestCase):
    def test_default_phase0_evidence_uses_a_unique_run_directory(self):
        first_run_id = _new_publication_run_id()
        second_run_id = _new_publication_run_id()
        self.assertRegex(first_run_id, r"^[0-9a-f]{32}$")
        self.assertRegex(second_run_id, r"^[0-9a-f]{32}$")
        self.assertNotEqual(first_run_id, second_run_id)
        first = _default_phase0_evidence_path(first_run_id)
        second = _default_phase0_evidence_path(second_run_id)
        self.assertEqual(
            Path("Reports/ci/phase0-runs")
            / first_run_id
            / "phase0-evidence.json",
            first,
        )
        self.assertNotEqual(first, second)

    def test_phase0_output_is_restricted_to_lowercase_ignored_reports_ci(self):
        accepted = _safe_phase0_output_path(
            ROOT, Path("Reports/ci/phase0-safe.json"), "Phase0 evidence"
        )
        self.assertEqual(
            ROOT / "Reports" / "ci" / "phase0-safe.json", accepted
        )
        for unsafe in (
            Path("README.md"),
            Path("Reports/ci/Phase0.json"),
            Path("Reports/ci/not-json.txt"),
            Path("Reports/ci/phase0.json.publication.json"),
        ):
            with self.subTest(path=str(unsafe)), self.assertRaises(Phase0Error):
                _safe_phase0_output_path(ROOT, unsafe, "Phase0 evidence")

    def test_phase0_json_requires_explicit_committed_integrity_marker(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            evidence_path = Path(temporary) / "phase0.json"
            write_evidence(evidence_path, {"overall_status": "PASS"})
            value, digest = _load_committed_json_object_with_sha(
                evidence_path, "Phase0 evidence"
            )
            self.assertEqual({"overall_status": "PASS"}, value)
            self.assertEqual(
                hashlib.sha256(evidence_path.read_bytes()).hexdigest(),
                digest,
            )
            marker_path = _publication_marker_path(evidence_path)
            marker = json.loads(marker_path.read_text(encoding="utf-8"))
            self.assertEqual("COMMITTED", marker["status"])

            marker["payload_sha256"] = "0" * 64
            marker_path.write_text(
                json.dumps(marker, sort_keys=True) + "\n", encoding="utf-8"
            )
            with self.assertRaisesRegex(Phase0Error, "COMMITTED binding"):
                _load_committed_json_object_with_sha(
                    evidence_path, "Phase0 evidence"
                )

    def test_uncommitted_phase0_stage_is_not_readable(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            evidence_path = Path(temporary) / "phase0.json"
            publication = EvidencePublication(evidence_path)
            with publication:
                publication.stage({"overall_status": "PASS"})
                with self.assertRaisesRegex(Phase0Error, "manual recovery"):
                    _load_committed_json_object_with_sha(
                        evidence_path, "Phase0 evidence"
                    )

    def test_complete_directory_transport_preserves_quarantine_claim(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            source_directory = Path(temporary) / "run"
            source_directory.mkdir()
            evidence_path = source_directory / "phase0-evidence.json"
            publication = EvidencePublication(evidence_path)
            with publication:
                publication.stage({"overall_status": "PASS"})
                original_fsync = os.fsync
                calls = {"count": 0}

                def fail_committed_marker_directory_sync(descriptor):
                    calls["count"] += 1
                    if calls["count"] == 2:
                        raise OSError("committed marker directory sync failed")
                    return original_fsync(descriptor)

                with mock.patch(
                    "run_phase0_gate.os.fsync",
                    side_effect=fail_committed_marker_directory_sync,
                ):
                    with self.assertRaisesRegex(
                        OSError, "committed marker directory sync failed"
                    ):
                        publication.commit()

            self.assertTrue(publication.claim_path.exists())
            with tempfile.TemporaryDirectory(dir=reports) as artifact_root:
                extracted_directory = Path(artifact_root) / "phase0-evidence"
                shutil.copytree(source_directory, extracted_directory)
                extracted_evidence = extracted_directory / evidence_path.name
                self.assertTrue(
                    extracted_directory.joinpath(publication.claim_path.name).exists()
                )
                with self.assertRaisesRegex(Phase0Error, "manual recovery"):
                    _load_committed_json_object_with_sha(
                        extracted_evidence, "transported Phase0 evidence"
                    )

    def test_claim_unlink_directory_fsync_failure_restores_quarantine(self):
        reports = ROOT / "Reports" / "ci"
        reports.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=reports) as temporary:
            evidence_path = Path(temporary) / "phase0-evidence.json"
            publication = EvidencePublication(evidence_path)
            with publication:
                publication.stage({"overall_status": "PASS"})
                original_fsync = os.fsync
                calls = {"count": 0}

                def fail_claim_unlink_directory_sync(descriptor):
                    calls["count"] += 1
                    if calls["count"] == 3:
                        raise OSError("claim unlink directory sync failed")
                    return original_fsync(descriptor)

                with mock.patch(
                    "run_phase0_gate.os.fsync",
                    side_effect=fail_claim_unlink_directory_sync,
                ):
                    with self.assertRaisesRegex(
                        OSError, "claim unlink directory sync failed"
                    ):
                        publication.commit()

            self.assertGreaterEqual(calls["count"], 5)
            self.assertTrue(publication.claim_path.exists())
            with self.assertRaisesRegex(Phase0Error, "manual recovery"):
                _load_committed_json_object_with_sha(
                    evidence_path, "Phase0 evidence"
                )

    def test_clean_checkout_is_formal_evidence_eligible(self):
        fixture = RepositoryFixture()
        try:
            fixture.add_module("alpha")
            fixture.commit()
            check = workspace_cleanliness_check(fixture.root, diagnostic=False)
            self.assertEqual("PASS", check["status"])
            self.assertTrue(check["details"]["clean"])
            self.assertTrue(check["details"]["formal_evidence_eligible"])
            self.assertEqual(
                ("REPOSITORY_STATIC_VERIFIED", "REPOSITORY_STATIC_VERIFIED"),
                evidence_classification("PASS", True, False),
            )
        finally:
            fixture.close()

    def test_modified_tracked_test_file_blocks_formal_evidence(self):
        fixture = RepositoryFixture()
        try:
            module = fixture.add_module("alpha")
            fixture.commit()
            (module / "tests" / "test_smoke.py").write_text(
                "raise SystemExit('mutated')\n", encoding="utf-8"
            )
            check = workspace_cleanliness_check(fixture.root, diagnostic=False)
            self.assertEqual("FAIL", check["status"])
            self.assertFalse(check["details"]["formal_evidence_eligible"])
            self.assertEqual(
                ("REPOSITORY_STATIC_VERIFIED", None),
                evidence_classification("FAIL", False, False),
            )
        finally:
            fixture.close()

    def test_untracked_production_file_blocks_formal_evidence(self):
        fixture = RepositoryFixture()
        try:
            module = fixture.add_module("alpha")
            fixture.commit()
            (module / "src" / "untracked.py").write_text(
                "VALUE = 2\n", encoding="utf-8"
            )
            check = workspace_cleanliness_check(fixture.root, diagnostic=False)
            self.assertEqual("FAIL", check["status"])
            self.assertGreater(check["details"]["dirty_entry_count"], 0)
            self.assertFalse(check["details"]["formal_evidence_eligible"])
        finally:
            fixture.close()

    def test_dirty_diagnostic_workspace_never_issues_formal_level(self):
        fixture = RepositoryFixture()
        try:
            module = fixture.add_module("alpha")
            fixture.commit()
            (module / "src" / "implementation.py").write_text(
                "VALUE = 2\n", encoding="utf-8"
            )
            check = workspace_cleanliness_check(fixture.root, diagnostic=True)
            self.assertEqual("PASS", check["status"])
            self.assertFalse(check["details"]["formal_evidence_eligible"])
            self.assertEqual(
                ("WORKSPACE_DIAGNOSTIC_ONLY", None),
                evidence_classification("PASS", False, True),
            )
        finally:
            fixture.close()

    def test_node_patch_version_mismatch_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="dps-node-version-") as temporary:
            root = Path(temporary)
            executable = root / "node"
            executable.write_text(
                "#!/bin/sh\nprintf 'v24.19.0\\n'\n", encoding="utf-8"
            )
            executable.chmod(0o755)
            version, error = node_version(str(executable), root)
        self.assertIsNone(version)
        self.assertIn("Node 24.18.0 is required", str(error))

    def test_trusted_environment_ignores_path_and_dps_node_injection(self):
        with tempfile.TemporaryDirectory(prefix="dps-trusted-node-") as temporary:
            root = Path(temporary)
            trusted_directory = root / "trusted"
            poisoned_directory = root / "poisoned"
            trusted_directory.mkdir()
            poisoned_directory.mkdir()
            trusted_node = trusted_directory / "node"
            poisoned_node = poisoned_directory / "node"
            trusted_node.write_text(
                "#!/bin/sh\nprintf 'v24.18.0\\n'\n", encoding="utf-8"
            )
            poisoned_node.write_text(
                "#!/bin/sh\nprintf 'v99.0.0\\n'\n", encoding="utf-8"
            )
            trusted_node.chmod(0o755)
            poisoned_node.chmod(0o755)
            _trusted_node_executable.cache_clear()
            try:
                with mock.patch(
                    "run_phase0_gate._trusted_node_candidates",
                    return_value=(trusted_node,),
                ), mock.patch.dict(
                    "os.environ",
                    {
                        "PATH": str(poisoned_directory),
                        "DPS_NODE": str(poisoned_node),
                    },
                    clear=False,
                ):
                    with _trusted_test_environment_scope({}) as environment:
                        self.assertEqual(
                            str(trusted_node.resolve()), environment["DPS_NODE"]
                        )
                        self.assertEqual(
                            str(trusted_directory.resolve()),
                            environment["PATH"].split(os.pathsep)[0],
                        )
                        self.assertNotIn(str(poisoned_directory), environment["PATH"])
                        self.assertNotIn(str(poisoned_node), environment.values())
                        completed = subprocess.run(
                            ["node", "--version"],
                            cwd=str(ROOT),
                            env=environment,
                            text=True,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT,
                            check=True,
                        )
                        self.assertEqual("v24.18.0", completed.stdout.strip())
            finally:
                _trusted_node_executable.cache_clear()

    def test_unlocked_fixed_node_candidate_fails_closed(self):
        with tempfile.TemporaryDirectory(prefix="dps-unlocked-node-") as temporary:
            executable = Path(temporary) / "node"
            executable.write_text(
                "#!/bin/sh\nprintf 'v24.14.0\\n'\n", encoding="utf-8"
            )
            executable.chmod(0o755)
            _trusted_node_executable.cache_clear()
            try:
                with mock.patch(
                    "run_phase0_gate._trusted_node_candidates",
                    return_value=(executable,),
                ):
                    with self.assertRaisesRegex(
                        Phase0Error, "trusted Node 24.18.0 executable is unavailable"
                    ):
                        with _trusted_test_environment_scope({}):
                            pass
            finally:
                _trusted_node_executable.cache_clear()

    def test_trusted_environment_rejects_path_override(self):
        with self.assertRaisesRegex(
            Phase0Error, "unknown trusted test environment override: PATH"
        ):
            with _trusted_test_environment_scope({"PATH": "/tmp/poisoned"}):
                pass

    def test_trusted_environment_forwards_candidate_postgres_keys(self):
        forwarded = {
            "DPS_TEST_POSTGRES": (
                "Host=/tmp/dps-postgres;Port=55432;Database=dps_test;"
                "Username=young;Pooling=false"
            ),
            "DPS_TEST_POSTGRES_URI": (
                "host=/tmp/dps-postgres port=55432 dbname=dps_test user=young"
            ),
            "DPS_PSQL": "/trusted/postgresql/18/bin/psql",
        }
        with _trusted_test_environment_scope(forwarded) as environment:
            self.assertEqual(
                forwarded, {key: environment[key] for key in forwarded}
            )

    def test_trusted_dotnet_ignores_ambient_path_and_home(self):
        with tempfile.TemporaryDirectory(prefix="dps-poisoned-dotnet-") as temporary:
            root = Path(temporary)
            fake = root / "dotnet"
            fake.write_text("#!/bin/sh\nprintf '10.0.301\\n'\n", encoding="utf-8")
            fake.chmod(0o755)
            _trusted_dotnet_executable.cache_clear()
            try:
                with mock.patch.dict(
                    "os.environ",
                    {"PATH": str(root), "HOME": str(root), "DPS_DOTNET": str(fake)},
                    clear=False,
                ):
                    resolved = _trusted_dotnet_executable()
                self.assertNotEqual(fake.resolve(), resolved)
            finally:
                _trusted_dotnet_executable.cache_clear()

    def test_trusted_environment_uses_private_non_ambient_state(self):
        with mock.patch.dict(
            "os.environ",
            {
                "HOME": "/tmp/poisoned-home",
                "TMPDIR": "/tmp/poisoned-tmp",
                "LANG": "poisoned",
                "LC_ALL": "poisoned",
                "CODEX_SANDBOX": "seatbelt",
                "CODEX_SANDBOX_NETWORK_DISABLED": "1",
            },
            clear=False,
        ):
            with _trusted_test_environment_scope({}) as first:
                first_home = Path(first["HOME"])
                first_tmp = Path(first["TMPDIR"])
                self.assertTrue(first_home.is_dir())
                self.assertTrue(first_tmp.is_dir())
                self.assertEqual("C", first["LANG"])
                self.assertEqual("C", first["LC_ALL"])
                self.assertNotIn("poisoned", " ".join(first.values()))
                self.assertEqual("1", first["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"])
                self.assertEqual("1", first["MSBUILDDISABLENODEREUSE"])
                self.assertEqual("seatbelt", first["CODEX_SANDBOX"])
                self.assertEqual("1", first["CODEX_SANDBOX_NETWORK_DISABLED"])
            self.assertFalse(first_home.exists())
            with _trusted_test_environment_scope({}) as second:
                self.assertNotEqual(first_home, Path(second["HOME"]))

    def test_manifest_cannot_disable_the_executor_network_sandbox(self):
        with self.assertRaisesRegex(
            Phase0Error,
            "unknown trusted test environment override: CODEX_SANDBOX_NETWORK_DISABLED",
        ):
            with _trusted_test_environment_scope(
                {"CODEX_SANDBOX_NETWORK_DISABLED": "0"}
            ):
                pass

    def test_world_accessible_trusted_state_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="dps-unsafe-state-") as temporary:
            root = Path(temporary)
            root.chmod(0o755)
            with self.assertRaisesRegex(Phase0Error, "permissions are unsafe"):
                _trusted_test_environment({}, root)

    def test_restore_platform_failure_is_infrastructure_error(self):
        self.assertTrue(
            _restore_failure_is_infrastructure(
                1,
                "CSSM_ModuleLoad(): One or more parameters passed to a function were invalid",
            )
        )
        self.assertFalse(
            _restore_failure_is_infrastructure(
                1,
                "error NU1004: The package references have changed for lock file",
            )
        )

    def test_command_timeout_kills_the_posix_process_group(self):
        process = mock.Mock()
        process.pid = 4242
        process.communicate.side_effect = [
            subprocess.TimeoutExpired(["fixture"], 1, output="partial"),
            ("partial", None),
        ]
        with mock.patch("phase0.subprocess.Popen", return_value=process) as popen, mock.patch(
            "phase0.os.killpg"
        ) as killpg, mock.patch("phase0.os.name", "posix"):
            result = run_command(["fixture"], ROOT, timeout_seconds=1)
        self.assertEqual(124, result.exit_code)
        popen.assert_called_once()
        self.assertTrue(popen.call_args.kwargs["start_new_session"])
        killpg.assert_called_once_with(4242, signal.SIGKILL)

    def test_global_required_unittest_skip_is_not_pass(self):
        result = CommandResult(
            command=[sys.executable, "-m", "unittest"],
            exit_code=0,
            duration_ms=1,
            output="s\nRan 1 test in 0.001s\n\nOK (skipped=1)\n",
        )
        check = check_from_command("global-required-tests", True, result)
        enforced = enforce_unittest_evidence(
            check, result, 1, 1, (), "global required tests"
        )
        self.assertEqual("FAIL", enforced["status"])
        self.assertEqual(1, enforced["exit_code"])
        self.assertIn("skipped", enforced["log"].casefold())

    def test_global_required_unittest_expected_failure_is_not_pass(self):
        result = CommandResult(
            command=[sys.executable, "-m", "unittest"],
            exit_code=0,
            duration_ms=1,
            output="x\nRan 1 test in 0.001s\n\nOK (expected failures=1)\n",
        )
        check = check_from_command("global-required-tests", True, result)
        enforced = enforce_unittest_evidence(
            check, result, 1, 1, (), "global required tests"
        )
        self.assertEqual("FAIL", enforced["status"])
        self.assertIn("expected failures", enforced["log"].casefold())

    def test_required_only_pass_can_release_gate(self):
        pass_check = new_check("required", True, "PASS", None, 0, 1, "ok")
        optional_skip = new_check(
            "optional", False, "NOT_APPLICABLE", None, None, 0, "not applicable"
        )
        overall, _ = evaluate_checks([pass_check, optional_skip])
        self.assertEqual("PASS", overall)

        for status in (
            "FAIL",
            "SKIP",
            "PARTIAL",
            "NOT_RUN",
            "INFRA_ERROR",
            "NOT_APPLICABLE",
        ):
            with self.subTest(status=status):
                check = new_check("required", True, status, None, 1, 1, status)
                overall, _ = evaluate_checks([check])
                self.assertEqual("FAIL", overall)

    def test_zero_required_checks_fails(self):
        overall, summary = evaluate_checks([])
        self.assertEqual("FAIL", overall)
        self.assertEqual(0, summary["required"])

    def test_schema_required_property_is_enforced(self):
        schema = {
            "type": "object",
            "required": ["kind"],
            "properties": {"kind": {"const": "Module"}},
            "additionalProperties": False,
        }
        self.assertTrue(validate_json_schema({}, schema))
        self.assertFalse(validate_json_schema({"kind": "Module"}, schema))

    def test_phase0_evidence_can_record_required_failure_truthfully(self):
        schema = json.loads(
            ROOT.joinpath(
                "governance", "schemas", "phase0-test-evidence.schema.json"
            ).read_text(encoding="utf-8")
        )
        record = {
            "schema_version": "dps.phase0-test-evidence/v1",
            "evidence_id": "phase0:evidence-0001",
            "test_id": "module-governance",
            "module_id": "evidence-service",
            "test_type": "static",
            "required": True,
            "status": "FAIL",
            "verification_level": "REPOSITORY_STATIC_VERIFIED",
            "baseline_commit": "a" * 40,
            "instruction_receipt_id": "instruction:receipt-0001",
            "runner_identity": "dps-phase0-gate",
            "command": "internal:module-governance",
            "started_at": "2026-07-14T00:00:00Z",
            "finished_at": "2026-07-14T00:00:01Z",
            "exit_code": 1,
            "environment": {},
            "artifacts": [
                {
                    "path": "embedded:log",
                    "sha256": "b" * 64,
                    "media_type": "text/plain",
                }
            ],
            "reason": "required check failed",
        }
        self.assertFalse(validate_json_schema(record, schema))
        record["status"] = "PASS"
        self.assertTrue(validate_json_schema(record, schema))


class CiMutationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="dps-ci-policy-")
        self.root = Path(self.temporary.name)
        workflow = self.root / ".github" / "workflows"
        runner = self.root / "Tools" / "ci"
        scripts = self.root / "scripts"
        workflow.mkdir(parents=True)
        runner.mkdir(parents=True)
        scripts.mkdir(parents=True)
        (runner / "run_phase0_gate.py").write_text(
            """import sys

def main():
    run_phase0_unittests()
    run_external_gate_unittests()
    run_locked_solution_build()
    run_required_module_static_tests()
    validate_governance()
    validate_ci_integrity()
    load_or_issue_receipt()
    resolve_instruction_receipt()
    build_test_evidence_records()
    write_evidence()
    overall_status, _ = evaluate_checks()
    return 0 if overall_status == "PASS" else 1

if __name__ == "__main__":
    sys.exit(main())
""",
            encoding="utf-8",
        )
        self.workflow_path = workflow / "static-ci.yml"
        self.workflow_path.write_text(self.valid_workflow(), encoding="utf-8")
        self.release_path = scripts / "release.sh"
        self.release_path.write_text(
            """#!/usr/bin/env bash
set -euo pipefail
cat <<'EOF'
Help may say git commit, git tag, and git push are never executed.
EOF
if [[ -n "$(git status --porcelain=v1 --untracked-files=all)" ]]; then
  exit 1
fi
repo_root="/fixture"
python_executable="$repo_root/.venv/bin/python"
phase0_evidence=""
if [[ "${1:-}" == "--phase0-evidence" ]]; then
  phase0_evidence="${2:-}"
fi
bom_path="candidate.json"
bundle_root="bundle"
previous_bom_path="previous.json"
schema_sha256="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
head_commit="$(git rev-parse HEAD^{commit})"
bom_commit="$($python_executable - "$bom_path" <<'PY'
"""
            + RELEASE_BOM_COMMIT_READER
            + """PY
)"
if [[ "$bom_commit" != "$head_commit" ]]; then
  exit 1
fi
phase0_arguments=(--base "$head_commit")
if [[ -n "$phase0_evidence" ]]; then
  phase0_arguments+=(--evidence "$phase0_evidence")
fi
"$python_executable" Tools/ci/run_phase0_gate.py "${phase0_arguments[@]}"
"$python_executable" Tools/ci/candidate_bom_validator.py --repo-root "$repo_root" --bundle-root "$bundle_root" --bom "$bom_path" --previous-bom "$previous_bom_path" --native-stop-trust-receipt "$native_stop_trust_receipt_path" --schema-sha256 "$schema_sha256"
""",
            encoding="utf-8",
        )

    def tearDown(self):
        self.temporary.cleanup()

    @staticmethod
    def valid_workflow():
        return """name: Static CI
on: [push, pull_request]
jobs:
  validate:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@08c6903cd8c0fde910a37f88322edcfb5dd907a8 # v5.0.0
        with:
          fetch-depth: 0
      - uses: actions/setup-python@e797f83bcb11b83ae66e0230d6156d7c80228e7c # v6.0.0
        with:
          python-version: "3.12.13"
      - uses: actions/setup-node@2028fbc5c25fe9cf00d9f06a71cc4710d4507903 # v6.0.0
        with:
          node-version: "24.18.0"
      - uses: actions/setup-dotnet@d4c94342e560b34958eacfc5d055d21461ed1c5d # v5.0.0
        with:
          dotnet-version: "10.0.301"
      - run: python -m pip install --require-hashes --requirement requirements-ci.txt
      - run: python Tools/ci/run_phase0_gate.py --evidence Reports/ci/phase0-evidence/phase0-evidence.json
      - uses: actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4.6.2
        with:
          path: |
            Reports/ci/phase0-evidence/
          if-no-files-found: error
"""

    def test_valid_ci_policy_passes(self):
        result = validate_ci_integrity(self.root)
        self.assertIn("workflow_sha256", result)

    def test_repository_workflow_uploads_complete_evidence_directory(self):
        workflow = (ROOT / ".github/workflows/static-ci.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn(
            "Reports/ci/phase0-evidence/phase0-evidence.json", workflow
        )
        self.assertIn(
            "            Reports/ci/phase0-evidence/\n", workflow
        )

    def test_workflow_missing_complete_evidence_directory_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "            Reports/ci/phase0-evidence/\n",
                "",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "complete canonical"):
            validate_ci_integrity(self.root)

    def test_workflow_payload_and_marker_without_claim_namespace_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "            Reports/ci/phase0-evidence/\n",
                "            Reports/ci/phase0-evidence/phase0-evidence.json\n"
                "            Reports/ci/phase0-evidence/phase0-evidence.json.publication.json\n",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "quarantine claim"):
            validate_ci_integrity(self.root)

    def test_workflow_directory_upload_excluding_claim_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "            Reports/ci/phase0-evidence/\n",
                "            Reports/ci/phase0-evidence/\n"
                "            !Reports/ci/phase0-evidence/*.publication.lock\n",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "without exclusions"):
            validate_ci_integrity(self.root)

    def test_workflow_directory_upload_must_fail_when_missing(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "          if-no-files-found: error\n",
                "          if-no-files-found: ignore\n",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "fail on missing files"):
            validate_ci_integrity(self.root)

    def test_fixed_success_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "--evidence Reports/ci/phase0-evidence/phase0-evidence.json",
                "--evidence Reports/ci/phase0-evidence/phase0-evidence.json || true",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "convert command failure to success"):
            validate_ci_integrity(self.root)

    def test_direct_validator_bypass_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow()
            + "      - run: python Tools/ci/validate_repo.py\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "bypass"):
            validate_ci_integrity(self.root)

    def test_workflow_echo_gate_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "- run: python Tools/ci/run_phase0_gate.py",
                "- run: echo python Tools/ci/run_phase0_gate.py",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "actually invoke"):
            validate_ci_integrity(self.root)

    def test_workflow_pip_without_require_hashes_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(" --require-hashes", ""),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "must use --require-hashes"):
            validate_ci_integrity(self.root)

    def test_workflow_diagnostic_mode_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "--evidence Reports/ci/phase0-evidence/phase0-evidence.json",
                "--diagnostic-workspace --evidence Reports/ci/phase0-evidence/phase0-evidence.json",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "may not use.*diagnostic"):
            validate_ci_integrity(self.root)

    def test_workflow_always_false_gate_step_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "      - run: python Tools/ci/run_phase0_gate.py",
                "      - if: false\n"
                "        run: python Tools/ci/run_phase0_gate.py",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "always-false step"):
            validate_ci_integrity(self.root)

    def test_runner_comment_only_markers_are_rejected(self):
        runner = self.root / "Tools" / "ci" / "run_phase0_gate.py"
        runner.write_text(
            """import sys
# run_phase0_unittests run_external_gate_unittests run_locked_solution_build
# run_required_module_static_tests validate_governance validate_ci_integrity
# load_or_issue_receipt resolve_instruction_receipt evaluate_checks
# build_test_evidence_records write_evidence
def main():
    overall_status = "PASS"
    return 0 if overall_status == "PASS" else 1
if __name__ == "__main__":
    sys.exit(main())
""",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "cannot reach required operations"):
            validate_ci_integrity(self.root)

    def test_runner_fixed_success_is_rejected(self):
        runner = self.root / "Tools" / "ci" / "run_phase0_gate.py"
        runner.write_text(
            runner.read_text(encoding="utf-8").replace(
                'return 0 if overall_status == "PASS" else 1', "return 0"
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "return nonzero"):
            validate_ci_integrity(self.root)

    def test_runner_if_false_operations_are_not_reachable(self):
        runner = self.root / "Tools" / "ci" / "run_phase0_gate.py"
        calls = [
            "run_phase0_unittests()",
            "run_external_gate_unittests()",
            "run_locked_solution_build()",
            "run_required_module_static_tests()",
            "validate_governance()",
            "validate_ci_integrity()",
            "load_or_issue_receipt()",
            "resolve_instruction_receipt()",
            "build_test_evidence_records()",
            "write_evidence()",
        ]
        runner.write_text(
            "import sys\n"
            "def main():\n"
            "    if False:\n"
            + "".join("        " + call + "\n" for call in calls)
            + "    overall_status, _ = evaluate_checks()\n"
            '    return 0 if overall_status == "PASS" else 1\n'
            'if __name__ == "__main__":\n'
            "    sys.exit(main())\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "cannot reach required operations"):
            validate_ci_integrity(self.root)

    def test_runner_calls_after_return_are_not_reachable(self):
        runner = self.root / "Tools" / "ci" / "run_phase0_gate.py"
        runner.write_text(
            runner.read_text(encoding="utf-8").replace(
                "def main():\n", "def main():\n    return 0\n", 1
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "cannot reach required operations"):
            validate_ci_integrity(self.root)

    def test_missing_dotnet_pin_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace('dotnet-version: "10.0.301"', 'dotnet-version: "10.0.300"'),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "pin .NET SDK 10.0.301"):
            validate_ci_integrity(self.root)

    def test_mutable_action_tag_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace(
                "actions/checkout@08c6903cd8c0fde910a37f88322edcfb5dd907a8 # v5.0.0",
                "actions/checkout@v5",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "must pin official"):
            validate_ci_integrity(self.root)

    def test_latest_runner_image_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow().replace("runs-on: ubuntu-24.04", "runs-on: ubuntu-latest"),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "pin runs-on to ubuntu-24.04"):
            validate_ci_integrity(self.root)

    def test_unapproved_action_is_rejected(self):
        self.workflow_path.write_text(
            self.valid_workflow()
            + "      - uses: example/unsafe@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "outside the pinned allowlist"):
            validate_ci_integrity(self.root)

    def test_release_help_words_do_not_trigger_mutation_detection(self):
        result = validate_ci_integrity(self.root)
        self.assertIn("release_script_sha256", result)

    def test_repository_release_uses_safe_runner_default_and_array_override(self):
        release = (ROOT / "scripts" / "release.sh").read_text(encoding="utf-8")
        self.assertIn('phase0_evidence=""', release)
        self.assertIn('phase0_arguments=(--base "$head_commit")', release)
        self.assertIn(
            'phase0_arguments+=(--evidence "$phase0_evidence")', release
        )
        self.assertIn(
            'Tools/ci/run_phase0_gate.py "${phase0_arguments[@]}"', release
        )

    def test_release_fixed_default_evidence_path_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8").replace(
                'phase0_evidence=""',
                'phase0_evidence="Reports/ci/reused.json"',
                1,
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(
            Phase0Error, "safe Phase 0|arbitrary Phase 0 evidence path"
        ):
            validate_ci_integrity(self.root)

    def test_release_string_concatenated_evidence_override_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8").replace(
                'phase0_arguments+=(--evidence "$phase0_evidence")',
                'phase0_arguments+=(--evidence "Reports/ci/$phase0_evidence")',
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(
            Phase0Error, "safe Phase 0|arbitrary Phase 0 evidence path"
        ):
            validate_ci_integrity(self.root)

    def test_release_git_commit_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8") + "git commit -am unsafe\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "may not execute git commit"):
            validate_ci_integrity(self.root)

    def test_release_git_tag_after_separator_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8") + "true && git tag v1\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "may not execute git tag"):
            validate_ci_integrity(self.root)

    def test_release_git_push_in_command_substitution_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8") + "value=$(git push origin main)\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "may not execute git push"):
            validate_ci_integrity(self.root)

    def test_release_missing_candidate_bom_validator_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8").replace(
                '"$python_executable" '
                "Tools/ci/candidate_bom_validator.py "
                '--repo-root "$repo_root" --bundle-root "$bundle_root" '
                '--bom "$bom_path" --previous-bom "$previous_bom_path" '
                '--native-stop-trust-receipt "$native_stop_trust_receipt_path" '
                '--schema-sha256 "$schema_sha256"\n',
                "",
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "candidate_bom_validator"):
            validate_ci_integrity(self.root)

    def test_release_comment_only_invocations_are_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8")
            .replace(
                '"$python_executable" Tools/ci/run_phase0_gate.py '
                '"${phase0_arguments[@]}"\n',
                '# "$python_executable" Tools/ci/run_phase0_gate.py '
                '"${phase0_arguments[@]}"\n',
            )
            .replace(
                '"$python_executable" '
                "Tools/ci/candidate_bom_validator.py "
                '--repo-root "$repo_root" --bundle-root "$bundle_root" '
                '--bom "$bom_path" --previous-bom "$previous_bom_path" '
                '--native-stop-trust-receipt "$native_stop_trust_receipt_path" '
                '--schema-sha256 "$schema_sha256"\n',
                '# "$python_executable" '
                "Tools/ci/candidate_bom_validator.py "
                '--repo-root "$repo_root" --bundle-root "$bundle_root" '
                '--bom "$bom_path" --previous-bom "$previous_bom_path" '
                '--native-stop-trust-receipt "$native_stop_trust_receipt_path" '
                '--schema-sha256 "$schema_sha256"\n',
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "must actually invoke"):
            validate_ci_integrity(self.root)

    def test_release_network_command_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8")
            + "curl -X POST https://example.invalid/deploy\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "validation-only allowlist"):
            validate_ci_integrity(self.root)

    def test_release_deployment_command_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8") + "kubectl apply -f release.yml\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "validation-only allowlist"):
            validate_ci_integrity(self.root)

    def test_release_file_deletion_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8") + "rm -rf bundle\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "validation-only allowlist"):
            validate_ci_integrity(self.root)

    def test_release_arbitrary_python_is_rejected(self):
        self.release_path.write_text(
            self.release_path.read_text(encoding="utf-8")
            + '"$python_executable" Tools/unsafe.py\n',
            encoding="utf-8",
        )
        with self.assertRaisesRegex(Phase0Error, "validation-only allowlist"):
            validate_ci_integrity(self.root)


if __name__ == "__main__":
    unittest.main()
