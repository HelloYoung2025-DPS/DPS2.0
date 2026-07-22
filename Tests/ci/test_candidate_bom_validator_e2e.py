"""End-to-end main() happy path for the candidate BOM validator (R0-C).

This drives the real release entry the way scripts/release.sh invokes it:
argparse -> from_deployed_anchor -> validate() -> exit 0 with "result":"PASS",
run as a subprocess (Option A of the W4 integration notes).  The candidate
material is the pinned W4 fixture set under
Tests/ci/fixtures/r0c_native_stop_trust_e2e/: a deterministic synthetic
integration commit (real ancestors cannot satisfy the validator's exact
manifest shapes), a signed candidate BOM + previous stable BOM, and the
on-disk bundle those BOMs reference.

Trust-anchor substitution, by design: the deployed anchor's four classic keys
were generated without retaining their private halves, so no fixture can ever
be signed under them.  The synthetic policy assembled at test time therefore
carries the test-suite's existing public key material for the classic roles,
while the native-stop-trust link under test is the GENUINE deployed one: the
owner's signer group and key entry are read from the live patched policy
(governance/policies/deployed-release-trust-policy.v1.json) at test time and
never hardcoded here.

VISIBLE RED until the owner-signed receipt signature lands: while
fixtures/r0c_native_stop_trust_e2e/receipt-signature.json holds the committed
placeholder, the test fails hard with "awaiting owner signature".  It goes
green the moment the owner-signed signature object replaces the placeholder;
nothing else needs to change.
"""

import calendar
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve(strict=True).parents[2]
FIXTURES = ROOT / "Tests" / "ci" / "fixtures" / "r0c_native_stop_trust_e2e"
DEPLOYED_POLICY_PATH = (
    ROOT / "governance" / "policies" / "deployed-release-trust-policy.v1.json"
)
VALIDATOR_PATH = ROOT / "Tools" / "ci" / "candidate_bom_validator.py"
SUITE_PATH = ROOT / "Tests" / "ci" / "test_candidate_bom_validator.py"

# Deterministic commit identity, pinned so the synthetic integration commit
# sha reproduces exactly (a git commit is a pure function of tree, parents,
# author/committer lines, and message).
COMMIT_EPOCH = calendar.timegm((2026, 7, 14, 0, 0, 0))
COMMIT_DATE = f"@{COMMIT_EPOCH} +0000"
COMMIT_NAME = "DPS Release Fixture"
COMMIT_EMAIL = "release-fixture@dps.invalid"
COMMIT_MESSAGE = "release BOM e2e fixture (deterministic synthetic integration commit)\n"

OWNER_SIGNATURE_PLACEHOLDER_MESSAGE = (
    "awaiting owner signature — see PR #4 description: "
    "Tests/ci/fixtures/r0c_native_stop_trust_e2e/receipt-signature.json must "
    'be the owner-signed {"algorithm":"rsa-pss-sha256","key_id":'
    '"native-stop-trust-owner-key-1","value":"<base64>"} object, not the '
    "committed placeholder"
)


def _load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


SUBJECT = _load_module("_dps_r0c_e2e_cbv_subject", VALIDATOR_PATH)
# The classic fixture roles reuse the existing suite's pinned public key
# material (KEYS) instead of re-declaring it here.
BOM_SUITE = _load_module("_dps_r0c_e2e_bom_suite", SUITE_PATH)
canonical_bytes = SUBJECT.canonical_bytes
sha256_bytes = SUBJECT.sha256_bytes


