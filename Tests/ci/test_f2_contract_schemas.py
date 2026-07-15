from __future__ import annotations

import copy
import json
import unittest
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parents[2]
SOUL_ID = "soul_" + "a" * 64
DEVICE_BINDING_ID = "db_" + "b" * 32
PLATFORM_ACCOUNT_ID = "pa_" + "c" * 32
TRACE_ID = "trace_" + "d" * 32
IDEMPOTENCY_KEY = "idem_" + "e" * 64
EVENT_ID = "11111111-1111-4111-8111-111111111111"
OCCURRED_AT = "2026-07-14T01:02:03.1234567Z"
STRICT_FORMAT_CHECKER = FormatChecker()


@STRICT_FORMAT_CHECKER.checks("date-time", raises=(TypeError, ValueError))
def is_real_datetime(value: object) -> bool:
    if not isinstance(value, str):
        return False
    datetime.fromisoformat(value.replace("Z", "+00:00"))
    return True


def common(contract_id: str, producer_module: str) -> dict[str, Any]:
    return {
        "schema_version": "1.0.0",
        "contract_id": contract_id,
        "producer_module": producer_module,
        "soul_id": SOUL_ID,
        "device_binding_id": DEVICE_BINDING_ID,
        "platform_account_id": PLATFORM_ACCOUNT_ID,
        "trace_id": TRACE_ID,
        "idempotency_key": IDEMPOTENCY_KEY,
        "occurred_at": OCCURRED_AT,
        "privacy_class": "personal",
    }


SOUL_RESOLVED = {
    **common("soul.resolved/v1", "soul-registry"),
    "privacy_class": "pseudonymous",
    "alias_kind": "email",
    "alias_digest": "f" * 64,
    "alias_key_id": "alias-key-01",
}
MEMORY_EVENT = {
    **common("memory.event/v1", "memory-event-ledger"),
    "event_id": EVENT_ID,
    "event_type": "content.observed",
    "observation": {
        "content_digest": "1" * 64,
        "verified": True,
        "interest_signals": [{"topic": "robotics", "confidence": 0.75}],
    },
}
MEMORY_OUTBOX = {
    **common("memory.outbox/v1", "memory-event-ledger"),
    "outbox_id": "22222222-2222-4222-8222-222222222222",
    "event_id": EVENT_ID,
    "topic": "memory.event/v1",
    "payload_sha256": "2" * 64,
}
INTEREST_SNAPSHOT = {
    **common("interest.snapshot/v1", "interest-reducer"),
    "as_of": "2026-07-14T02:03:04Z",
    "algorithm_version": "exponential-half-life/v1",
    "half_life_seconds": 86400,
    "source_event_count": 1,
    "interests": [
        {
            "topic": "robotics",
            "original_confidence": 0.75,
            "decayed_confidence": 0.5,
            "half_life_seconds": 86400,
            "algorithm_version": "exponential-half-life/v1",
            "evidence": [
                {
                    "event_id": EVENT_ID,
                    "event_hash": "3" * 64,
                    "occurred_at": "2026-07-14T01:02:03Z",
                    "original_confidence": 0.75,
                    "decayed_confidence": 0.5,
                }
            ],
        }
    ],
}
GBRAIN_PROJECTION = {
    **common("gbrain.projection/v1", "gbrain-projector"),
    "source_id": "dps-" + "a" * 28,
    "projection_revision": "4" * 64,
    "projection_checksum": "5" * 64,
    "render_status": "dto-rendered-not-written",
    "source_event_count": 1,
    "events": [
        {
            "event_id": EVENT_ID,
            "event_hash": "6" * 64,
            "content_digest": "7" * 64,
            "occurred_at": "2026-07-14T01:02:03.1Z",
        }
    ],
    "interests": copy.deepcopy(INTEREST_SNAPSHOT["interests"]),
}


CASES = {
    "soul.resolved/v1": (
        ROOT / "Modules/soul-registry/contracts/provided/soul.resolved.v1.schema.json",
        SOUL_RESOLVED,
    ),
    "memory.event/v1": (
        ROOT / "Modules/memory-event-ledger/contracts/provided/memory.event.v1.schema.json",
        MEMORY_EVENT,
    ),
    "memory.outbox/v1": (
        ROOT / "Modules/memory-event-ledger/contracts/provided/memory.outbox.v1.schema.json",
        MEMORY_OUTBOX,
    ),
    "interest.snapshot/v1": (
        ROOT / "Modules/interest-reducer/contracts/provided/interest.snapshot.v1.schema.json",
        INTEREST_SNAPSHOT,
    ),
    "gbrain.projection/v1": (
        ROOT / "Modules/gbrain-projector/contracts/provided/gbrain.projection.v1.schema.json",
        GBRAIN_PROJECTION,
    ),
}


