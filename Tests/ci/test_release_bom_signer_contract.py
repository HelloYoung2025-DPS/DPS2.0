from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import json
import re
import unittest
from pathlib import Path
from typing import Any


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
AUTH_SPEC_PATH = REPOSITORY_ROOT / "governance/schemas/release-bom.v1.auth.json"
CORPUS_PATH = (
    REPOSITORY_ROOT
    / "governance/verification/release-bom.signature.v1.corpus.json"
)
DEPLOYED_TRUST_POLICY_PATH = (
    REPOSITORY_ROOT
    / "governance/policies/deployed-release-trust-policy.v1.json"
)

EXPECTED_NEGATIVE_IDS = {
    "base64-padding-bit-alias",
    "base64-trailing-whitespace",
    "short-i2osp",
    "representative-equals-modulus",
    "modulus-below-256-octets",
    "wrong-public-exponent",
    "purpose-reuse",
    "wrong-mgf1-hash",
    "wrong-salt-length",
    "wrong-trailer-field",
    "altered-payload",
    "altered-domain",
    "final-wire-trailing-lf",
    "final-wire-member-reorder",
    "identical-request-new-final-wire",
    "stable-twin-copied-candidate-signature",
    "stable-twin-top-level-drift",
}


def _reject_duplicate_members(
    pairs: list[tuple[str, Any]],
) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for name, item in pairs:
        if name in value:
            raise ValueError(f"duplicate JSON member: {name}")
        value[name] = item
    return value


def _load_strict(path: Path) -> dict[str, Any]:
    value = json.loads(
        path.read_text(encoding="utf-8"),
        object_pairs_hook=_reject_duplicate_members,
    )
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain one JSON object")
    return value


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def _decode_canonical_base64(value: str) -> bytes | None:
    try:
        decoded = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError):
        return None
    if base64.b64encode(decoded).decode("ascii") != value:
        return None
    return decoded


def _mgf1(seed: bytes, length: int, hash_name: str) -> bytes:
    output = bytearray()
    counter = 0
    while len(output) < length:
        digest = hashlib.new(hash_name)
        digest.update(seed)
        digest.update(counter.to_bytes(4, "big"))
        output.extend(digest.digest())
        counter += 1
    return bytes(output[:length])


def _verify_rsa_pss_sha256(
    message: bytes,
    signature: bytes,
    modulus: int,
    exponent: int,
    *,
    mgf1_hash: str = "sha256",
    salt_length: int = 32,
    trailer: int = 0xBC,
) -> bool:
    if exponent != 65537 or modulus <= 0:
        return False
    modulus_octets = (modulus.bit_length() + 7) // 8
    if len(signature) != modulus_octets:
        return False
    representative = int.from_bytes(signature, "big")
    if representative >= modulus:
        return False

    encoded_bits = modulus.bit_length() - 1
    encoded_length = (encoded_bits + 7) // 8
    encoded = pow(representative, exponent, modulus).to_bytes(
        encoded_length, "big"
    )
    digest_length = hashlib.sha256().digest_size
    if (
        encoded_length < digest_length + salt_length + 2
        or encoded[-1] != trailer
    ):
        return False

    masked_db = encoded[: encoded_length - digest_length - 1]
    encoded_hash = encoded[encoded_length - digest_length - 1 : -1]
    unused_bits = 8 * encoded_length - encoded_bits
    if unused_bits and masked_db[0] >> (8 - unused_bits):
        return False
    data_mask = _mgf1(encoded_hash, len(masked_db), mgf1_hash)
    data_block = bytearray(
        left ^ right for left, right in zip(masked_db, data_mask)
    )
    if unused_bits:
        data_block[0] &= 0xFF >> unused_bits
    padding_length = encoded_length - digest_length - salt_length - 2
    if (
        padding_length < 0
        or data_block[:padding_length] != b"\x00" * padding_length
        or data_block[padding_length] != 0x01
    ):
        return False
    salt = bytes(data_block[-salt_length:]) if salt_length else b""
    expected = hashlib.sha256(
        b"\x00" * 8 + hashlib.sha256(message).digest() + salt
    ).digest()
    return hmac.compare_digest(encoded_hash, expected)


