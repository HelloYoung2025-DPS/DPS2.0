from __future__ import annotations

import json
from pathlib import Path
import unittest


MODULE_ROOT = Path(__file__).resolve().parents[1]
PROVIDED = MODULE_ROOT / "contracts" / "provided"


class RollbackContractTests(unittest.TestCase):
    def _load(self, name: str) -> dict:
        with (PROVIDED / name).open("r", encoding="utf-8") as handle:
            return json.load(handle)

    def test_all_owned_contracts_are_strict_versioned_draft_2020_12(self):
        expected = {
            "rollback.request.v1.schema.json",
            "rollback.plan.v1.schema.json",
            "rollback.result.v1.schema.json",
        }
        self.assertEqual(expected, {path.name for path in PROVIDED.glob("*.json")})
        for name in expected:
            with self.subTest(name=name):
                schema = self._load(name)
                self.assertEqual("https://json-schema.org/draft/2020-12/schema", schema["$schema"])
                self.assertFalse(schema["additionalProperties"])
                self.assertEqual(len(schema["required"]), len(set(schema["required"])))

    def test_request_producer_and_compensation_boundary_are_machine_readable(self):
        schema = self._load("rollback.request.v1.schema.json")
        self.assertEqual("factory-release-controller", schema["properties"]["producer_module"]["const"])
        branch = schema["allOf"][0]
        self.assertEqual("NON_ROLLBACKABLE", branch["if"]["properties"]["rollback_unit"]["const"])
        self.assertEqual(1, branch["then"]["properties"]["external_effects"]["minItems"])
        self.assertEqual(0, branch["else"]["properties"]["external_effects"]["maxItems"])
        self.assertIsNone(branch["else"]["properties"]["compensation_plan"]["const"])

    def test_plan_contract_encodes_both_exact_five_step_sequences(self):
        schema = self._load("rollback.plan.v1.schema.json")
        self.assertTrue({"request_sha256", "stable_bom_verification_id"}.issubset(schema["required"]))
        branch = schema["allOf"][0]
        rollbackable = [item["const"] for item in branch["then"]["properties"]["ordered_steps"]["prefixItems"]]
        compensation = [item["const"] for item in branch["else"]["properties"]["ordered_steps"]["prefixItems"]]
        self.assertEqual(
            ["STOP_ROUTING", "DRAIN", "RECONCILE", "SWITCH_BOM", "VERIFY"],
            rollbackable,
        )
        self.assertEqual(
            ["STOP_ROUTING", "DRAIN", "RECONCILE", "COMPENSATE", "VERIFY"],
            compensation,
        )

    def test_result_contract_never_calls_compensation_rolled_back(self):
        schema = self._load("rollback.result.v1.schema.json")
        self.assertTrue(
            {"request_sha256", "plan_sha256", "authorization_id", "stable_bom_verification_id"}.issubset(
                schema["required"]
            )
        )
        outcomes = schema["properties"]["outcome"]["enum"]
        self.assertIn("ROLLED_BACK", outcomes)
        self.assertIn("COMPENSATED", outcomes)
        compensated = schema["allOf"][1]["then"]["properties"]
        self.assertEqual("NON_ROLLBACKABLE", compensated["rollback_unit"]["const"])
        self.assertEqual(1, compensated["compensation_evidence_ids"]["minItems"])


if __name__ == "__main__":
    unittest.main()