def validator(path: Path) -> Draft202012Validator:
    schema = json.loads(path.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=STRICT_FORMAT_CHECKER)


def set_path(value: dict[str, Any], path: tuple[Any, ...], replacement: Any) -> None:
    target: Any = value
    for segment in path[:-1]:
        target = target[segment]
    target[path[-1]] = replacement


def paths_named(value: Any, names: set[str], prefix: tuple[Any, ...] = ()) -> Iterable[tuple[Any, ...]]:
    if isinstance(value, dict):
        for key, child in value.items():
            path = prefix + (key,)
            if key in names:
                yield path
            yield from paths_named(child, names, path)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from paths_named(child, names, prefix + (index,))


class F2ContractSchemaTests(unittest.TestCase):
    def assert_rejected(self, schema_path: Path, instance: dict[str, Any]) -> None:
        self.assertTrue(list(validator(schema_path).iter_errors(instance)))

    def test_draft_2020_12_schemas_accept_canonical_instances(self) -> None:
        for name, (schema_path, instance) in CASES.items():
            with self.subTest(contract=name):
                self.assertEqual([], list(validator(schema_path).iter_errors(instance)))

    def test_all_five_opaque_identifiers_reject_case_length_and_suffix_attacks(self) -> None:
        fields = {
            "soul_id": SOUL_ID,
            "device_binding_id": DEVICE_BINDING_ID,
            "platform_account_id": PLATFORM_ACCOUNT_ID,
            "trace_id": TRACE_ID,
            "idempotency_key": IDEMPOTENCY_KEY,
        }
        mutations = lambda value: (value.upper(), value[:-1], value + "0", value + "\n", value + " ")
        for contract, (schema_path, instance) in CASES.items():
            for field, valid_value in fields.items():
                for invalid_value in mutations(valid_value):
                    with self.subTest(contract=contract, field=field, invalid=repr(invalid_value)):
                        candidate = copy.deepcopy(instance)
                        candidate[field] = invalid_value
                        self.assert_rejected(schema_path, candidate)

    def test_recursive_hashes_are_exact_lowercase_sha256(self) -> None:
        names = {
            "alias_digest",
            "content_digest",
            "payload_sha256",
            "event_hash",
            "projection_revision",
            "projection_checksum",
        }
        for contract, (schema_path, instance) in CASES.items():
            for path in paths_named(instance, names):
                valid_value = instance
                for segment in path:
                    valid_value = valid_value[segment]
                for invalid_value in ("A" * 64, valid_value[:-1], valid_value + "0", valid_value + "\n"):
                    with self.subTest(contract=contract, path=path, invalid=repr(invalid_value)):
                        candidate = copy.deepcopy(instance)
                        set_path(candidate, path, invalid_value)
                        self.assert_rejected(schema_path, candidate)

    def test_recursive_timestamps_require_real_canonical_utc(self) -> None:
        invalid_timestamps = (
            "2026-07-14T01:02:03+00:00",
            "2026-07-14T01:02:03.1200000Z",
            "2026-07-14T01:02:03Z\n",
            "2026-02-30T01:02:03Z",
            "2026-07-14t01:02:03z",
        )
        for contract, (schema_path, instance) in CASES.items():
            for path in paths_named(instance, {"occurred_at", "as_of"}):
                for invalid_value in invalid_timestamps:
                    with self.subTest(contract=contract, path=path, invalid=invalid_value):
                        candidate = copy.deepcopy(instance)
                        set_path(candidate, path, invalid_value)
                        self.assert_rejected(schema_path, candidate)

    def test_contract_versions_and_gbrain_source_fail_closed(self) -> None:
        for contract, (schema_path, instance) in CASES.items():
            for invalid_version in ("2.0.0", "1.0.0\n", "v1"):
                with self.subTest(contract=contract, version=repr(invalid_version)):
                    candidate = copy.deepcopy(instance)
                    candidate["schema_version"] = invalid_version
                    self.assert_rejected(schema_path, candidate)
        schema_path, instance = CASES["gbrain.projection/v1"]
        for invalid_source in (
            instance["source_id"].upper(),
            instance["source_id"][:-1],
            instance["source_id"] + "0",
            instance["source_id"] + "\n",
        ):
            with self.subTest(source=repr(invalid_source)):
                candidate = copy.deepcopy(instance)
                candidate["source_id"] = invalid_source
                self.assert_rejected(schema_path, candidate)


if __name__ == "__main__":
    unittest.main()