def _valid_bom_key_profile(key: dict[str, Any]) -> bool:
    modulus_hex = key.get("modulus_hex")
    if (
        key.get("algorithm") != "rsa-pss-sha256"
        or key.get("exponent") != 65537
        or key.get("purposes") != ["bom"]
        or not isinstance(modulus_hex, str)
        or re.fullmatch(r"[0-9a-f]+", modulus_hex) is None
        or modulus_hex.startswith("0")
    ):
        return False
    return (len(modulus_hex) + 1) // 2 >= 256


class ReleaseBomExternalSignerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.auth_spec_bytes = AUTH_SPEC_PATH.read_bytes()
        cls.auth_spec = _load_strict(AUTH_SPEC_PATH)
        cls.corpus_bytes = CORPUS_PATH.read_bytes()
        cls.corpus = _load_strict(CORPUS_PATH)
        cls.policy = _load_strict(DEPLOYED_TRUST_POLICY_PATH)

    def test_01_auth_spec_is_exactly_the_r0c_deployed_rsa_pss_profile(self) -> None:
        self.assertEqual(
            set(self.auth_spec),
            {
                "schema_version",
                "profile_id",
                "release_bom_contract",
                "scope",
                "private_key_boundary",
                "key_profile",
                "pss_profile",
                "signing_input",
                "lifecycle_pair",
                "canonical_wire",
                "rsa_representative",
                "issuance_output",
                "canonicalization_corpora",
            },
        )
        self.assertEqual(
            self.auth_spec["schema_version"],
            "dps.release-bom-auth-spec/v1",
        )
        self.assertEqual(
            self.auth_spec["profile_id"],
            "r0c-deployed-rsa-pss-sha256/v1",
        )
        self.assertEqual(
            self.auth_spec["release_bom_contract"],
            {
                "schema_version": "dps.release-bom/v1",
                "schema_path": "governance/schemas/release-bom.schema.json",
                "issued_statuses": ["SIGNED", "STABLE"],
            },
        )
        scope = self.auth_spec["scope"]
        self.assertTrue(scope["does_not_redefine_release_bom_schema"])
        self.assertTrue(scope["does_not_change_signature_algorithm_enum"])
        non_scope = scope["explicit_non_scope"]
        self.assertEqual(
            non_scope,
            [
                {
                    "surface": "Tools/verification external F6-F9 verification",
                    "contract_id": "dps.external-verification-bom/v1",
                    "signature_algorithm": "ecdsa-p256-sha256",
                    "domain_separator_utf8": "dps-external-verification-bom/v1\n",
                    "statement": (
                        "This RSA-PSS profile is cryptographically disjoint from "
                        "the dormant F6-F9 ECDSA Release BOM protocol; neither "
                        "contract accepts the other's identifier or signing domain."
                    ),
                }
            ],
        )
        self.assertNotEqual(
            non_scope[0]["contract_id"],
            self.auth_spec["release_bom_contract"]["schema_version"],
        )
        self.assertNotEqual(
            non_scope[0]["domain_separator_utf8"],
            self.auth_spec["signing_input"]["domain_separator_utf8"],
        )

    def test_02_private_key_and_exact_crypto_boundaries_are_normative(self) -> None:
        boundary = self.auth_spec["private_key_boundary"]
        self.assertEqual(
            boundary["exclusive_owner"],
            "repository-external Owner/KMS release signer",
        )
        self.assertEqual(
            set(boundary["forbidden_private_key_recipients"]),
            {
                "repository",
                "model",
                "candidate code",
                "Tools/ci process",
                "Control Plane runtime process",
            },
        )
        self.assertEqual(
            boundary["repository_material"],
            "public verification facts and conformance vectors only",
        )

        key_profile = self.auth_spec["key_profile"]
        self.assertEqual(key_profile["algorithm"], "rsa-pss-sha256")
        self.assertEqual(key_profile["selected_key_cardinality_per_issuance"], 1)
        self.assertEqual(key_profile["purposes_exact"], ["bom"])
        self.assertEqual(key_profile["minimum_unsigned_modulus_octets"], 256)
        self.assertEqual(key_profile["public_exponent"], 65537)

        pss = self.auth_spec["pss_profile"]
        self.assertEqual(
            pss,
            {
                "message_hash": "SHA-256",
                "mask_generation_function": "MGF1",
                "mgf1_hash": "SHA-256",
                "salt_length_octets": 32,
                "trailer_field_hex": "bc",
            },
        )
        representative = self.auth_spec["rsa_representative"]
        self.assertEqual(
            representative["signature_octets"],
            "exact I2OSP(s, k)",
        )
        self.assertEqual(representative["representative_range"], "0 <= s < n")

    def test_03_canonical_final_wire_is_persisted_and_reused(self) -> None:
        signing = self.auth_spec["signing_input"]
        self.assertEqual(signing["domain_separator_utf8"], "dps-release-bom/v1\n")
        self.assertEqual(
            signing["payload_definition"],
            "for each issued lifecycle wire, all top-level Release BOM members except the single signature member",
        )
        lifecycle_pair = self.auth_spec["lifecycle_pair"]
        self.assertEqual(lifecycle_pair["candidate_wire"]["status"], "SIGNED")
        self.assertEqual(lifecycle_pair["stable_twin_wire"]["status"], "STABLE")
        self.assertEqual(
            lifecycle_pair["top_level_differences_exact"],
            ["status", "signature"],
        )
        self.assertIn("independently RSA-PSS signed", lifecycle_pair["signature_rule"])
        self.assertIn("exact SIGNED", lifecycle_pair["issuance_rule"])
        self.assertIn("exact STABLE", lifecycle_pair["issuance_rule"])
        self.assertIn(
            "exact persisted STABLE",
            lifecycle_pair["next_candidate_reference"][
                "previous_stable_bom_sha256"
            ],
        )
        wire = self.auth_spec["canonical_wire"]
        self.assertEqual(wire["encoding"], "UTF-8")
        self.assertEqual(wire["maximum_final_wire_utf8_bytes"], 4 * 1024 * 1024)
        self.assertEqual(wire["object_member_order"], "ascending Unicode scalar value")
        self.assertEqual(wire["duplicate_members"], "reject")
        self.assertEqual(wire["trailing_bytes"], "reject")
        self.assertEqual(
            wire["signature_object_exact_members"],
            ["algorithm", "key_id", "value"],
        )
        self.assertIn("canonical padded Base64", wire["signature_value_encoding"])

        output = self.auth_spec["issuance_output"]
        self.assertIn(
            "exact canonical SIGNED final wire bytes",
            output["persist_before_return"],
        )
        self.assertIn(
            "exact canonical STABLE lifecycle-twin final wire bytes",
            output["persist_before_return"],
        )
        self.assertIn(
            "both byte-identical persisted final wires",
            output["identical_request_behavior"],
        )
        self.assertIn("do not perform either", output["identical_request_behavior"])
        self.assertIn(
            "exact persisted SIGNED or STABLE final wire",
            output["release_bom_sha256_definition"],
        )
        self.assertIn("reuse the applicable exact persisted", output["consumer_rule"])

    def test_04_auth_spec_and_canonicalization_corpora_are_hash_bound(self) -> None:
        self.assertEqual(
            self.corpus["auth_spec"],
            {
                "path": "governance/schemas/release-bom.v1.auth.json",
                "sha256": _sha256(self.auth_spec_bytes),
            },
        )
        for artifact in self.auth_spec["canonicalization_corpora"]:
            artifact_path = REPOSITORY_ROOT / artifact["path"]
            self.assertTrue(artifact_path.is_file())
            self.assertEqual(_sha256(artifact_path.read_bytes()), artifact["sha256"])

    def test_05_deployed_policy_exposes_one_public_bom_key_in_profile(self) -> None:
        bom_keys = [
            key for key in self.policy["keys"] if "bom" in key.get("purposes", [])
        ]
        self.assertEqual(len(bom_keys), 1)
        self.assertTrue(_valid_bom_key_profile(bom_keys[0]))
        self.assertIn(
            bom_keys[0]["identity"],
            self.policy["release_controller_identities"],
        )
        forbidden_private_fields = {
            "private_key",
            "private_key_pem",
            "private_exponent",
            "prime_p",
            "prime_q",
            "d",
            "p",
            "q",
        }
        self.assertFalse(forbidden_private_fields.intersection(bom_keys[0]))

    def test_06_public_positive_vectors_prove_signed_and_stable_pair(self) -> None:
        self.assertEqual(
            self.corpus["schema_version"],
            "dps.release-bom-signature-corpus/v1",
        )
        self.assertEqual(self.corpus["profile_id"], self.auth_spec["profile_id"])
        key = self.corpus["key"]
        self.assertTrue(_valid_bom_key_profile(key))
        self.assertEqual(key["unsigned_modulus_octets"], 256)
        self.assertTrue(key["private_key_material"].startswith("absent;"))

        positive = self.corpus["positive"]
        self.assertEqual(positive["expected"], "PASS")
        domain = positive["domain_separator_utf8"].encode("utf-8")
        payload = positive["canonical_payload_utf8"].encode("utf-8")
        self.assertEqual(domain, b"dps-release-bom/v1\n")
        payload_object = json.loads(
            payload.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_members,
        )
        self.assertEqual(payload, _canonical_json(payload_object))
        self.assertEqual(_sha256(payload), positive["payload_sha256"])
        message = domain + payload
        self.assertEqual(_sha256(message), positive["message_sha256"])

        signature = _decode_canonical_base64(positive["signature_base64"])
        self.assertIsNotNone(signature)
        assert signature is not None
        self.assertEqual(len(signature), positive["signature_octets"])
        self.assertEqual(_sha256(signature), positive["signature_sha256"])
        modulus = int(key["modulus_hex"], 16)
        self.assertLess(int.from_bytes(signature, "big"), modulus)
        self.assertTrue(
            _verify_rsa_pss_sha256(
                message,
                signature,
                modulus,
                key["exponent"],
            )
        )

        final_wire = positive["final_wire_utf8"].encode("utf-8")
        final_object = json.loads(
            final_wire.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_members,
        )
        self.assertEqual(final_wire, _canonical_json(final_object))
        self.assertEqual(final_object["signature"]["value"], positive["signature_base64"])
        self.assertEqual(final_object["signature"]["key_id"], key["key_id"])
        self.assertEqual(final_object["signature"]["algorithm"], key["algorithm"])
        payload_from_final = dict(final_object)
        payload_from_final.pop("signature")
        self.assertEqual(_canonical_json(payload_from_final), payload)
        self.assertEqual(_sha256(final_wire), positive["final_wire_sha256"])
        self.assertEqual(
            positive["identical_retry_final_wire_sha256"],
            positive["final_wire_sha256"],
        )

        stable = self.corpus["stable_twin"]
        self.assertEqual(stable["expected"], "PASS")
        stable_domain = stable["domain_separator_utf8"].encode("utf-8")
        stable_payload = stable["canonical_payload_utf8"].encode("utf-8")
        self.assertEqual(stable_domain, domain)
        stable_payload_object = json.loads(
            stable_payload.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_members,
        )
        self.assertEqual(stable_payload, _canonical_json(stable_payload_object))
        self.assertEqual(_sha256(stable_payload), stable["payload_sha256"])
        stable_message = stable_domain + stable_payload
        self.assertEqual(_sha256(stable_message), stable["message_sha256"])

        stable_signature = _decode_canonical_base64(stable["signature_base64"])
        self.assertIsNotNone(stable_signature)
        assert stable_signature is not None
        self.assertEqual(len(stable_signature), stable["signature_octets"])
        self.assertEqual(_sha256(stable_signature), stable["signature_sha256"])
        self.assertLess(int.from_bytes(stable_signature, "big"), modulus)
        self.assertTrue(
            _verify_rsa_pss_sha256(
                stable_message,
                stable_signature,
                modulus,
                key["exponent"],
            )
        )
        self.assertFalse(
            _verify_rsa_pss_sha256(
                stable_message,
                signature,
                modulus,
                key["exponent"],
            )
        )
        self.assertFalse(
            _verify_rsa_pss_sha256(
                message,
                stable_signature,
                modulus,
                key["exponent"],
            )
        )

        stable_wire = stable["final_wire_utf8"].encode("utf-8")
        stable_object = json.loads(
            stable_wire.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_members,
        )
        self.assertEqual(stable_wire, _canonical_json(stable_object))
        self.assertEqual(
            stable_object["signature"]["value"],
            stable["signature_base64"],
        )
        stable_payload_from_final = dict(stable_object)
        stable_payload_from_final.pop("signature")
        self.assertEqual(_canonical_json(stable_payload_from_final), stable_payload)
        self.assertEqual(_sha256(stable_wire), stable["final_wire_sha256"])
        self.assertEqual(
            stable["identical_retry_final_wire_sha256"],
            stable["final_wire_sha256"],
        )

        pair = self.corpus["pair_invariant"]
        differences = {
            name
            for name in set(final_object).union(stable_object)
            if final_object.get(name) != stable_object.get(name)
        }
        self.assertEqual(differences, {"status", "signature"})
        self.assertEqual(
            pair["top_level_differences_exact"],
            ["status", "signature"],
        )
        self.assertEqual(final_object["status"], pair["candidate_status"])
        self.assertEqual(stable_object["status"], pair["stable_twin_status"])
        self.assertEqual(
            pair["candidate_final_wire_sha256"],
            positive["final_wire_sha256"],
        )
        self.assertEqual(
            pair["stable_twin_final_wire_sha256"],
            stable["final_wire_sha256"],
        )
        self.assertEqual(
            pair["next_candidate_previous_stable_bom"],
            stable_object["bom_id"],
        )
        self.assertEqual(
            pair["next_candidate_previous_stable_bom_sha256"],
            stable["final_wire_sha256"],
        )
        self.assertEqual(pair["expected"], "PASS")

    def test_07_every_adversarial_vector_fails_its_bound_invariant(self) -> None:
        cases = {case["id"]: case for case in self.corpus["negative_cases"]}
        self.assertEqual(set(cases), EXPECTED_NEGATIVE_IDS)
        self.assertTrue(all(case["expected"] == "FAIL" for case in cases.values()))

        key = self.corpus["key"]
        positive = self.corpus["positive"]
        signature_base64 = positive["signature_base64"]
        signature = _decode_canonical_base64(signature_base64)
        assert signature is not None
        modulus = int(key["modulus_hex"], 16)
        modulus_octets = (modulus.bit_length() + 7) // 8
        message = (
            positive["domain_separator_utf8"]
            + positive["canonical_payload_utf8"]
        ).encode("utf-8")

        padding_alias = signature_base64[:-3] + "R=="
        self.assertEqual(base64.b64decode(padding_alias, validate=True), signature)
        self.assertIsNone(_decode_canonical_base64(padding_alias))
        self.assertIsNone(_decode_canonical_base64(signature_base64 + "\n"))
        self.assertFalse(
            _verify_rsa_pss_sha256(
                message, signature[1:], modulus, key["exponent"]
            )
        )
        modulus_as_signature = modulus.to_bytes(modulus_octets, "big")
        self.assertFalse(
            _verify_rsa_pss_sha256(
                message, modulus_as_signature, modulus, key["exponent"]
            )
        )

        short_key = dict(key)
        short_key["modulus_hex"] = key["modulus_hex"][2:]
        self.assertFalse(_valid_bom_key_profile(short_key))
        wrong_exponent = dict(key)
        wrong_exponent["exponent"] = 3
        self.assertFalse(_valid_bom_key_profile(wrong_exponent))
        reused_key = dict(key)
        reused_key["purposes"] = ["bom", "artifact"]
        self.assertFalse(_valid_bom_key_profile(reused_key))

        self.assertFalse(
            _verify_rsa_pss_sha256(
                message,
                signature,
                modulus,
                key["exponent"],
                mgf1_hash="sha384",
            )
        )
        self.assertFalse(
            _verify_rsa_pss_sha256(
                message,
                signature,
                modulus,
                key["exponent"],
                salt_length=20,
            )
        )
        self.assertFalse(
            _verify_rsa_pss_sha256(
                message,
                signature,
                modulus,
                key["exponent"],
                trailer=0xCC,
            )
        )

        altered_payload = positive["canonical_payload_utf8"].replace(
            '"SIGNED"', '"REVOKED"'
        ).encode("utf-8")
        self.assertFalse(
            _verify_rsa_pss_sha256(
                positive["domain_separator_utf8"].encode("utf-8")
                + altered_payload,
                signature,
                modulus,
                key["exponent"],
            )
        )
        self.assertFalse(
            _verify_rsa_pss_sha256(
                positive["domain_separator_utf8"].rstrip("\n").encode("utf-8")
                + positive["canonical_payload_utf8"].encode("utf-8"),
                signature,
                modulus,
                key["exponent"],
            )
        )

        final_wire = positive["final_wire_utf8"].encode("utf-8")
        final_object = json.loads(
            final_wire.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_members,
        )
        self.assertNotEqual(final_wire + b"\n", _canonical_json(final_object))
        reordered_wire = (
            '{"bom_id":"contract-vector-1",'
            '"schema_version":"dps.release-bom/v1",'
            '"status":"SIGNED",'
            '"signature":'
            + json.dumps(
                final_object["signature"],
                separators=(",", ":"),
                ensure_ascii=False,
            )
            + "}"
        ).encode("utf-8")
        self.assertEqual(
            json.loads(reordered_wire.decode("utf-8")),
            final_object,
        )
        self.assertNotEqual(reordered_wire, _canonical_json(final_object))

        changed_retry = bytearray(final_wire)
        signature_start = final_wire.index(signature_base64.encode("ascii"))
        changed_retry[signature_start] = (
            ord("s") if changed_retry[signature_start] != ord("s") else ord("t")
        )
        self.assertNotEqual(bytes(changed_retry), final_wire)
        self.assertNotEqual(_sha256(bytes(changed_retry)), _sha256(final_wire))

        stable = self.corpus["stable_twin"]
        stable_message = (
            stable["domain_separator_utf8"]
            + stable["canonical_payload_utf8"]
        ).encode("utf-8")
        self.assertFalse(
            _verify_rsa_pss_sha256(
                stable_message,
                signature,
                modulus,
                key["exponent"],
            )
        )
        stable_object = json.loads(
            stable["final_wire_utf8"],
            object_pairs_hook=_reject_duplicate_members,
        )
        drifted_stable = dict(stable_object)
        drifted_stable["bom_id"] = "contract-vector-drift"
        pair_differences = {
            name
            for name in set(final_object).union(drifted_stable)
            if final_object.get(name) != drifted_stable.get(name)
        }
        self.assertNotEqual(pair_differences, {"status", "signature"})
        self.assertIn("bom_id", pair_differences)

    def test_08_repository_contract_artifacts_contain_no_private_key(self) -> None:
        combined = self.auth_spec_bytes + b"\n" + self.corpus_bytes
        for marker in (
            b"-----BEGIN PRIVATE KEY-----",
            b"-----BEGIN RSA PRIVATE KEY-----",
            b'"private_exponent"',
            b'"prime_p"',
            b'"prime_q"',
        ):
            self.assertNotIn(marker, combined)


if __name__ == "__main__":
    unittest.main()
