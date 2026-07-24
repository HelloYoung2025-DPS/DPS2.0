#!/usr/bin/env python3
"""M1C module-impact, merge-head, conflict, and rollback verification."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / "Tools" / "ci"
if str(CI) not in sys.path:
    sys.path.insert(0, str(CI))

from phase0 import (  # noqa: E402
    Phase0Error,
    build_dependency_graph_snapshot,
    load_module_records_without_schema,
    resolve_instruction_receipt,
    validate_instruction_receipt,
)


def git(root: Path, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *arguments],
        cwd=root,
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )


def contract_schema(title: str) -> dict:
    properties = {
        "schema_version": {"const": "1.0.0"},
        "contract_id": {"const": "sample.event/v1"},
        "producer_module": {"const": "provider"},
        "soul_id": {"type": ["string", "null"]},
        "device_binding_id": {"type": ["string", "null"]},
        "platform_account_id": {"type": ["string", "null"]},
        "trace_id": {"type": "string"},
        "idempotency_key": {"type": "string"},
        "occurred_at": {"type": "string"},
        "privacy_class": {"type": "string"},
    }
    return {
        "title": title,
        "type": "object",
        "additionalProperties": False,
        "required": sorted(properties),
        "properties": properties,
    }


def contract_item(owner: str) -> dict:
    return {
        "contractId": "sample.event",
        "major": 1,
        "source": "Modules/provider/contracts/provided/sample.event.v1.schema.json",
        "status": "proposed",
        "mode": "active",
        "ownerModule": owner,
    }


def manifest(module_id: str) -> dict:
    provided = [contract_item("provider")] if module_id == "provider" else []
    consumed = [contract_item("provider")] if module_id == "consumer" else []
    dependencies = ["provider"] if module_id == "consumer" else []
    return {
        "apiVersion": "dps.module/v1",
        "kind": "Module",
        "metadata": {"id": module_id},
        "ownership": {"paths": [f"Modules/{module_id}/**"]},
        "dependencies": {"runtime": dependencies},
        "contracts": {"provides": provided, "consumes": consumed},
    }


class ImpactRepository:
    def __init__(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="dps-module-impact-")
        self.root = Path(self.temporary.name)
        (self.root / "Modules").mkdir()
        (self.root / "AGENTS.md").write_text("# Root policy\n", encoding="utf-8")
        schema_target = (
            self.root
            / "governance"
            / "schemas"
            / "phase0-instruction-receipt.schema.json"
        )
        schema_target.parent.mkdir(parents=True)
        schema_target.write_bytes(
            (
                ROOT
                / "governance"
                / "schemas"
                / "phase0-instruction-receipt.schema.json"
            ).read_bytes()
        )
        for module_id in ("provider", "consumer", "observer"):
            module_root = self.root / "Modules" / module_id
            module_root.mkdir()
            layout = {
                "src/implementation.py": "VALUE = 1\n",
                "contracts/provided/.gitkeep": "tracked\n",
                "contracts/consumed/.gitkeep": "tracked\n",
                "tests/test_smoke.py": "import unittest\n",
                "migrations/README.md": "No migrations.\n",
                "operations/README.md": "No operations.\n",
                "CHANGELOG.md": "# Changelog\n",
            }
            for relative, content in layout.items():
                target = module_root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(content, encoding="utf-8")
            (module_root / "AGENTS.md").write_text(
                "---\n"
                "agents_spec: dps.agents/v1\n"
                "policy_version: 1.0.0\n"
                f"module_id: {module_id}\n"
                "manifest: ./module.yaml\n"
                "applies_to: .\n"
                "---\n\n"
                f"# {module_id}\n\n"
                "Read contracts, compatibility, tests, communication, canary rollout, "
                "and rollback evidence before changing this module.\n",
                encoding="utf-8",
            )
            (module_root / "module.yaml").write_text(
                json.dumps(manifest(module_id), indent=2) + "\n",
                encoding="utf-8",
            )
        self.contract = (
            self.root
            / "Modules"
            / "provider"
            / "contracts"
            / "provided"
            / "sample.event.v1.schema.json"
        )
        self.contract.parent.mkdir(parents=True, exist_ok=True)
        self.write_contract("base")
        git(self.root, "init", "-q")
        git(self.root, "config", "user.email", "module-impact@dps.invalid")
        git(self.root, "config", "user.name", "DPS Module Impact")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "baseline")
        git(self.root, "branch", "-M", "main")
        self.base = git(self.root, "rev-parse", "HEAD").stdout.strip()

    def close(self) -> None:
        self.temporary.cleanup()

    def write_contract(self, title: str) -> None:
        self.contract.write_text(
            json.dumps(contract_schema(title), indent=2) + "\n",
            encoding="utf-8",
        )

    def commit_contract(self, branch: str, title: str) -> str:
        git(self.root, "checkout", "-q", "-B", branch, self.base)
        self.write_contract(title)
        git(self.root, "add", self.contract.relative_to(self.root).as_posix())
        git(self.root, "commit", "-qm", title)
        return git(self.root, "rev-parse", "HEAD").stdout.strip()

    def merge_provider_change(self) -> tuple[str, str, str]:
        provider_head = self.commit_contract("provider-change", "provider-change")
        git(self.root, "checkout", "-q", "-B", "integration", self.base)
        observer_manifest = self.root / "Modules" / "observer" / "module.yaml"
        observer = manifest("observer")
        observer["dependencies"]["runtime"] = ["provider"]
        observer["contracts"]["consumes"] = [contract_item("provider")]
        observer_manifest.write_text(
            json.dumps(observer, indent=2) + "\n",
            encoding="utf-8",
        )
        git(self.root, "add", observer_manifest.relative_to(self.root).as_posix())
        git(self.root, "commit", "-qm", "advance integration base")
        integration_base = git(self.root, "rev-parse", "HEAD").stdout.strip()
        git(
            self.root,
            "merge",
            "--no-ff",
            "-m",
            "merge provider change",
            "provider-change",
        )
        merge_head = git(self.root, "rev-parse", "HEAD").stdout.strip()
        return merge_head, integration_base, provider_head


class ModuleImpactSuite(unittest.TestCase):
    def setUp(self) -> None:
        self.repo = ImpactRepository()

    def tearDown(self) -> None:
        self.repo.close()

    def assert_impact(
        self,
        baseline: str,
        expected_scope: list[str],
    ) -> dict:
        receipt = resolve_instruction_receipt(
            self.repo.root,
            baseline,
            agent_identity="module-impact-suite",
            agent_role="evidence-auditor",
            resolved_at="2026-07-24T00:00:00Z",
        )
        self.assertEqual(expected_scope, receipt["scope"])
        valid, message, current = validate_instruction_receipt(
            self.repo.root,
            receipt,
        )
        self.assertTrue(valid, message)
        self.assertEqual(receipt, current)
        return receipt

    def test_provider_consumer_and_dag_impact(self) -> None:
        self.repo.write_contract("working-change")
        records = load_module_records_without_schema(self.repo.root)
        graph = build_dependency_graph_snapshot(records)
        self.assertIn(
            {
                "consumer": "consumer",
                "provider": "provider",
                "reason": "declared module dependency",
            },
            graph["edges"],
        )
        self.assert_impact(self.repo.base, ["consumer", "provider"])

    def test_simulated_parallel_change_conflicts_fail_closed(self) -> None:
        self.repo.commit_contract("provider-change", "provider-change")
        self.repo.commit_contract("parallel-change", "parallel-change")
        git(self.repo.root, "checkout", "-q", "-B", "integration", self.repo.base)
        git(
            self.repo.root,
            "merge",
            "--no-ff",
            "-m",
            "merge provider change",
            "provider-change",
        )
        before_conflict = git(
            self.repo.root, "rev-parse", "HEAD"
        ).stdout.strip()
        conflict = git(
            self.repo.root,
            "merge",
            "--no-ff",
            "-m",
            "merge parallel change",
            "parallel-change",
            check=False,
        )
        self.assertNotEqual(0, conflict.returncode)
        self.assertIn("CONFLICT", conflict.stdout)
        self.assertEqual(
            before_conflict,
            git(self.repo.root, "rev-parse", "HEAD").stdout.strip(),
        )
        with self.assertRaisesRegex(Phase0Error, "unmerged paths fail closed"):
            resolve_instruction_receipt(
                self.repo.root,
                self.repo.base,
                agent_identity="module-impact-suite",
                agent_role="evidence-auditor",
                resolved_at="2026-07-24T00:00:00Z",
            )
        git(self.repo.root, "merge", "--abort")
        self.assertEqual(
            "",
            git(self.repo.root, "status", "--porcelain").stdout.strip(),
        )

    def test_exact_merge_head_is_rerun(self) -> None:
        provider_head = self.repo.commit_contract(
            "provider-change", "provider-change"
        )
        self.assert_impact(self.repo.base, ["consumer", "provider"])
        git(self.repo.root, "checkout", "-q", "-B", "integration", self.repo.base)
        observer_manifest = self.repo.root / "Modules" / "observer" / "module.yaml"
        observer = manifest("observer")
        observer["dependencies"]["runtime"] = ["provider"]
        observer["contracts"]["consumes"] = [contract_item("provider")]
        observer_manifest.write_text(
            json.dumps(observer, indent=2) + "\n",
            encoding="utf-8",
        )
        git(
            self.repo.root,
            "add",
            observer_manifest.relative_to(self.repo.root).as_posix(),
        )
        git(self.repo.root, "commit", "-qm", "advance integration base")
        integration_base = git(
            self.repo.root, "rev-parse", "HEAD"
        ).stdout.strip()
        git(
            self.repo.root,
            "merge",
            "--no-ff",
            "-m",
            "merge provider change",
            provider_head,
        )
        merge_head = git(self.repo.root, "rev-parse", "HEAD").stdout.strip()
        parents = git(
            self.repo.root, "rev-list", "--parents", "-n", "1", merge_head
        ).stdout.split()
        self.assertEqual(3, len(parents))
        self.assertNotEqual(
            git(self.repo.root, "rev-parse", provider_head + "^{tree}").stdout.strip(),
            git(self.repo.root, "rev-parse", merge_head + "^{tree}").stdout.strip(),
        )
        receipt = self.assert_impact(
            integration_base,
            ["consumer", "observer", "provider"],
        )
        self.assertEqual(
            merge_head,
            git(self.repo.root, "rev-parse", "HEAD").stdout.strip(),
        )
        self.assertEqual(integration_base, receipt["baseline_commit"])

    def test_rollback_reverts_bytes_and_reruns_impact(self) -> None:
        merge_head, _, _ = self.repo.merge_provider_change()
        git(self.repo.root, "revert", "-m", "1", "--no-edit", merge_head)
        rollback_head = git(self.repo.root, "rev-parse", "HEAD").stdout.strip()
        self.assertNotEqual(merge_head, rollback_head)
        self.assertEqual("base", json.loads(self.repo.contract.read_text())["title"])
        self.assert_impact(
            merge_head,
            ["consumer", "observer", "provider"],
        )


if __name__ == "__main__":
    unittest.main()