class CandidateBomValidatorMainEndToEndTests(unittest.TestCase):
    def test_main_happy_path_with_owner_signed_receipt_passes(self) -> None:
        signature = self._owner_signature()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            repo = work / "repo"
            bundle = work / "bundle"
            shutil.copytree(FIXTURES / "repo-content", repo)
            shutil.copytree(FIXTURES / "bundle", bundle)
            bom_path = work / "bom.json"
            previous_path = work / "previous-bom.json"
            shutil.copyfile(FIXTURES / "bom.json", bom_path)
            shutil.copyfile(FIXTURES / "previous-bom.json", previous_path)
            commit = self._make_integration_commit(repo)
            policy = self._synthetic_anchor_policy()
            policy_path = (
                repo / "governance" / "policies" / "deployed-release-trust-policy.v1.json"
            )
            policy_path.parent.mkdir(parents=True, exist_ok=True)
            policy_path.write_bytes(canonical_bytes(policy) + b"\n")
            patched_validator = work / "candidate_bom_validator.py"
            self._patch_deployed_digest_constant(
                patched_validator, sha256_bytes(canonical_bytes(policy))
            )
            schema_sha = sha256_bytes(
                (repo / "governance" / "schemas" / "release-bom.schema.json").read_bytes()
            )
            receipt_path = work / "receipt.json"
            payload = json.loads((FIXTURES / "receipt-payload.json").read_bytes())
            receipt = dict(payload)
            receipt["signature"] = signature
            receipt_bytes = canonical_bytes(receipt) + b"\n"
            receipt_path.write_bytes(receipt_bytes)
            result = subprocess.run(
                [
                    sys.executable, str(patched_validator),
                    "--repo-root", str(repo),
                    "--bundle-root", str(bundle),
                    "--bom", str(bom_path),
                    "--previous-bom", str(previous_path),
                    "--native-stop-trust-receipt", str(receipt_path),
                    "--schema-sha256", schema_sha,
                    # The fixture authority windows close on 2026-07-31, so the
                    # happy path pins the validation instant deterministically
                    # inside them (the fixture created_at) instead of depending
                    # on the wall clock.
                    "--validation-time", "2026-07-14T00:00:00Z",
                ],
                capture_output=True, text=True, timeout=120,
            )
        self.assertEqual(
            0, result.returncode,
            f"validator exit {result.returncode}\nstdout: {result.stdout}\nstderr: {result.stderr}",
        )
        report = json.loads(result.stdout)
        self.assertEqual("PASS", report["result"])
        self.assertEqual("dps-deployed-release-anchor-v1", report["trust_policy_id"])
        self.assertEqual(commit, report["integration_commit"])
        self.assertEqual(self._owner_signer_identity(), report["native_stop_trust_signer"])
        self.assertEqual("INTEGRATION_VERIFIED", report["verification_ceiling"])
        self.assertEqual(
            sha256_bytes(receipt_bytes), report["native_stop_trust_receipt_sha256"]
        )

    def _owner_key_entry(self):
        # Read the live patched deployed anchor; the owner native-stop-trust
        # key entry is never hardcoded in this test.
        policy = json.loads(DEPLOYED_POLICY_PATH.read_bytes())
        entries = [
            key for key in policy["keys"] if "native-stop-trust" in key["purposes"]
        ]
        self.assertEqual(
            1, len(entries),
            "deployed anchor must pin exactly one native-stop-trust key entry",
        )
        return policy, entries[0]

    def _owner_signer_identity(self):
        policy, owner_entry = self._owner_key_entry()
        self.assertIn(
            owner_entry["identity"], policy["native_stop_trust_signer_identities"]
        )
        return owner_entry["identity"]

    def _owner_signature(self):
        _, owner_entry = self._owner_key_entry()
        signature_path = FIXTURES / "receipt-signature.json"
        signature = None
        if signature_path.is_file():
            try:
                signature = json.loads(signature_path.read_bytes())
            except (UnicodeDecodeError, json.JSONDecodeError):
                signature = None
        if (
            not isinstance(signature, dict)
            or set(signature) != {"algorithm", "key_id", "value"}
            or signature["algorithm"] != "rsa-pss-sha256"
            or signature["key_id"] != owner_entry["key_id"]
            or not isinstance(signature["value"], str)
            or not signature["value"]
        ):
            self.fail(OWNER_SIGNATURE_PLACEHOLDER_MESSAGE)
        return signature

    def _synthetic_anchor_policy(self):
        # Deployed anchor shape: its schema version, policy id, the three
        # deployed required gates, allow_bootstrap, and -- the genuine link
        # under test -- the owner's native-stop-trust signer group and key
        # entry, all read from the live patched policy at test time.  The
        # classic roles are carried by the test-suite KEYS because the
        # deployed anchor's classic private halves were discarded by design.
        policy, owner_entry = self._owner_key_entry()
        classic = {
            "controller-key": ("release-controller", ["artifact", "bom"]),
            "evidence-key": ("evidence-issuer", ["evidence"]),
            "approval-key": ("human-approver", ["approval"]),
        }
        return {
            "schema_version": policy["schema_version"],
            "policy_id": policy["policy_id"],
            "required_gates": policy["required_gates"],
            "implementer_identities": ["module-builder"],
            "evidence_issuer_identities": ["evidence-issuer"],
            "release_controller_identities": ["release-controller"],
            "release_approver_identities": ["human-approver"],
            "native_stop_trust_signer_identities": (
                policy["native_stop_trust_signer_identities"]
            ),
            "allow_bootstrap": policy["allow_bootstrap"],
            "keys": [
                {
                    "key_id": key_id,
                    "identity": identity,
                    "algorithm": "rsa-pss-sha256",
                    "modulus_hex": format(BOM_SUITE.KEYS[key_id][0], "x"),
                    "exponent": 65537,
                    "purposes": purposes,
                }
                for key_id, (identity, purposes) in classic.items()
            ]
            + [owner_entry],
        }

    def _patch_deployed_digest_constant(self, target, policy_sha):
        # from_deployed_anchor hard-binds the code-bound digest, so the
        # subprocess runs a copy of the real validator with only the constant
        # re-anchored to the synthetic policy; every other byte is the real
        # operational entry.
        raw = VALIDATOR_PATH.read_bytes()
        old = (
            f'_DEPLOYED_TRUST_POLICY_SHA256 = '
            f'"{SUBJECT._DEPLOYED_TRUST_POLICY_SHA256}"'
        ).encode()
        new = f'_DEPLOYED_TRUST_POLICY_SHA256 = "{policy_sha}"'.encode()
        self.assertEqual(1, raw.count(old), "validator digest constant not found exactly once")
        target.write_bytes(raw.replace(old, new))

    def _make_integration_commit(self, repo):
        def git(*args, env_extra=None):
            env = {
                "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
                "HOME": str(repo),  # isolate from any global gitconfig
                "GIT_CONFIG_NOSYSTEM": "1",
                "LC_ALL": "C",
            }
            if env_extra:
                env.update(env_extra)
            completed = subprocess.run(
                ["git", "-C", str(repo), *args], check=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env,
            )
            return completed.stdout.decode().strip()

        git("init", "-q")
        git("config", "user.email", COMMIT_EMAIL)
        git("config", "user.name", COMMIT_NAME)
        git("add", ".")
        git("commit", "-q", "-m", COMMIT_MESSAGE, env_extra={
            "GIT_AUTHOR_NAME": COMMIT_NAME, "GIT_AUTHOR_EMAIL": COMMIT_EMAIL,
            "GIT_COMMITTER_NAME": COMMIT_NAME, "GIT_COMMITTER_EMAIL": COMMIT_EMAIL,
            "GIT_AUTHOR_DATE": COMMIT_DATE, "GIT_COMMITTER_DATE": COMMIT_DATE,
        })
        commit = git("rev-parse", "HEAD")
        expected = (FIXTURES / "fixture-commit.txt").read_text(encoding="utf-8").strip()
        self.assertEqual(
            expected, commit,
            "synthetic integration commit drifted from the pinned fixture sha",
        )
        return commit


if __name__ == "__main__":
    unittest.main()
