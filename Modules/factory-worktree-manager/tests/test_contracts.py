import copy
import datetime as dt
import importlib.util
import json
import sys
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import ValidationError


MODULE_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_SPEC = importlib.util.spec_from_file_location(
    "factory_worktree_manager_contract_fixture",
    MODULE_ROOT / "tests" / "test_worktree_manager.py",
)
FIXTURE_MODULE = importlib.util.module_from_spec(FIXTURE_SPEC)
assert FIXTURE_SPEC.loader is not None
sys.modules[FIXTURE_SPEC.name] = FIXTURE_MODULE
FIXTURE_SPEC.loader.exec_module(FIXTURE_MODULE)


def strict_format_checker():
    checker = FormatChecker()

    @checker.checks("date-time")
    def is_datetime(value):
        try:
            parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        except (AttributeError, ValueError):
            return False
        return parsed.tzinfo is not None

    return checker


def valid_worktree_plan():
    return {
        "schema_version": "dps.worktree-plan/v1",
        "contract_id": "worktree.plan/v1",
        "producer_module": "factory-worktree-manager",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "2" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "plan_id": "worktree:" + "a" * 32,
        "change_plan_id": "change:" + "b" * 32,
        "instruction_receipt_id": "instruction:" + "c" * 32,
        "baseline_commit": "d" * 40,
        "entries": [
            {
                "module_id": "alpha-module",
                "writer_identity": "implementer-1",
                "owned_paths": ["Modules/alpha-module/src/domain.py"],
                "worktree_ref": "factory-worktree:alpha-module:" + "e" * 16,
                "depends_on": [],
                "lease_keys": [
                    "module:alpha-module",
                    "path:Modules/alpha-module/src/domain.py",
                ],
            }
        ],
        "contract_worktree": {
            "writer_identity": "contract-architect-1",
            "contract_ids": ["alpha.event"],
            "owned_paths": [
                "Modules/alpha-module/contracts/provided/alpha.event.v1.schema.json"
            ],
            "worktree_ref": "factory-contract-worktree:" + "f" * 16,
            "lease_keys": [
                "contract:alpha.event",
                "path:Modules/alpha-module/contracts/provided/alpha.event.v1.schema.json",
            ],
        },
        "trusted_policy_sha256": "1" * 64,
    }


def valid_worktree_lease():
    return {
        "schema_version": "dps.worktree-lease/v1",
        "contract_id": "worktree.lease/v1",
        "producer_module": "factory-worktree-manager",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "3" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "lease_id": "lease:" + "2" * 32,
        "plan_id": "worktree:" + "a" * 32,
        "holder_identity": "implementer-1",
        "lock_keys": [
            "module:alpha-module",
            "path:Modules/alpha-module/src/domain.py",
        ],
        "lock_tokens": {
            "module:alpha-module": 4,
            "path:Modules/alpha-module/src/domain.py": 7,
        },
        "fencing_token": 7,
        "acquired_at": "2026-07-14T00:00:00Z",
        "expires_at": "2026-07-14T00:05:00Z",
        "status": "ACTIVE",
    }


class WorktreeContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        provided = MODULE_ROOT / "contracts" / "provided"
        cls.plan_schema = json.loads(
            (provided / "worktree.plan.v1.schema.json").read_text(encoding="utf-8")
        )
        cls.lease_schema = json.loads(
            (provided / "worktree.lease.v1.schema.json").read_text(encoding="utf-8")
        )
        for schema in (cls.plan_schema, cls.lease_schema):
            Draft202012Validator.check_schema(schema)
        cls.plan_validator = Draft202012Validator(
            cls.plan_schema, format_checker=strict_format_checker()
        )
        cls.lease_validator = Draft202012Validator(
            cls.lease_schema, format_checker=strict_format_checker()
        )

    def test_complete_v1_plan_and_lease_validate(self):
        self.plan_validator.validate(valid_worktree_plan())
        self.lease_validator.validate(valid_worktree_lease())

    def test_production_planner_and_lease_store_outputs_validate(self):
        fixture = FIXTURE_MODULE.WorktreeManagerTests(
            methodName="test_plan_has_one_writer_per_module_and_one_contract_worktree"
        )
        fixture.setUp()
        try:
            produced_plan = fixture.planner.create_plan(
                fixture._change_plan(), fixture._receipt(), fixture.policy
            )
            produced_lease = fixture.store.acquire(
                plan_id=produced_plan["plan_id"],
                holder_identity="implementer-1",
                lock_keys=[
                    "module:alpha",
                    "path:Modules/alpha/src/domain.py",
                ],
                ttl_seconds=10,
                envelope=fixture._envelope(),
            )
            self.plan_validator.validate(produced_plan)
            self.lease_validator.validate(produced_lease)
        finally:
            fixture.tearDown()

    def test_unknown_major_producer_and_field_fail_closed(self):
        invalid = valid_worktree_plan()
        invalid["contract_id"] = "worktree.plan/v2"
        with self.assertRaises(ValidationError):
            self.plan_validator.validate(invalid)

        invalid = valid_worktree_lease()
        invalid["producer_module"] = "candidate-writer"
        with self.assertRaises(ValidationError):
            self.lease_validator.validate(invalid)

        invalid = valid_worktree_plan()
        invalid["git_command"] = ["reset", "--hard"]
        with self.assertRaises(ValidationError):
            self.plan_validator.validate(invalid)

    def test_path_and_fencing_boundaries_fail_closed(self):
        invalid = copy.deepcopy(valid_worktree_plan())
        invalid["entries"][0]["owned_paths"] = ["../outside.py"]
        with self.assertRaises(ValidationError):
            self.plan_validator.validate(invalid)

        invalid = valid_worktree_lease()
        invalid["fencing_token"] = 0
        with self.assertRaises(ValidationError):
            self.lease_validator.validate(invalid)

        invalid = valid_worktree_lease()
        del invalid["expires_at"]
        with self.assertRaises(ValidationError):
            self.lease_validator.validate(invalid)


if __name__ == "__main__":
    unittest.main()
