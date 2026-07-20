"""Dual-pin: the C# activation authority's Release BOM top-level field set
must stay identical to the python release gate's _BOM_FIELDS.

The runtime activation authority
(Modules/control-plane-host/src/Dps.ControlPlaneHost/ActiveReleaseBindingAuthority.cs,
ReleaseBomWireContract.RequiredTopLevelFields) enforces an activation-safety
subset of signed Release BOM validation; its very first check is exact
top-level field-set equality. That set is authored twice — python
(Tools/ci/candidate_bom_validator.py::_BOM_FIELDS) and C# — so this test
extracts both literals and asserts set equality, the same double-pin
discipline as MODULE_MANIFEST_SCHEMA_SHA256. Extraction is regex over string
literals inside each block, deliberately tolerant of formatting.
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
PYTHON_SOURCE = REPO_ROOT / "Tools/ci/candidate_bom_validator.py"
CSHARP_SOURCE = (
    REPO_ROOT
    / "Modules/control-plane-host/src/Dps.ControlPlaneHost/ActiveReleaseBindingAuthority.cs"
)


def _extract_block(text: str, marker: str, source: str) -> str:
    """Return the first balanced {...} block after marker (format-tolerant)."""
    anchor = text.find(marker)
    if anchor < 0:
        raise AssertionError(f"{source}: marker {marker!r} not found")
    start = text.find("{", anchor)
    if start < 0:
        raise AssertionError(f"{source}: no block opens after {marker!r}")
    depth = 0
    for index in range(start, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return text[start:index + 1]
    raise AssertionError(f"{source}: unbalanced braces after {marker!r}")


def _python_bom_fields() -> frozenset[str]:
    block = _extract_block(
        PYTHON_SOURCE.read_text(encoding="utf-8"), "_BOM_FIELDS =", "candidate_bom_validator.py"
    )
    return frozenset(re.findall(r'"([a-z_0-9]+)"', block))


def _csharp_bom_fields() -> frozenset[str]:
    block = _extract_block(
        CSHARP_SOURCE.read_text(encoding="utf-8"),
        "RequiredTopLevelFields",
        "ActiveReleaseBindingAuthority.cs",
    )
    return frozenset(re.findall(r'"([a-z_0-9]+)"', block))


class ReleaseBomFieldSetDualPinTests(unittest.TestCase):
    def test_field_sets_are_identical(self) -> None:
        python_fields = _python_bom_fields()
        csharp_fields = _csharp_bom_fields()
        self.assertGreaterEqual(len(python_fields), 20, "python extraction looks broken")
        self.assertEqual(
            python_fields,
            csharp_fields,
            "Release BOM top-level field sets diverged between "
            "Tools/ci/candidate_bom_validator.py::_BOM_FIELDS and "
            "ReleaseBomWireContract.RequiredTopLevelFields",
        )

    def test_signature_field_is_pinned_on_both_sides(self) -> None:
        self.assertIn("signature", _python_bom_fields())
        self.assertIn("signature", _csharp_bom_fields())


if __name__ == "__main__":
    raise SystemExit(unittest.main())
