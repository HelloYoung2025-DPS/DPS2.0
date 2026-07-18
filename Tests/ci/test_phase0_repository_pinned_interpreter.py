#!/usr/bin/env python3
"""Regression guard for the repository-pinned interpreter declaration.

``Modules/legacy-runtime-adapter`` declares its required static suites as
``.venv/bin/python -I <script>`` and its own contract states that a
PATH-selected interpreter cannot satisfy the byte-baseline gate.  Phase0 never
executes a declared path -- it runs ``sys.executable`` -- so the declaration is
only honest if the gate refuses it whenever the running interpreter is not the
repository virtual environment.

The first attempt at that guard compared the *resolved executables*.  That is
fail-open: ``.venv/bin/python`` is a symlink onto the shared base CPython, so
``resolve()`` collapses both sides onto that base and the guard then accepts the
bare PATH interpreter and every sibling venv built on the same base -- including
one carrying a hostile ``sitecustomize`` module, which still executes under
``-I`` and can patch ``hashlib`` inside the verifier process.  These tests pin
the corrected behaviour so that regression cannot return silently.
"""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path
from typing import Any, Dict
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = ROOT / "Tools" / "ci"

_ORIGINAL_IMPORT_PATH = list(sys.path)
try:
    if str(CI_DIRECTORY) not in sys.path:
        sys.path.insert(0, str(CI_DIRECTORY))

    import run_phase0_gate as gate  # noqa: E402
    from phase0 import Phase0Error  # noqa: E402
finally:
    sys.path = _ORIGINAL_IMPORT_PATH


LEGACY_MODULE_ROOT = ROOT / "Modules" / "legacy-runtime-adapter"
PINNED_SUITE_ID = "legacy-runtime-adapter.static"


def _pinned_suite() -> Dict[str, Any]:
    """The real required suite that declares the repository-pinned interpreter."""

    manifest = json.loads((LEGACY_MODULE_ROOT / "module.yaml").read_text(encoding="utf-8-sig"))
    for suite in manifest["tests"]["suites"]:
        if suite.get("id") == PINNED_SUITE_ID:
            return suite
    raise AssertionError("the repository-pinned required suite is missing")


class RepositoryPinnedInterpreterTest(unittest.TestCase):
    def setUp(self) -> None:
        self.suite = _pinned_suite()
        self.assertTrue(
            self.suite["command"].startswith(gate.REPOSITORY_PINNED_PYTHON + " "),
            "this guard only matters while the suite declares the pinned interpreter",
        )

    def _parse_with_prefix(self, prefix: str):
        with mock.patch.object(gate.sys, "prefix", prefix):
            return gate.parse_manifest_suite_command(
                ROOT, LEGACY_MODULE_ROOT, "legacy-runtime-adapter", self.suite
            )

    def _assert_rejected(self, prefix: str, label: str) -> None:
        with self.assertRaises(Phase0Error, msg=label) as caught:
            self._parse_with_prefix(prefix)
        self.assertIn("repository-pinned", str(caught.exception))

    def test_the_venv_launcher_is_a_symlink_onto_a_shared_base_interpreter(self) -> None:
        """Document the trap: resolving the launcher leaves the virtual environment."""

        launcher = ROOT / gate.REPOSITORY_PINNED_PYTHON
        if not launcher.exists():
            self.skipTest("repository virtual environment is not constructed here")
        resolved = launcher.resolve()
        self.assertFalse(
            resolved.is_relative_to((ROOT / gate.REPOSITORY_PINNED_VENV).resolve()),
            "if the launcher ever stops escaping the venv this guard needs rereview",
        )

    def test_repository_venv_is_accepted(self) -> None:
        venv = ROOT / gate.REPOSITORY_PINNED_VENV
        if not venv.is_dir():
            self.skipTest("repository virtual environment is not constructed here")
        plan = self._parse_with_prefix(str(venv))
        self.assertEqual(1, len(plan.invocations))
        self.assertEqual(sys.executable, plan.invocations[0].argv[0])

    def test_sibling_venv_on_the_same_base_interpreter_is_rejected(self) -> None:
        """The hostile-sitecustomize vector: same base CPython, different venv."""

        self._assert_rejected("/private/tmp/some-other-venv", "sibling venv")

    def test_bare_base_interpreter_is_rejected(self) -> None:
        """A PATH-selected interpreter cannot satisfy the legacy byte-baseline gate."""

        self._assert_rejected(str(Path(sys.base_prefix)), "bare base interpreter")

    def test_resolved_launcher_target_is_rejected(self) -> None:
        """Guarding on the resolved executable would accept this; guarding on the venv must not."""

        launcher = ROOT / gate.REPOSITORY_PINNED_PYTHON
        if not launcher.exists():
            self.skipTest("repository virtual environment is not constructed here")
        self._assert_rejected(str(launcher.resolve().parent.parent), "resolved launcher prefix")

    def test_system_interpreter_is_rejected(self) -> None:
        self._assert_rejected("/usr", "system interpreter")


if __name__ == "__main__":
    unittest.main()
