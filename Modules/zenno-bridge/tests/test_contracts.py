import copy
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import ValidationError


MODULE_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = MODULE_ROOT.parents[1]


def validator_for(schema_path: Path) -> Draft202012Validator:
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=FormatChecker())


def assert_corpus(test: unittest.TestCase, corpus_path: Path) -> int:
    corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
    test.assertEqual("dps.contract-corpus/v1", corpus["corpus_version"])
    validator = validator_for(corpus_path.parent / corpus["schema_file"])
    base = corpus["base_instance"]
    case_ids = [case["id"] for case in corpus["cases"]]
    test.assertEqual(len(case_ids), len(set(case_ids)))
    for case in corpus["cases"]:
        with test.subTest(owner=corpus["owner_module"], case=case["id"]):
            instance = copy.deepcopy(base)
            instance.update(case["patch"])
            errors = list(validator.iter_errors(instance))
            test.assertEqual(case["expected"] == "FAIL", bool(errors))
    return len(case_ids)


def valid_exchange(kind="POLL"):
    native = kind == "NATIVE_RESULT"
    return {
        "schema_version": "1.0",
        "contract_id": "edge.bridge.exchange/v1",
        "producer_module": "zenno-bridge",
        "soul_id": "soul_" + "a" * 64,
        "device_binding_id": "db_" + "b" * 32,
        "platform_account_id": "pa_" + "c" * 32,
        "trace_id": "trace_" + "d" * 32,
        "idempotency_key": "idem_" + "e" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "personal",
        "auth_nonce": "b" * 64,
        "exchange_kind": kind,
        "command_id": "command-contract-0001" if native else None,
        "action_kind": "VERIFY" if native else None,
        "step_kind": "VERIFY_POSTCONDITION" if native else None,
        "selector": "fixture:state" if native else None,
        "text": None,
        "wait_ms": None,
        "expected_postcondition": "fixture-visible" if native else None,
        "native_status": "SUCCESS" if native else None,
        "native_detail": "verified" if native else None,
        "postcondition_verified": True if native else None,
    }


class EdgeBridgeExchangeContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract_root = MODULE_ROOT / "contracts" / "provided"
        cls.validator = validator_for(
            cls.contract_root / "edge.bridge.exchange.v1.schema.json"
        )

    def test_poll_and_native_result_v1_validate(self):
        self.validator.validate(valid_exchange("POLL"))
        self.validator.validate(valid_exchange("NATIVE_RESULT"))

    def test_owned_exchange_corpus_is_authoritative(self):
        count = assert_corpus(
            self, self.contract_root / "edge.bridge.exchange.v1.corpus.json"
        )
        self.assertEqual(9, count)

    def test_consumed_directive_uses_supervisor_owner_corpus(self):
        owner_root = (
            REPOSITORY_ROOT
            / "Modules"
            / "windows-edge-supervisor"
            / "contracts"
            / "provided"
        )
        count = assert_corpus(
            self, owner_root / "edge.bridge.directive.v1.corpus.json"
        )
        self.assertEqual(14, count)

    def test_unknown_major_action_field_and_invalid_identity_fail_closed(self):
        cases = []
        unknown_major = valid_exchange()
        unknown_major["contract_id"] = "edge.bridge.exchange/v2"
        cases.append(unknown_major)
        unknown_action = valid_exchange("NATIVE_RESULT")
        unknown_action["action_kind"] = "COORDINATE_CLICK"
        cases.append(unknown_action)
        unknown_field = valid_exchange()
        unknown_field["arbitrary_code"] = "not-an-interface-field"
        cases.append(unknown_field)
        missing_scope = valid_exchange()
        del missing_scope["trace_id"]
        cases.append(missing_scope)
        bad_binding = valid_exchange()
        bad_binding["device_binding_id"] = "device-without-db-prefix"
        cases.append(bad_binding)
        for invalid in cases:
            with self.assertRaises(ValidationError):
                self.validator.validate(invalid)

    def test_security_patterns_are_exact_and_terminal(self):
        schema = json.loads(
            (self.contract_root / "edge.bridge.exchange.v1.schema.json").read_text(
                encoding="utf-8"
            )
        )
        nonce = schema["properties"]["auth_nonce"]
        self.assertEqual((64, 64), (nonce["minLength"], nonce["maxLength"]))
        self.assertTrue(nonce["pattern"].endswith("$(?![\\s\\S])"))


if __name__ == "__main__":
    unittest.main()
