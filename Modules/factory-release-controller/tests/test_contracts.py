import copy
import hashlib
import importlib.util
import json
import sys
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker

ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "release_controller.py"
SUBJECT_NAME = "_dps_factory_release_controller_contract_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("contract subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != ROOT or source_path.parent != source_root:
        raise ImportError("contract subject escaped the module-owned src directory")

    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        existing_path = Path(getattr(existing, "__file__", "")).resolve(strict=True)
        if existing_path != source_path:
            raise ImportError("contract subject module name is already bound elsewhere")
        return existing

    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the contract subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()
ReleaseController = SUBJECT.ReleaseController


def command(from_state, to_state, key=None):
    return {
        "schema_version": "2.0.0",
        "contract_id": "rollout.command/v2",
        "producer_module": "factory-control-plane-host",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + SUBJECT.sha256({
            "fixture_key": key or f"transition-{from_state.lower()}-{to_state.lower()}"
        }),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "upgrade_id": "upgrade-001",
        "from_state": from_state,
        "to_state": to_state,
        "transition_evidence": {
            "evidence_refs": [f"receipt:evidence:state-{to_state.lower()}"]
        },
    }


def legacy_v1_command():
    return {
        "schema_version": "1.0.0",
        "contract_id": "rollout.command/v1",
        "producer_module": "factory-upgrade-intake",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + SUBJECT.sha256({"legacy": "command-v1"}),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "upgrade_id": "upgrade-001",
        "from_state": "REQUESTED",
        "to_state": "SCOPE_RESOLVED",
        "risk_tier": "R3",
        "transition_evidence": {"evidence_refs": ["legacy-evidence-001"]},
    }


class DurableLedger:
    def __init__(self):
        self.events = []

    def append(self, request):
        previous = self.events[-1]["event_sha256"] if self.events else "0" * 64
        event = {
            "schema_version": "1.0.0",
            "contract_id": "upgrade.event/v1",
            "producer_module": "factory-evidence-ledger",
            "soul_id": request["soul_id"],
            "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"],
            "trace_id": request["trace_id"],
            "idempotency_key": request["idempotency_key"],
            "occurred_at": request["occurred_at"],
            "privacy_class": "internal",
            "event_id": "event-" + SUBJECT.sha256({
                "stream_id": request["stream_id"],
                "idempotency_key": request["idempotency_key"],
            })[:32],
            "stream_id": request["stream_id"],
            "sequence": request["expected_sequence"] + 1,
            "event_type": request["event_type"],
            "source_module": request["producer_module"],
            "payload": copy.deepcopy(request["payload"]),
            "payload_sha256": request["payload_sha256"],
            "previous_event_sha256": previous,
            "append_status": "APPENDED",
        }
        event["event_sha256"] = SUBJECT.sha256({
            key: value for key, value in event.items()
            if key not in {"event_sha256", "append_status"}
        })
        self.events.append(copy.deepcopy(event))
        return event


class FactsResolver:
    def __call__(self, upgrade_id, from_state, to_state, evidence_refs):
        return SUBJECT.TrustedTransitionFacts(
            upgrade_id=upgrade_id,
            resolved_evidence_refs=tuple(evidence_refs),
            receipt_set_sha256=SUBJECT.sha256({
                "resolved_evidence_refs": list(evidence_refs),
                "source": "trusted-contract-fixture",
            }),
            risk_tier="R3",
            evidence_kind="INTEGRATION",
            verification_level="INTEGRATION_VERIFIED",
            simulation_only=False,
            side_effect_count=0,
            kill_switch_armed=False,
            observed_bom_sha256=None,
            observed_artifact_sha256=None,
            candidate_validation=None,
        )


def load(name):
    return json.loads((ROOT / "contracts" / "provided" / name).read_text(encoding="utf-8"))


class ReleaseContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.command_schema = load("rollout.command.v2.schema.json")
        cls.event_schema = load("rollout.event.v2.schema.json")
        cls.legacy_command_schema = load("rollout.command.v1.schema.json")
        cls.legacy_event_schema = load("rollout.event.v1.schema.json")
        cls.native_stop_trust_schema = load(
            "release.bom.native.stop.authority.trust.v1.schema.json"
        )
        cls.freeze = load("rollout.v1.frozen-sha256.json")
        cls.manifest = json.loads((ROOT / "module.yaml").read_text(encoding="utf-8"))
        for schema in (
            cls.command_schema, cls.event_schema,
            cls.legacy_command_schema, cls.legacy_event_schema,
            cls.native_stop_trust_schema,
        ):
            Draft202012Validator.check_schema(schema)

    def validate(self, schema, value):
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)

    def assert_runtime_command_rejected(self, value):
        ledger = DurableLedger()
        controller = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            FactsResolver(), ledger.append,
        )
        with self.assertRaises(SUBJECT.InvalidReleaseCommand):
            controller.transition(value)
        self.assertEqual([], ledger.events)

    def test_positive_command_and_event_validate(self):
        request = command("REQUESTED", "SCOPE_RESOLVED")
        request["device_binding_id"] = "db_" + "3" * 32
        request["platform_account_id"] = "pa_" + "4" * 32
        self.validate(self.command_schema, request)
        ledger = DurableLedger()
        event = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            FactsResolver(), ledger.append,
        ).transition(request)
        self.validate(self.event_schema, event)

    def test_schema_and_runtime_cover_the_same_complete_field_inventories(self):
        self.assertEqual(
            set(self.command_schema["required"]),
            set(self.command_schema["properties"]),
        )
        self.assertEqual(
            set(self.command_schema["required"]),
            SUBJECT._COMMAND_FIELDS,
        )
        self.assertEqual(
            set(self.event_schema["required"]),
            set(self.event_schema["properties"]),
        )
        self.assertEqual(
            set(self.event_schema["required"]),
            SUBJECT._ROLLOUT_EVENT_FIELDS,
        )

    def test_every_declared_receipt_kind_matches_schema_and_runtime(self):
        self.assertEqual(
            {
                "approval", "artifact", "candidate", "canary", "evidence",
                "impact", "instruction", "merge", "rollback", "shadow",
                "test", "worktree",
            },
            SUBJECT._RECEIPT_KINDS,
        )
        for kind in sorted(SUBJECT._RECEIPT_KINDS):
            with self.subTest(kind=kind):
                request = command("REQUESTED", "SCOPE_RESOLVED")
                request["transition_evidence"]["evidence_refs"] = [
                    f"receipt:{kind}:reference-001"
                ]
                self.validate(self.command_schema, request)
                ledger = DurableLedger()
                event = ReleaseController(
                    "upgrade-001", "controller-service", ["controller-service"],
                    FactsResolver(), ledger.append,
                ).transition(request)
                self.validate(self.event_schema, event)

    def test_all_native_stop_trust_patterns_reject_trailing_newlines(self):
        samples = {
            "properties/trace_id": "trace_" + "1" * 32,
            "properties/idempotency_key": "idem_" + "2" * 64,
            "properties/receipt_id": "native-stop-trust-" + "3" * 32,
            "properties/integration_commit": "4" * 40,
            "$defs/id": "release-bom-001",
            "$defs/sha256": "5" * 64,
            "$defs/semver": "1.2.3",
            "$defs/nativeStopAuthority/properties/authority_id": "authority-001",
            "$defs/nativeStopAuthority/properties/worker_version": "1.2.3",
            "$defs/nativeStopAuthority/properties/worker_instance_id": "wi_" + "6" * 32,
            "$defs/nativeStopAuthority/properties/key_id": "worker-key-001",
            "$defs/deviceRouteAssignmentAuthority/properties/route_authority_id":
                "route-authority-001",
            "$defs/deviceRouteAssignmentAuthority/properties/supervisor_instance_id":
                "si_" + "7" * 32,
            "$defs/deviceRouteAssignmentAuthority/properties/route_signer_key_id":
                "p256_spki_" + "8" * 64,
            "$defs/nativeStopChallengeAuthority/properties/authority_id":
                "challenge-authority-001",
            "$defs/nativeStopChallengeAuthority/properties/policy_instance_id":
                "pi_" + "9" * 32,
            "$defs/nativeStopChallengeAuthority/properties/key_id": "policy-key-001",
            "$defs/strictSemver": "1.2.3",
            "$defs/canonicalRuntimeUtc": "2026-07-15T00:00:00.0000000Z",
        }
        self.assertEqual(19, len(samples))

        discovered = {}

        def collect(value, path=()):
            if isinstance(value, dict):
                if "pattern" in value:
                    discovered["/".join(path)] = value["pattern"]
                for key, item in value.items():
                    collect(item, path + (key,))
            elif isinstance(value, list):
                for index, item in enumerate(value):
                    collect(item, path + (str(index),))

        collect(self.native_stop_trust_schema)
        self.assertEqual(set(samples), set(discovered))
        for path, valid_value in samples.items():
            validator = Draft202012Validator({
                "type": "string",
                "pattern": discovered[path],
            })
            self.assertEqual([], list(validator.iter_errors(valid_value)), path)
            for suffix in ("\n", "\r"):
                with self.subTest(path=path, suffix=repr(suffix)):
                    self.assertTrue(
                        list(validator.iter_errors(valid_value + suffix)),
                        f"{path} accepted trailing {suffix!r}",
                    )

    def test_native_stop_trust_contract_has_unique_release_owner_and_exact_host_route(self):
        contract_id = "release.bom.native.stop.authority.trust"
        provided = [
            item for item in self.manifest["contracts"]["provided"]
            if item["contractId"] == contract_id
        ]
        self.assertEqual(1, len(provided))
        self.assertEqual(
            {
                "contractId": contract_id,
                "major": 1,
                "source": (
                    "Modules/factory-release-controller/contracts/provided/"
                    "release.bom.native.stop.authority.trust.v1.schema.json"
                ),
                "status": "proposed",
                "mode": "active",
                "ownerModule": "factory-release-controller",
            },
            provided[0],
        )
        routes = [
            item for item in self.manifest["communication"]["outbound"]
            if item["contractId"] == contract_id
        ]
        self.assertEqual(
            [{
                "peerModule": "factory-control-plane-host",
                "contractId": contract_id,
                "major": 1,
                "direction": "outbound",
                "transport": "receipt",
                "timeoutMs": 5000,
                "retryPolicy": "same-receipt-id-and-payload-sha256",
                "idempotencyKey": "receipt_id:release_bom_sha256",
                "authScope": "factory:host:native.stop.authority.trust",
                "failureMode": (
                    "host-must-not-activate-bom-without-verified-trust-receipt"
                ),
            }],
            routes,
        )
        self.assertEqual(
            "release.bom.native.stop.authority.trust/v1",
            self.native_stop_trust_schema["properties"]["contract_id"]["const"],
        )
        contract_id_validator = Draft202012Validator(
            self.native_stop_trust_schema["properties"]["contract_id"]
        )
        self.assertTrue(list(contract_id_validator.iter_errors(
            "release.bom.native-stop-authority-trust/v1"
        )))
        self.assertEqual(
            "factory-release-controller",
            self.native_stop_trust_schema["properties"]["producer_module"]["const"],
        )
        self.assertEqual(
            [1],
            self.manifest["compatibility"]["supportedContractMajors"][contract_id],
        )
        self.assertEqual("reject", self.manifest["compatibility"]["unknownMajorBehavior"])

        property_names = set()

        def collect_property_names(value):
            if isinstance(value, dict):
                properties = value.get("properties")
                if isinstance(properties, dict):
                    property_names.update(properties)
                for item in value.values():
                    collect_property_names(item)
            elif isinstance(value, list):
                for item in value:
                    collect_property_names(item)

        collect_property_names(self.native_stop_trust_schema)
        forbidden_raw_secret_fields = {
            "private_key", "private_key_pem", "secret", "client_secret",
            "api_key", "access_token", "activation_token",
        }
        self.assertTrue(forbidden_raw_secret_fields.isdisjoint(property_names))

    def test_frozen_v1_bytes_modes_and_full_shape_have_no_runtime_authority(self):
        expected_hashes = {
            item["path"]: item["sha256"] for item in self.freeze["files"]
        }
        self.assertEqual("quarantine-only", self.freeze["mode"])
        self.assertEqual(
            {
                "Modules/factory-release-controller/contracts/provided/rollout.command.v1.schema.json":
                    "2f3a3e9a872c9626e095be9187c06ac8cfedcb61e66448d1aa12c84a5ac93736",
                "Modules/factory-release-controller/contracts/provided/rollout.event.v1.schema.json":
                    "469acfbc43c76b7d97b78c51fe1bf4db20a919aa7a1a5f9a6371400c8bf41298",
            },
            expected_hashes,
        )
        for relative_path, expected_sha256 in expected_hashes.items():
            actual = hashlib.sha256((ROOT.parents[1] / relative_path).read_bytes()).hexdigest()
            self.assertEqual(expected_sha256, actual, relative_path)

        declared = {
            (item["contractId"], item["major"]): (item["status"], item["mode"])
            for item in self.manifest["contracts"]["provided"]
            if item["contractId"] in {"rollout.command", "rollout.event"}
        }
        self.assertEqual(
            {
                ("rollout.command", 1): ("deprecated", "quarantine-only"),
                ("rollout.command", 2): ("proposed", "active"),
                ("rollout.event", 1): ("deprecated", "quarantine-only"),
                ("rollout.event", 2): ("proposed", "active"),
            },
            declared,
        )
        rollout_edges = [
            edge for direction in ("inbound", "outbound")
            for edge in self.manifest["communication"][direction]
            if edge["contractId"] in {"rollout.command", "rollout.event"}
        ]
        self.assertTrue(rollout_edges)
        self.assertTrue(all(edge["major"] == 2 for edge in rollout_edges))

        legacy = legacy_v1_command()
        self.validate(self.legacy_command_schema, legacy)
        ledger = DurableLedger()
        controller = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            FactsResolver(), ledger.append,
        )
        with self.assertRaises(SUBJECT.QuarantinedReleaseCommand):
            controller.transition(legacy)
        self.assertEqual([], ledger.events)

        active_event = controller.transition(command("REQUESTED", "SCOPE_RESOLVED"))
        legacy_event = copy.deepcopy(active_event)
        legacy_event["schema_version"] = "1.0.0"
        legacy_event["contract_id"] = "rollout.event/v1"
        legacy_event.pop("receipt_set_sha256")
        self.validate(self.legacy_event_schema, legacy_event)
        self.assertEqual(
            "QUARANTINE_ONLY_V1", SUBJECT.classify_rollout_event_major(legacy_event)
        )

    def test_unknown_major_missing_common_and_extra_fields_fail(self):
        invalid = command("REQUESTED", "SCOPE_RESOLVED")
        invalid["schema_version"] = "3.0.0"
        invalid["contract_id"] = "rollout.command/v3"
        self.assertRaises(Exception, self.validate, self.command_schema, invalid)
        invalid = command("REQUESTED", "SCOPE_RESOLVED")
        del invalid["trace_id"]
        self.assertRaises(Exception, self.validate, self.command_schema, invalid)
        invalid = command("REQUESTED", "SCOPE_RESOLVED")
        invalid["approval"] = True
        self.assertRaises(Exception, self.validate, self.command_schema, invalid)
        invalid = command("REQUESTED", "SCOPE_RESOLVED")
        invalid["risk_tier"] = "R0"
        self.assertRaises(Exception, self.validate, self.command_schema, invalid)

    def test_wrong_producer_and_illegal_identity_fail(self):
        invalid = command("REQUESTED", "SCOPE_RESOLVED")
        invalid["producer_module"] = "factory-upgrade-intake"
        self.assertRaises(Exception, self.validate, self.command_schema, invalid)
        invalid = command("REQUESTED", "SCOPE_RESOLVED")
        invalid["platform_account_id"] = "account-without-pa-prefix"
        self.assertRaises(Exception, self.validate, self.command_schema, invalid)

    def test_raw_intents_empty_refs_masquerades_and_bounds_fail_schema(self):
        for major in (1, 2):
            raw_intent = {
                "schema_version": f"{major}.0.0",
                "contract_id": f"upgrade.intent/v{major}",
                "producer_module": "factory-upgrade-intake",
                "upgrade_id": "upgrade-001",
            }
            self.assertRaises(Exception, self.validate, self.command_schema, raw_intent)

            nested = command("REQUESTED", "SCOPE_RESOLVED")
            nested["transition_evidence"]["upgrade_intent"] = raw_intent
            self.assertRaises(Exception, self.validate, self.command_schema, nested)

            receipt_object = command("REQUESTED", "SCOPE_RESOLVED")
            receipt_object["transition_evidence"]["evidence_refs"] = [raw_intent]
            self.assertRaises(Exception, self.validate, self.command_schema, receipt_object)

        invalid_sets = [
            [],
            ["receipt:evidence:duplicate-001", "receipt:evidence:duplicate-001"],
            [f"receipt:evidence:bounded-{index:03d}" for index in range(65)],
            ["receipt:upgrade.intent:upgrade-001"],
            ["receipt:upgrade.intent.v2:upgrade-001"],
            ["receipt:upgrade-intent:upgrade-001"],
            ["receipt:upgrade--intent"],
            ["receipt:upgrade--intent:upgrade-001"],
            ["receipt:dps.upgrade-intent:upgrade-001"],
            ["receipt:evidence:bad--identifier"],
            ["receipt:evidence:bad..identifier"],
            ["receipt:evidence:bad__identifier"],
            ["receipt:Evidence:state-scope-resolved"],
            ["receipt:evidence:State-scope-resolved"],
            ["receipt:evidence:state:scope-resolved"],
        ]
        for references in invalid_sets:
            invalid = command("REQUESTED", "SCOPE_RESOLVED")
            invalid["transition_evidence"]["evidence_refs"] = references
            self.assertRaises(Exception, self.validate, self.command_schema, invalid)
            self.assert_runtime_command_rejected(invalid)

    def test_upgrade_id_and_time_schema_match_strict_runtime_validation(self):
        invalid_upgrade_ids = (
            "Upgrade-001", "upgrade--001", "upgrade..001", "upgrade__001",
            "short", "upgrade-001-", "upgrade:001::next",
        )
        for upgrade_id in invalid_upgrade_ids:
            with self.subTest(upgrade_id=upgrade_id):
                invalid = command("REQUESTED", "SCOPE_RESOLVED")
                invalid["upgrade_id"] = upgrade_id
                self.assertRaises(Exception, self.validate, self.command_schema, invalid)
                self.assert_runtime_command_rejected(invalid)

        invalid_times = (
            "2026-07-14T00:00:00+00:00",
            "2026-07-14T00:00:00.000Z",
            "2026-02-30T00:00:00Z",
            "2026-07-14t00:00:00Z",
            "2026-07-14T00:00:00z",
            "2026-07-14T00:00:00Z\n",
        )
        for occurred_at in invalid_times:
            with self.subTest(occurred_at=repr(occurred_at)):
                invalid = command("REQUESTED", "SCOPE_RESOLVED")
                invalid["occurred_at"] = occurred_at
                self.assertRaises(Exception, self.validate, self.command_schema, invalid)
                self.assert_runtime_command_rejected(invalid)

    def test_strict_wire_parser_has_no_duplicate_member_or_nonfinite_escape(self):
        compact = json.dumps(
            command("REQUESTED", "SCOPE_RESOLVED"),
            sort_keys=True,
            separators=(",", ":"),
        )
        duplicate_root = compact[:-1] + ',"upgrade_id":"upgrade-001"}'
        duplicate_nested = compact.replace(
            '"transition_evidence":{"evidence_refs":',
            '"transition_evidence":{"evidence_refs":[],"evidence_refs":',
            1,
        )
        for invalid_wire in (
            duplicate_root,
            duplicate_nested,
            '{"schema_version":NaN}',
            "[]",
            b'{"schema_version":"2.0.0"}\xff',
        ):
            with self.subTest(wire=repr(invalid_wire)[:80]):
                with self.assertRaises(SUBJECT.InvalidReleaseCommand):
                    SUBJECT.parse_rollout_command_json(invalid_wire)

        parsed = SUBJECT.parse_rollout_command_json(compact.encode("utf-8"))
        self.assertEqual(command("REQUESTED", "SCOPE_RESOLVED"), parsed)

    def test_simulation_event_cannot_claim_canary_or_side_effect(self):
        ledger = DurableLedger()
        event = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            FactsResolver(), ledger.append,
        ).transition(command("REQUESTED", "SCOPE_RESOLVED"))
        invalid = copy.deepcopy(event)
        invalid.update({
            "evidence_kind": "SIMULATION",
            "simulation_only": True,
            "verification_level": "CANARY_VERIFIED",
        })
        self.assertRaises(Exception, self.validate, self.event_schema, invalid)
        invalid["verification_level"] = "INTEGRATION_VERIFIED"
        invalid["side_effect_count"] = 1
        self.assertRaises(Exception, self.validate, self.event_schema, invalid)

    def test_event_requires_nonzero_receipt_set_digest_and_exact_receipts(self):
        ledger = DurableLedger()
        event = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            FactsResolver(), ledger.append,
        ).transition(command("REQUESTED", "SCOPE_RESOLVED"))

        invalid = copy.deepcopy(event)
        invalid["receipt_set_sha256"] = "0" * 64
        self.assertRaises(Exception, self.validate, self.event_schema, invalid)

        invalid = copy.deepcopy(event)
        invalid["evidence_refs"] = []
        self.assertRaises(Exception, self.validate, self.event_schema, invalid)

        invalid = copy.deepcopy(event)
        invalid["evidence_refs"] = ["receipt:upgrade.intent:upgrade-001"]
        self.assertRaises(Exception, self.validate, self.event_schema, invalid)

        for reference in (
            "receipt:upgrade--intent:upgrade-001",
            "receipt:evidence:bad--identifier",
            "receipt:Evidence:state-scope-resolved",
        ):
            with self.subTest(reference=reference):
                invalid = copy.deepcopy(event)
                invalid["evidence_refs"] = [reference]
                self.assertRaises(Exception, self.validate, self.event_schema, invalid)

    def test_event_side_effect_count_is_an_exact_nonnegative_integer(self):
        ledger = DurableLedger()
        event = ReleaseController(
            "upgrade-001", "controller-service", ["controller-service"],
            FactsResolver(), ledger.append,
        ).transition(command("REQUESTED", "SCOPE_RESOLVED"))

        for invalid_count in (True, -1):
            with self.subTest(side_effect_count=invalid_count):
                invalid = copy.deepcopy(event)
                invalid["side_effect_count"] = invalid_count
                self.assertRaises(Exception, self.validate, self.event_schema, invalid)


if __name__ == "__main__":
    unittest.main()
