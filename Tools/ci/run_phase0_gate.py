#!/usr/bin/env python3
"""The only supported DPS Phase 0 gate entry point.

Every required check is represented in one evidence bundle.  A required check
passes only with the literal status ``PASS``; skip, partial, empty, timeout,
missing evidence, and infrastructure errors all make this process exit nonzero.

One narrow exception exists at the overall level: required FAILs whose failure
fingerprints exactly match ``governance/baseline-red-ledger.json`` yield the
overall status ``PASS_WITH_REGISTERED_BASELINE`` (exit 0, never the literal
``PASS``, and never releasable: ``--require-literal-pass``, pinned into
``scripts/release.sh``, re-tightens exit 0 to the literal ``PASS``).  Any
deviation from the registered fingerprints -- an unregistered red, a changed,
moved, shrunk, or grown fingerprint, a stale entry, ledger expansion or
removal relative to ``--base``, or a missing authoritative baseline commit
while the ledger is load-bearing -- fails closed.  A registered unittest
failure is bound to a normalized digest of its traceback, so the same test
failing for a new reason is a new red, and a run that introduces ledger
entries not already present at ``--base`` is treated as expansion and can
never clear itself.

Known boundaries, stated so this is not mistaken for more than it is:

* Fingerprint protection only applies to runs that actually execute this gate
  (the CI path).  If ``run_phase0_gate.py`` itself is edited with hostile
  intent, today's machine checks do not catch semantic changes:
  ``validate_ci_integrity`` pins the workflow shape and the reachable-call
  inventory of ``main``, and the candidate trust anchor pins bytes, but a
  semantic edit that preserves those shapes passes both.  Protection of this
  file's semantics rests on trust-root review discipline, not on a machine
  guarantee.
* Registered fingerprints can only legitimately change through an
  Owner-merged edit of ``governance/baseline-red-ledger.json``.  This gate
  refuses expansion relative to ``--base`` but cannot itself verify who
  merged the baseline it compares against.
* ``observed_commit`` on a ledger entry is a provenance annotation for human
  review.  It is syntax-checked only; this gate never resolves it and never
  corroborates an entry against evidence taken at that commit.  What the gate
  does guarantee is that entries are load-bearing only once they already exist
  at ``--base``, so the annotation is never the thing standing between a
  candidate and exit 0.
* Failure-cause digests apply the seven rewrites enumerated in
  :func:`_unittest_reason_digest` (eighth-round R8 correction: the earlier
  "exactly three" arithmetic undercounted what the code does): (1) every
  line loses its trailing whitespace; (2) inside a traceback preamble a
  frame line loses its line number and the unbounded run of lines indented
  deeper than the frame -- the echoed source line, the 3.11+ caret line,
  and anything else that deep -- is swallowed; (3) a preamble frame path
  has its backslashes rewritten to forward slashes; (4) this checkout's own
  absolute root is stripped from every line, in both spellings (``/tmp/...``
  and its ``/private/tmp/...`` alias); (5) the item order of an inline
  brace group is sorted only when every item is a string literal (only
  str/bytes hashing is randomized); (6) blank-line runs collapse to a
  single blank and leading and trailing blank lines are dropped; (7) the
  item run under a unittest set-comparison header is sorted whatever the
  items look like -- a distinct rule from (5), with no string-literal
  restriction.  Anything those seven rewrites do not cover is hashed
  as-is.  In particular no timestamp is ever erased -- not in an assertion
  message and not in a line-leading logger position; no temporary path is
  ever erased -- not the mkdtemp segment and not the tail after it; and no
  hex address is ever erased -- not a bare literal and not a ``<... at
  0x...>`` repr tail.  The sixth-round review measured all three of those
  rules at zero hits on the real Tests/ci red log while each carried a
  release-shaped collision, so they were deleted rather than narrowed.
  Content that is unstable but not covered here simply MISMATCHes, which
  blocks; normalization is never widened to buy quiet.
* Introducing a populated ledger fails its own drift check
  (``LEDGER_INTRODUCED``) and merges red.  That is the shipping form, by
  design: the run that registers a red can never clear itself, and the
  three-state policy takes effect from the next commit.  It is not staged as
  "empty file now, entries later" -- that would be a post-merge obligation,
  and this project does not create those.
"""

from __future__ import annotations

import argparse
import atexit
import contextlib
import datetime as dt
import errno
import fnmatch
import functools
import hashlib
import importlib.machinery
import json
import os
import re
import shlex
import shutil
import stat
import sys
import tempfile
import time
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = Path(__file__).resolve().parent
if str(CI_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(CI_DIRECTORY))

from phase0 import (  # noqa: E402
    REQUIRED_DOTNET_SDK,
    REQUIRED_NODE_VERSION,
    REQUIRED_PYTHON,
    VERIFICATION_LEVEL,
    CommandResult,
    Phase0Error,
    check_from_command,
    evaluate_checks,
    discover_registered_module_dirs,
    git_output,
    load_json_compatible_yaml,
    manifest_module_id,
    new_check,
    resolve_instruction_receipt,
    run_command,
    sha256_text,
    sha256_file,
    stable_json,
    toolchain_details,
    validate_ci_integrity,
    validate_governance,
    validate_instruction_receipt,
    validate_json_schema,
    workspace_digest,
    _open_directory_no_follow,
    _write_all,
)


ALLOWED_MANIFEST_ENVIRONMENT = {"PYTHONPATH"}
ALLOWED_TRUSTED_EXECUTOR_ENVIRONMENT = ALLOWED_MANIFEST_ENVIRONMENT | {
    "DPS_LEGACY_BASELINE_ANCHOR",
    "DPS_PSQL",
    "DPS_TEST_POSTGRES",
    "DPS_TEST_POSTGRES_ADMIN_URI",
    "DPS_TEST_POSTGRES_RUNTIME_URI",
    "DPS_TEST_POSTGRES_URI",
    "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE",
}
PYTHON_NAMES = {"python", "python3", "python3.12"}
REPOSITORY_VENV_PYTHON = ".venv/bin/python"
LEGACY_BASELINE_ANCHOR_ENV = "DPS_LEGACY_BASELINE_ANCHOR"
LEGACY_BASELINE_MODULE_ID = "legacy-runtime-adapter"
LEGACY_BASELINE_SUITE_ID = "legacy-runtime-adapter.static"
FORBIDDEN_COMMAND_FRAGMENTS = ("\n", "\r", "\x00", "`", "$(`", "${", ";", "|", ">", "<")
PUBLICATION_SCHEMA_VERSION = "dps.evidence-publication/v1"
PUBLICATION_MARKER_SUFFIX = ".publication.json"
PUBLICATION_CLAIM_SUFFIX = ".publication.lock"
MAX_PUBLICATION_FILE_BYTES = 64 * 1024 * 1024
DEFAULT_PHASE0_RUNS_ROOT = Path("Reports/ci/phase0-runs")


@dataclass(frozen=True)
class TrustedInvocation:
    argv: Sequence[str]
    kind: str
    minimum_tests: int = 1


@dataclass(frozen=True)
class TrustedSuitePlan:
    module_id: str
    suite_id: str
    test_type: str
    evidence_level: str
    declared_command: str
    environment: Mapping[str, str]
    invocations: Sequence[TrustedInvocation]


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run the trusted DPS Phase 0 gate")
    parser.add_argument(
        "--evidence",
        default=None,
        help=(
            "Evidence JSON path. When omitted, a unique run-id directory under "
            "Reports/ci/phase0-runs is used."
        ),
    )
    parser.add_argument(
        "--node",
        default=None,
        help="Node 24 executable. Known local bundled runtimes are auto-detected.",
    )
    parser.add_argument(
        "--base",
        default=None,
        help="Baseline commit used for changed scope and instruction receipt",
    )
    parser.add_argument(
        "--receipt-in",
        default=None,
        help="Validate a previously issued instruction receipt instead of issuing one",
    )
    parser.add_argument(
        "--receipt-out",
        default=None,
        help="Optional path for the freshly validated instruction receipt",
    )
    parser.add_argument(
        "--diagnostic-workspace",
        action="store_true",
        help=(
            "Run all checks on a dirty workspace without issuing formal "
            "REPOSITORY_STATIC_VERIFIED evidence"
        ),
    )
    parser.add_argument(
        "--require-literal-pass",
        action="store_true",
        help=(
            "Exit 0 only for the literal overall status PASS; registered "
            "baseline reds (PASS_WITH_REGISTERED_BASELINE) exit nonzero. "
            "Pinned into scripts/release.sh so a registered red never releases."
        ),
    )
    return parser.parse_args(argv)


def _new_publication_run_id() -> str:
    return os.urandom(16).hex()


def _default_phase0_evidence_path(run_id: str) -> Path:
    if re.fullmatch(r"[0-9a-f]{32}", run_id) is None:
        raise Phase0Error("Phase0 publication run id is invalid")
    return DEFAULT_PHASE0_RUNS_ROOT / run_id / "phase0-evidence.json"


def resolve_baseline(explicit: Optional[str]) -> tuple[str, bool]:
    """Resolve the baseline commit and whether it is authoritative.

    Only an explicit ``--base`` or the DPS_BASELINE_COMMIT / GITHUB_BASE_SHA
    environment naming a commit other than HEAD is authoritative.  The HEAD
    fallback (also reached through an empty or all-zero candidate) keeps
    legacy consumers such as the instruction receipt working, and an explicit
    base that resolves to the current HEAD commit (``--base HEAD``, or the
    HEAD sha spelled out) is the same self-comparison in disguise, so both
    are never an authority for the baseline red ledger: callers must treat a
    non-authoritative baseline as BASELINE_COMMIT_UNAVAILABLE for drift.
    ``scripts/release.sh`` intentionally self-compares with
    ``--base "$head_commit"``; it stays safe through
    ``--require-literal-pass``, which releases only the literal PASS.
    """

    head = git_output(ROOT, ["rev-parse", "HEAD"])
    candidates = (
        explicit,
        os.environ.get("DPS_BASELINE_COMMIT"),
        os.environ.get("GITHUB_BASE_SHA"),
    )
    for candidate in candidates:
        if candidate and candidate.strip("0"):
            resolved = git_output(ROOT, ["rev-parse", candidate + "^{commit}"])
            return resolved, resolved != head
    return head, False


def workspace_cleanliness_check(root: Path, diagnostic: bool) -> Dict[str, Any]:
    started = time.monotonic()
    result = run_command(
        [
            "git",
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
            "-z",
        ],
        root,
        timeout_seconds=30,
    )
    dirty_entries = [value for value in result.output.split("\0") if value]
    clean = result.exit_code == 0 and not dirty_entries
    formal_eligible = clean and not diagnostic
    details = {
        "clean": clean,
        "diagnostic_workspace": diagnostic,
        "formal_evidence_eligible": formal_eligible,
        "dirty_entry_count": len(dirty_entries),
        "status_sha256": sha256_text(result.output),
    }
    if result.exit_code != 0:
        status = "INFRA_ERROR"
        exit_code = result.exit_code
        log = "ERROR: cannot determine repository workspace status\n" + result.output
    elif clean:
        status = "PASS"
        exit_code = 0
        log = (
            "clean checkout eligible for formal evidence"
            if not diagnostic
            else "clean checkout intentionally limited to diagnostic evidence"
        )
    elif diagnostic:
        status = "PASS"
        exit_code = 0
        log = (
            "dirty workspace accepted only for WORKSPACE_DIAGNOSTIC_ONLY; "
            "formal evidence and commit attribution are suppressed"
        )
    else:
        status = "FAIL"
        exit_code = 1
        log = (
            "dirty workspace cannot issue REPOSITORY_STATIC_VERIFIED evidence; "
            "rerun with --diagnostic-workspace for non-releasable diagnostics"
        )
    return new_check(
        "clean-checkout-evidence-boundary",
        True,
        status,
        result.command,
        exit_code,
        int((time.monotonic() - started) * 1000),
        log,
        details,
    )


def evidence_classification(
    overall_status: str, formal_evidence_eligible: bool, diagnostic: bool
) -> tuple[str, Optional[str]]:
    if diagnostic:
        return "WORKSPACE_DIAGNOSTIC_ONLY", None
    level = (
        VERIFICATION_LEVEL
        if overall_status == "PASS" and formal_evidence_eligible
        else None
    )
    return VERIFICATION_LEVEL, level


# --- Baseline red ledger (RebuildPlan §4.1 clause 6, first item) -------------
#
# The ledger registers the exact failure fingerprint of every pre-existing
# required red on the merged baseline.  A run whose only required failures
# match the registered fingerprints byte-for-byte is reported as
# PASS_WITH_REGISTERED_BASELINE (exit 0, never the literal "PASS"); every
# other deviation -- an unregistered red, a changed, moved, shrunk, or grown
# fingerprint, a stale entry whose check has partially or fully turned green,
# a ledger that expanded or was removed relative to the --base commit, or a
# run without an authoritative --base commit while the ledger is load-bearing
# -- fails closed.  The only legal terminal state is an entries list shrunk to
# empty with the ledger file still present.

BASELINE_RED_LEDGER_RELATIVE_PATH = "governance/baseline-red-ledger.json"
# v2 (CF2-P0-2) binds every registered unittest failure to a normalized
# failure-cause digest.  The bump is deliberate and breaking: a v1 document is
# refused outright rather than read as a compatible ledger, because a v1 MATCH
# was decided without ever looking at why a test failed.
BASELINE_RED_LEDGER_SCHEMA_VERSION = "dps.baseline-red-ledger/v2"
BASELINE_RED_DRIFT_CHECK_ID = "baseline-red-ledger-drift"
OVERALL_PASS_WITH_REGISTERED_BASELINE = "PASS_WITH_REGISTERED_BASELINE"
PASSING_OVERALL_STATUSES = ("PASS", OVERALL_PASS_WITH_REGISTERED_BASELINE)
BASELINE_RED_FINGERPRINT_KINDS = ("unittest", "dotnet", "error-set")

_UNITTEST_SEPARATOR_LINE = "=" * 70
_UNITTEST_RULE_LINE = "-" * 70
_UNITTEST_DETAIL_HEADER = re.compile(r"^(?:FAIL|ERROR): (\S+) \(([^()\s]+)\)(.*)$")
_UNITTEST_FRAME_LINE = re.compile(
    r'^(?P<indent>\s*)File "(?P<path>[^"]*)", line [0-9]+(?P<rest>.*)$'
)
_TRACEBACK_HEADER_LINE = "Traceback (most recent call last):"
# R6-1/R6-2/R6-3: the timestamp, temporary-path and repr-address rules were
# deleted, not narrowed.  Each was measured at zero rewritten lines on the real
# Tests/ci red log (707 lines, 41 rewritten by the ROOT strip and 41 frame
# lines), so none of them bought any measurable stability -- while each one
# admitted a collision that ended in exit 0: a moved anchor-expiry stamp
# ("2026-01-01T00:00:00Z exceeds the permitted window" vs
# "1970-01-01T00:00:00Z ..."), a swapped first temporary segment
# ("/tmp/dps-sandbox" vs "/tmp/etc-shadow-link"), and an unanchored hex rewrite
# ("mapped at 0xDEADBEEF>" vs "mapped at 0x00000000>").  An un-normalized
# wall-clock stamp can only produce a MISMATCH, which is the safe direction.
_UNITTEST_RAN_LINE = re.compile(r"^Ran ([0-9]+) tests? in \S+$")
_UNITTEST_FAILED_SUMMARY = re.compile(r"^FAILED \(([^()]*)\)$")
_UNITTEST_SUMMARY_TOKEN = re.compile(r"^(failures|errors)=([0-9]+)$")
_DOTNET_FILE_ERROR_LINE = re.compile(
    r"^(.+?)\((\d+),(\d+)\):\s*error\s+([A-Za-z]+\d+)\s*:"
)
_DOTNET_BARE_ERROR_LINE = re.compile(r"(?:^|\s)error\s+([A-Za-z]+\d+)\s*:")
_DOTNET_FAILED_TEST_LINE = re.compile(r"^failed\s+(\S+\.\S+)(?:\s|$)")
_DOTNET_TEST_COUNT_LINE = re.compile(
    r"^\s*(total|failed|succeeded|skipped):\s*([0-9]+)\s*$"
)
_DOTNET_BUILD_FAILED_LINE = re.compile(
    r"^\s*(?:Build failed with exit code\b|Build FAILED\.)"
)


def _unittest_failure_item(method: str, qualified: str, suffix: str = "") -> str:
    """Full module-qualified failure identity, never a truncated suffix.

    ``suffix`` is the whitespace-normalized remainder of the detail header
    after the qualified name -- the subTest parameter/message portion such as
    ``(i=1)`` or ``[boom] (a=1, b=2, c=3)``.  It stays part of the identity so
    each failed subTest counts once, keeping the deduplicated header count
    equal to the FAILED summary instead of collapsing every subTest of one
    method into a single item (which made any subTest failure UNPARSEABLE).
    """

    if qualified.endswith("." + method) or qualified == method:
        item = qualified
    else:
        item = qualified + "." + method
    normalized_suffix = " ".join(suffix.split())
    if normalized_suffix:
        item = item + " " + normalized_suffix
    return item


def _checkout_prefixes() -> Sequence[str]:
    """This checkout's own root, in every spelling a log can carry it.

    ``ROOT`` is ``Path(...).resolve()``d, so on macOS a checkout under
    ``/tmp/x`` is known here as ``/private/tmp/x`` while a subprocess that
    never resolved the path prints ``/tmp/x``.  Both name the same directory,
    so both are stripped.  This is a literal, fully determined prefix -- not a
    wildcard segment -- so it can only fold two spellings of one checkout onto
    the same repository-relative path; it can never fold two different
    directories together.
    """

    root = str(ROOT).rstrip("/")
    prefixes = {root}
    if root.startswith("/private/"):
        prefixes.add(root[len("/private") :])
    else:
        prefixes.add("/private" + root)
    return sorted(prefixes, key=len, reverse=True)


def _scrub_unstable_reason_text(text: str) -> str:
    """Erase the checkout prefix.  Nothing else.

    R6-1..3 (sixth-round review): the temporary-path, ``<... at 0x...>`` repr
    address and line-leading wall-clock timestamp rules were **deleted**.  All
    three rewrote zero lines of the real Tests/ci red log, so they bought no
    measurable stability, while each one merged a hostile observation onto a
    registered one and released it at exit 0.  What is left is the only rule
    that actually fires on real logs (41 of 707 lines): the checkout prefix,
    which is pure location noise -- the same repository content is reached
    through a different absolute path on every runner.

    Nothing else is touched here.  A wall-clock timestamp, a temporary path
    and a hex address now all reach the digest verbatim, so a run whose
    environment moves them MISMATCHes.  That is the fail-closed direction and
    is deliberately preferred over the collisions the rules admitted.
    """

    for prefix in _checkout_prefixes():
        text = text.replace(prefix + "/", "")
    return text


_OPEN_TO_CLOSE = {"{": "}", "[": "]", "(": ")"}
# Only str/bytes hashing is randomized per process, so the only brace group
# whose order can move between runs is one whose items are string literals.
# Requiring that shape keeps prose such as "{root, intermediate, leaf}" -- where
# the order may well be the point -- out of the sorting rule entirely.
_STRING_LITERAL_ITEM = re.compile(r"^[bBrRuUfF]{0,2}[\"']")


def _scan_quoted(text: str, start: int) -> tuple:
    """Consume one quoted run verbatim; return (text, index after it)."""

    quote = text[start]
    index = start + 1
    while index < len(text):
        character = text[index]
        if character == "\\":
            index += 2
            continue
        if character == quote:
            return text[start : index + 1], index + 1
        index += 1
    return text[start:], len(text)


def _scan_bracket_group(text: str, start: int) -> tuple:
    """Rewrite one bracketed group, sorting it only if it is a set repr.

    A ``{...}`` group whose top level holds at least two comma separated
    string literals and no ``key: value`` pair is a set/frozenset rendering of
    strings: its order is python's randomized string hashing and carries no
    information, so the items are sorted.  Everything else -- dicts (insertion
    ordered, never hash randomized), lists, tuples, unbalanced brackets, and
    any brace group that is not a run of string literals -- is re-emitted
    exactly as it came in, with nested groups still normalized, so genuine
    sequence order stays load bearing.  Sorting is a permutation of the items,
    never a merge: change, add or drop one item and the rendering changes with
    it (U1 / C5b).
    """

    opener = text[start]
    closer = _OPEN_TO_CLOSE[opener]
    index = start + 1
    items: List[str] = []
    current: List[str] = []
    saw_colon = False
    while index < len(text):
        character = text[index]
        if character == closer:
            items.append("".join(current))
            index += 1
            verbatim = opener + ",".join(items) + closer
            if (
                opener == "{"
                and not saw_colon
                and len(items) >= 2
                and all(
                    _STRING_LITERAL_ITEM.match(item.strip()) for item in items
                )
            ):
                return (
                    "{" + ", ".join(sorted(item.strip() for item in items)) + "}",
                    index,
                )
            return verbatim, index
        if character in _OPEN_TO_CLOSE:
            rendered, index = _scan_bracket_group(text, index)
            current.append(rendered)
            continue
        if character in "\"'":
            rendered, index = _scan_quoted(text, index)
            current.append(rendered)
            continue
        if character == ",":
            items.append("".join(current))
            current = []
            index += 1
            continue
        if character == ":":
            saw_colon = True
        current.append(character)
        index += 1
    return text[start:], len(text)


def _sort_inline_set_literals(line: str) -> str:
    """Normalize the item order of every inline set repr on one line."""

    rendered: List[str] = []
    index = 0
    while index < len(line):
        character = line[index]
        if character in _OPEN_TO_CLOSE:
            chunk, index = _scan_bracket_group(line, index)
            rendered.append(chunk)
            continue
        if character in "\"'":
            chunk, index = _scan_quoted(line, index)
            rendered.append(chunk)
            continue
        rendered.append(character)
        index += 1
    return "".join(rendered)


# The exact headers unittest emits before a run of items that describe an
# unordered collection (unittest.case.assertSetEqual / assertCountEqual).
_UNITTEST_UNORDERED_BLOCK_MARKERS = (
    "Items in the first set but not the second:",
    "Items in the second set but not the first:",
    "Element counts were not equal:",
)


def _is_unordered_block_marker(line: str) -> bool:
    return any(
        line.endswith(marker) for marker in _UNITTEST_UNORDERED_BLOCK_MARKERS
    )


def _sort_unordered_report_blocks(lines: Sequence[str]) -> List[str]:
    """Sort the item runs unittest emits for set comparisons.

    ``assertSetEqual`` and ``assertCountEqual`` render their differences in
    Python set iteration order, which differs between processes because string
    hashing is randomized -- and the suite runs under ``-I``, which
    deliberately suppresses PYTHONHASHSEED, so the child cannot be pinned to a
    fixed seed.  Those items describe an unordered collection, so sorting the
    run removes the randomness without removing any information: change which
    items are in the set and the sorted run changes with it.  Only these three
    unittest-authored headers trigger it; no other line ordering is touched.
    """

    result: List[str] = []
    index = 0
    while index < len(lines):
        line = lines[index]
        result.append(line)
        index += 1
        if not _is_unordered_block_marker(line):
            continue
        run_start = index
        while index < len(lines):
            candidate = lines[index]
            if not candidate.strip() or _is_unordered_block_marker(candidate):
                break
            index += 1
        result.extend(sorted(lines[run_start:index]))
    return result


def _unittest_reason_digest(body: Sequence[str]) -> str:
    """Deterministic digest of one detail block's normalized failure cause.

    Frame rewriting is scoped to the traceback preamble: the region that opens
    with ``Traceback (most recent call last):`` and closes at the first
    non-blank line indented no deeper than that header -- the exception line.
    Inside it a frame keeps its repository-relative path and its function but
    loses the line number, the echoed source line and the 3.11+ caret anchors,
    which move whenever the file is edited above the failure site.

    CF5-4: outside the preamble nothing is swallowed.  Aggregated sub-failure
    reports embed ``File "x.py", line N, in f`` shaped lines inside the
    exception *message*; the old unconditional rule dropped every indented
    line beneath them, so ``reason: reciprocal edge missing for module alpha``
    and ``reason: TRUST ROOT SIGNATURE FORGED`` hashed identically.

    CF5-5: the frame path is no longer collapsed to its base name.  The ROOT
    prefix strip in :func:`_scrub_unstable_reason_text` already removes
    checkout-location noise, so dropping the directory bought no stability and
    only made ``Modules/alpha/helper.py`` and ``Modules/omega/helper.py``
    indistinguishable.

    R8 (eighth-round review): this function performs exactly seven
    rewrites, enumerated here so the prose can be held against the code.
    (1) Every line is rstripped.  (2) A frame line inside the traceback
    preamble loses its line number, and the unbounded run of lines indented
    deeper than that frame -- the echoed source line, the 3.11+ caret line,
    and any other line that deep -- is swallowed until a blank line or a
    line at the frame's indent or shallower.  (3) The frame path's
    backslashes become forward slashes.  (4) The checkout prefix, in both
    spellings (:func:`_checkout_prefixes`), is stripped from every line.
    (5) :func:`_sort_inline_set_literals` sorts an inline brace group only
    when every item is a string literal.  (6) Blank-line runs collapse to a
    single blank, and leading and trailing blank lines are dropped.
    (7) :func:`_sort_unordered_report_blocks` sorts the line run under a
    unittest set-comparison header with no string-literal restriction -- a
    separate rule from (5), not a variant of it.  Anything a rewrite does
    not cover is hashed as-is: a different exception class or a different
    message body always produces a different digest.
    """

    normalized: List[str] = []
    index = 0
    preamble_indent: Optional[int] = None
    while index < len(body):
        raw = body[index].rstrip()
        if raw.strip() == _TRACEBACK_HEADER_LINE:
            preamble_indent = len(raw) - len(raw.lstrip())
            normalized.append(raw)
            index += 1
            continue
        if preamble_indent is not None and (
            not raw.strip() or len(raw) - len(raw.lstrip()) <= preamble_indent
        ):
            # The exception line (or a blank) closes the frame region.  From
            # here on the text is the exception message and is kept verbatim,
            # frame shaped or not, until another traceback header opens.
            preamble_indent = None
        frame = (
            _UNITTEST_FRAME_LINE.match(raw) if preamble_indent is not None else None
        )
        if frame is None:
            normalized.append(raw)
            index += 1
            continue
        indent = len(frame.group("indent"))
        normalized.append(
            '{0}File "{1}", line <n>{2}'.format(
                frame.group("indent"),
                frame.group("path").replace("\\", "/"),
                frame.group("rest"),
            )
        )
        index += 1
        while index < len(body):
            following = body[index].rstrip()
            if not following.strip():
                break
            if len(following) - len(following.lstrip()) <= indent:
                break
            index += 1
    scrubbed = _scrub_unstable_reason_text("\n".join(normalized)).split("\n")
    collapsed: List[str] = []
    for line in scrubbed:
        stripped = _sort_inline_set_literals(line.rstrip())
        if not stripped and (not collapsed or not collapsed[-1]):
            continue
        collapsed.append(stripped)
    while collapsed and not collapsed[-1]:
        collapsed.pop()
    return sha256_text(stable_json(_sort_unordered_report_blocks(collapsed)))


def unittest_failure_fingerprint(
    log: str, required_inventory: Optional[Sequence[str]] = None
) -> Dict[str, Any]:
    """Fingerprint a python-unittest FAIL log from its canonical tail only.

    The parse domain is the unittest tail: exactly one ``Ran N tests`` line,
    exactly one ``FAILED (failures=X, errors=Y)`` summary as the last
    non-empty line, and detail headers that are counted only when they
    directly follow a 70-character ``=`` separator line.  Free-form prints in
    the progress region are never counted, and the deduplicated header count
    must equal X+Y; any deviation yields ``parse_error`` (never a match).
    ``executed`` (from the Ran line) and ``missing_inventory`` (required
    inventory names absent from the log) are part of the fingerprint, so
    deleting a carrier test file changes the fingerprint even while the check
    is already red.  ``failure_reasons`` binds each failed identity to a
    normalized digest of its detail block (CF2-P0-2), so a registered test that
    starts failing for a new reason is a new failure, not a registered one.
    """

    missing = (
        sorted(name for name in set(required_inventory) if name not in log)
        if required_inventory is not None
        else []
    )
    fingerprint: Dict[str, Any] = {
        "kind": "unittest",
        "failed_tests": [],
        "failure_reasons": {},
        "count": 0,
        "executed": 0,
        "missing_inventory": missing,
    }

    def failed(reason: str) -> Dict[str, Any]:
        fingerprint["parse_error"] = reason
        return fingerprint

    lines = log.splitlines()
    ran_indexes = [
        index for index, line in enumerate(lines) if _UNITTEST_RAN_LINE.match(line)
    ]
    if len(ran_indexes) != 1:
        return failed(
            "expected exactly one canonical 'Ran N tests' line, found {0}".format(
                len(ran_indexes)
            )
        )
    ran_index = ran_indexes[0]
    fingerprint["executed"] = int(
        _UNITTEST_RAN_LINE.match(lines[ran_index]).group(1)
    )
    tail_lines = [line for line in lines[ran_index + 1 :] if line.strip()]
    if len(tail_lines) != 1:
        return failed(
            "expected exactly the FAILED summary after the Ran line, found "
            + repr(tail_lines)
        )
    summary_match = _UNITTEST_FAILED_SUMMARY.match(tail_lines[0])
    if summary_match is None:
        return failed(
            "log does not end with a canonical FAILED summary: "
            + repr(tail_lines[0])
        )
    summary_total = 0
    for token in summary_match.group(1).split(", "):
        token_match = _UNITTEST_SUMMARY_TOKEN.match(token)
        if token_match is None:
            return failed("unsupported FAILED summary token: " + repr(token))
        summary_total += int(token_match.group(2))
    separator_indexes = [
        index for index in range(ran_index) if lines[index] == _UNITTEST_SEPARATOR_LINE
    ]
    items: Dict[str, str] = {}
    for position, index in enumerate(separator_indexes):
        header = (
            _UNITTEST_DETAIL_HEADER.match(lines[index + 1])
            if index + 1 < ran_index
            else None
        )
        if header is None:
            # A separator that opens no detail block would silently truncate
            # the preceding block's body, so a cause could be split off and
            # dropped from its digest.  Inside the parse domain every 70-'='
            # line must open a block; anything else is unparseable, never a
            # match.
            #
            # U2: one decorative 70-'=' banner voids every registered entry of
            # the check, so the error has to name the culprit.  The two lines
            # on each side of the offending separator are quoted verbatim --
            # that window contains the printing test's own output.
            return failed(
                "separator line at offset {0} does not open a detail block; "
                "context lines {1}-{2}: {3!r}".format(
                    index,
                    max(0, index - 2),
                    min(len(lines) - 1, index + 2),
                    lines[max(0, index - 2) : index + 3],
                )
            )
        item = _unittest_failure_item(
            header.group(1), header.group(2), header.group(3)
        )
        rule_index = index + 2
        if rule_index >= ran_index or lines[rule_index] != _UNITTEST_RULE_LINE:
            return failed(
                "detail block for {0!r} is not followed by the canonical "
                "70-dash rule line".format(item)
            )
        end = (
            separator_indexes[position + 1]
            if position + 1 < len(separator_indexes)
            else ran_index
        )
        body = list(lines[rule_index + 1 : end])
        while body and not body[-1].strip():
            body.pop()
        if body and body[-1] == _UNITTEST_RULE_LINE:
            body.pop()
            while body and not body[-1].strip():
                body.pop()
        if not body:
            return failed("detail block for {0!r} has an empty body".format(item))
        digest = _unittest_reason_digest(body)
        if items.setdefault(item, digest) != digest:
            return failed(
                "detail blocks for {0!r} disagree on the failure cause".format(item)
            )
    if len(items) != summary_total:
        return failed(
            "deduplicated detail headers ({0}) do not equal the FAILED summary "
            "failures+errors ({1})".format(len(items), summary_total)
        )
    fingerprint["failed_tests"] = sorted(items)
    fingerprint["failure_reasons"] = {name: items[name] for name in sorted(items)}
    fingerprint["count"] = len(items)
    return fingerprint


def dotnet_failure_fingerprint(
    log: str, root_prefixes: Optional[Sequence[str]] = None
) -> Dict[str, Any]:
    """Fingerprint a dotnet check by exact diagnostic instances plus counts.

    Compiler and analyzer diagnostics are collected as unique repository
    relative ``(file, line, column, code)`` instances (MSBuild reprints of the
    same diagnostic deduplicate), then hashed as a sorted table; moving a
    diagnostic to another line or file therefore always changes the
    fingerprint.  Test-run checks additionally pin the exact
    total/failed/succeeded/skipped summary counts, and compile checks pin the
    build-failed marker, so shrinking green coverage or losing the failure
    marker also changes the fingerprint.
    """

    prefixes = [
        str(value).rstrip("/") + "/"
        for value in (root_prefixes if root_prefixes is not None else [str(ROOT)])
    ]

    def relative(path: str) -> str:
        for prefix in prefixes:
            if path.startswith(prefix):
                return path[len(prefix) :]
        return path

    def normalized(text: str) -> str:
        for prefix in prefixes:
            text = text.replace(prefix, "")
        return text

    file_instances: set = set()
    bare_instances: set = set()
    failed_tests: set = set()
    count_values: Dict[str, List[int]] = {
        "total": [],
        "failed": [],
        "succeeded": [],
        "skipped": [],
    }
    build_failed = False
    for raw_line in log.splitlines():
        line = raw_line.strip()
        if _DOTNET_BUILD_FAILED_LINE.match(line):
            build_failed = True
            continue
        count_match = _DOTNET_TEST_COUNT_LINE.match(raw_line)
        if count_match is not None:
            count_values[count_match.group(1)].append(int(count_match.group(2)))
            continue
        file_match = _DOTNET_FILE_ERROR_LINE.match(line)
        if file_match is not None:
            file_instances.add(
                (
                    relative(file_match.group(1)),
                    int(file_match.group(2)),
                    int(file_match.group(3)),
                    file_match.group(4),
                )
            )
            continue
        failed_match = _DOTNET_FAILED_TEST_LINE.match(line)
        if failed_match is not None:
            failed_tests.add(failed_match.group(1))
            continue
        bare_match = _DOTNET_BARE_ERROR_LINE.search(line)
        if bare_match is not None:
            bare_instances.add((bare_match.group(1), normalized(line)))
    error_codes: Dict[str, int] = {}
    for instance in file_instances:
        error_codes[instance[3]] = error_codes.get(instance[3], 0) + 1
    for code, _line in bare_instances:
        error_codes[code] = error_codes.get(code, 0) + 1
    instance_table = {
        "file_instances": sorted(list(item) for item in file_instances),
        "bare_instances": sorted(list(item) for item in bare_instances),
    }
    fingerprint: Dict[str, Any] = {
        "kind": "dotnet",
        "error_instances_sha256": sha256_text(stable_json(instance_table)),
        "error_codes": {code: error_codes[code] for code in sorted(error_codes)},
        "files": sorted({instance[0] for instance in file_instances}),
        "failed_tests": sorted(failed_tests),
        "test_counts": None,
        "build_failed": build_failed,
        "count": sum(error_codes.values()) + len(failed_tests),
    }
    observed_counts = {key for key, values in count_values.items() if values}
    if observed_counts:
        if observed_counts != set(count_values) or any(
            len(values) != 1 for values in count_values.values()
        ):
            fingerprint["parse_error"] = (
                "ambiguous or partial dotnet test summary counts: "
                + repr({key: values for key, values in count_values.items()})
            )
            return fingerprint
        fingerprint["test_counts"] = {
            key: values[0] for key, values in count_values.items()
        }
    return fingerprint


def error_set_fingerprint(log: str) -> Dict[str, Any]:
    """Fingerprint of an in-process check: sha256 of the sorted error set."""

    lines = log.splitlines()
    if lines and lines[0].startswith("ERROR: "):
        lines[0] = lines[0][len("ERROR: ") :]
    errors = sorted({line.strip() for line in lines if line.strip()})
    return {
        "kind": "error-set",
        "errors_sha256": sha256_text(stable_json(errors)),
        "count": len(errors),
    }


def compute_failure_fingerprint(
    kind: str,
    log: str,
    required_inventory: Optional[Sequence[str]] = None,
    root_prefixes: Optional[Sequence[str]] = None,
) -> Dict[str, Any]:
    if kind == "unittest":
        return unittest_failure_fingerprint(log, required_inventory)
    if kind == "dotnet":
        return dotnet_failure_fingerprint(log, root_prefixes)
    if kind == "error-set":
        return error_set_fingerprint(log)
    raise Phase0Error("unknown baseline red fingerprint kind: " + str(kind))


def fingerprint_relation(
    observed: Mapping[str, Any], registered: Mapping[str, Any]
) -> str:
    """Return MATCH, SHRUNK, or GREW for observed relative to registered.

    Only the unittest failed-test set can be recognised as SHRUNK, and only
    while its executed count, missing-inventory list and the failure-cause
    digest of every surviving failure are unchanged.  Every dotnet and
    error-set deviation -- including a genuine partial fix, a line number drift
    after editing a file with registered errors, or a changed test count -- is
    GREW: reseeding those fingerprints is an Owner-merged ledger change by
    design, never an automatic pass.
    """

    if "parse_error" in observed or "parse_error" in registered:
        return "GREW"
    if observed.get("kind") != registered.get("kind"):
        return "GREW"
    kind = registered.get("kind")
    if kind == "unittest":
        if observed.get("executed") != registered.get("executed"):
            return "GREW"
        if list(observed.get("missing_inventory") or []) != list(
            registered.get("missing_inventory") or []
        ):
            return "GREW"
        observed_reasons = observed.get("failure_reasons")
        registered_reasons = registered.get("failure_reasons")
        if not isinstance(observed_reasons, Mapping) or not isinstance(
            registered_reasons, Mapping
        ):
            # CF2-P0-2: a cause-blind fingerprint predates the cause-bound
            # schema and can never be recognised, on either side.
            return "GREW"
        observed_tests = set(observed.get("failed_tests") or [])
        registered_tests = set(registered.get("failed_tests") or [])
        if not observed_tests <= registered_tests:
            return "GREW"
        for name in observed_tests:
            # A surviving failure that changed cause is a new failure wearing
            # the registered test's name; ``.get`` on a missing side is None
            # and therefore also GREW.
            if observed_reasons.get(name) != registered_reasons.get(name):
                return "GREW"
        if observed_tests == registered_tests:
            return "MATCH"
        return "SHRUNK"
    if kind in ("dotnet", "error-set"):
        return "MATCH" if dict(observed) == dict(registered) else "GREW"
    return "GREW"


def _sorted_unique_string_list_errors(value: Any, label: str) -> List[str]:
    if not isinstance(value, list) or any(
        not isinstance(item, str) or not item for item in value
    ):
        return [label + " must be a list of non-empty strings"]
    if value != sorted(set(value)):
        return [label + " must be sorted and free of duplicates"]
    return []


def _non_negative_int(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _fingerprint_errors(fingerprint: Any) -> List[str]:
    if not isinstance(fingerprint, Mapping):
        return ["failure_fingerprint must be an object"]
    if "parse_error" in fingerprint:
        return ["a fingerprint with parse_error may never be registered"]
    kind = fingerprint.get("kind")
    errors: List[str] = []
    if kind == "unittest":
        if set(fingerprint) != {
            "kind",
            "failed_tests",
            "failure_reasons",
            "count",
            "executed",
            "missing_inventory",
        }:
            # A pre-CF2-P0-2 entry has no failure_reasons and lands here: the
            # cause-blind schema fails closed, it is never accepted as
            # "compatible".
            return [
                "unittest fingerprint must have exactly kind/failed_tests/"
                "failure_reasons/count/executed/missing_inventory"
            ]
        errors.extend(
            _sorted_unique_string_list_errors(
                fingerprint.get("failed_tests"), "failed_tests"
            )
        )
        errors.extend(
            _sorted_unique_string_list_errors(
                fingerprint.get("missing_inventory"), "missing_inventory"
            )
        )
        reasons = fingerprint.get("failure_reasons")
        if not isinstance(reasons, Mapping) or any(
            not isinstance(name, str)
            or not isinstance(digest, str)
            or re.fullmatch(r"[0-9a-f]{64}", digest) is None
            for name, digest in reasons.items()
        ):
            errors.append(
                "failure_reasons must map failed test names to lowercase "
                "sha256 hex digests"
            )
        if errors:
            return errors
        if sorted(reasons) != fingerprint["failed_tests"]:
            errors.append(
                "failure_reasons must cover exactly the failed_tests names"
            )
        if not fingerprint["failed_tests"]:
            errors.append("failed_tests must not be empty")
        if fingerprint.get("count") != len(fingerprint["failed_tests"]):
            errors.append("count must equal the number of failed_tests")
        executed = fingerprint.get("executed")
        # Not bounded below by len(failed_tests): one executed test can fail
        # several subTests, each a distinct failure identity.
        if not _non_negative_int(executed) or executed < 1:
            errors.append("executed must be a positive integer")
        return errors
    if kind == "dotnet":
        if set(fingerprint) != {
            "kind",
            "error_instances_sha256",
            "error_codes",
            "files",
            "failed_tests",
            "test_counts",
            "build_failed",
            "count",
        }:
            return [
                "dotnet fingerprint must have exactly kind/error_instances_sha256/"
                "error_codes/files/failed_tests/test_counts/build_failed/count"
            ]
        digest = fingerprint.get("error_instances_sha256")
        if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            errors.append(
                "error_instances_sha256 must be a lowercase sha256 hex digest"
            )
        error_codes = fingerprint.get("error_codes")
        if not isinstance(error_codes, Mapping) or any(
            not isinstance(code, str)
            or not code
            or not isinstance(value, int)
            or isinstance(value, bool)
            or value < 1
            for code, value in error_codes.items()
        ):
            errors.append("error_codes must map non-empty codes to positive integers")
        errors.extend(
            _sorted_unique_string_list_errors(fingerprint.get("files"), "files")
        )
        errors.extend(
            _sorted_unique_string_list_errors(
                fingerprint.get("failed_tests"), "failed_tests"
            )
        )
        if not isinstance(fingerprint.get("build_failed"), bool):
            errors.append("build_failed must be a boolean")
        test_counts = fingerprint.get("test_counts")
        if test_counts is not None:
            if (
                not isinstance(test_counts, Mapping)
                or set(test_counts) != {"total", "failed", "succeeded", "skipped"}
                or not all(_non_negative_int(value) for value in test_counts.values())
            ):
                errors.append(
                    "test_counts must be null or exactly "
                    "total/failed/succeeded/skipped non-negative integers"
                )
        if errors:
            return errors
        if test_counts is None:
            if fingerprint["failed_tests"]:
                errors.append("failed_tests require the pinned test_counts summary")
        else:
            if test_counts["total"] != (
                test_counts["failed"]
                + test_counts["succeeded"]
                + test_counts["skipped"]
            ):
                errors.append("test_counts total must equal failed+succeeded+skipped")
            if test_counts["failed"] != len(fingerprint["failed_tests"]):
                errors.append(
                    "test_counts failed must equal the failed_tests length"
                )
        total = sum(error_codes.values()) + len(fingerprint["failed_tests"])
        if total < 1:
            errors.append("dotnet fingerprint must register at least one failure item")
        if fingerprint.get("count") != total:
            errors.append("count must equal error instances plus failed tests")
        return errors
    if kind == "error-set":
        if set(fingerprint) != {"kind", "errors_sha256", "count"}:
            return ["error-set fingerprint must have exactly kind/errors_sha256/count"]
        digest = fingerprint.get("errors_sha256")
        if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            errors.append("errors_sha256 must be a lowercase sha256 hex digest")
        count = fingerprint.get("count")
        if not isinstance(count, int) or isinstance(count, bool) or count < 1:
            errors.append("count must be a positive integer")
        return errors
    return ["fingerprint kind must be one of: " + ", ".join(BASELINE_RED_FINGERPRINT_KINDS)]


def parse_baseline_red_ledger(text: str, source_label: str) -> List[Dict[str, Any]]:
    """Parse and strictly validate one ledger document; fail closed on errors."""

    try:
        payload = json.loads(text)
    except Exception as exc:
        raise Phase0Error(
            "{0}: ledger is not valid JSON: {1}".format(source_label, exc)
        )
    if not isinstance(payload, dict):
        raise Phase0Error(source_label + ": ledger must be a JSON object")
    errors: List[str] = []
    if set(payload) - {"notes"} != {"schema_version", "entries"}:
        errors.append(
            "ledger must have exactly schema_version and entries plus optional notes"
        )
    if "notes" in payload and (
        not isinstance(payload["notes"], list)
        or any(
            not isinstance(item, str) or not item.strip()
            for item in payload["notes"]
        )
    ):
        errors.append("notes must be a list of non-empty strings")
    if payload.get("schema_version") != BASELINE_RED_LEDGER_SCHEMA_VERSION:
        errors.append(
            "schema_version must be " + BASELINE_RED_LEDGER_SCHEMA_VERSION
        )
    entries = payload.get("entries")
    if not isinstance(entries, list):
        errors.append("entries must be a list")
        entries = []
    seen_ids: set = set()
    expected_keys = {
        "check_id",
        "registered_batch",
        "root_cause",
        "observed_commit",
        "failure_fingerprint",
    }
    for index, entry in enumerate(entries):
        label = "entries[{0}]".format(index)
        if not isinstance(entry, Mapping):
            errors.append(label + " must be an object")
            continue
        if set(entry) != expected_keys:
            errors.append(
                label
                + " must have exactly check_id/registered_batch/root_cause/"
                + "observed_commit/failure_fingerprint"
            )
            continue
        check_id = entry.get("check_id")
        if not isinstance(check_id, str) or not check_id:
            errors.append(label + " check_id must be a non-empty string")
            continue
        if check_id == BASELINE_RED_DRIFT_CHECK_ID:
            errors.append(
                label + " may not register the ledger drift check itself"
            )
        if check_id in seen_ids:
            errors.append("duplicate ledger entry for check " + check_id)
        seen_ids.add(check_id)
        if not isinstance(entry.get("registered_batch"), str) or not entry[
            "registered_batch"
        ]:
            errors.append(label + " registered_batch must be a non-empty string")
        if not isinstance(entry.get("root_cause"), str) or not entry["root_cause"]:
            errors.append(label + " root_cause must be a non-empty string")
        observed_commit = entry.get("observed_commit")
        if (
            not isinstance(observed_commit, str)
            or re.fullmatch(r"[0-9a-f]{40}", observed_commit) is None
        ):
            errors.append(label + " observed_commit must be a 40-hex commit")
        errors.extend(
            label + " " + message
            for message in _fingerprint_errors(entry.get("failure_fingerprint"))
        )
    if errors:
        raise Phase0Error(source_label + ": " + "; ".join(errors))
    return [dict(entry) for entry in entries]


def _evaluate_ledger_drift(state: Dict[str, Any]) -> None:
    if state["errors"]:
        state["drift_status"] = "LEDGER_INVALID"
        state["drift_errors"] = list(state["errors"])
        return
    if state["baseline_commit"] is None:
        state["drift_status"] = "BASELINE_COMMIT_UNAVAILABLE"
        entries = state["entries"] if isinstance(state["entries"], list) else None
        if state["present"] and entries is not None and not entries:
            # An inert ledger (present, zero entries) excuses nothing, so an
            # unverifiable drift has nothing to hide; all-green still passes.
            return
        state["drift_errors"] = [
            "no authoritative baseline commit (--base, DPS_BASELINE_COMMIT, or "
            "GITHUB_BASE_SHA): ledger drift is UNKNOWN and the ledger is "
            "load-bearing, so this run fails closed"
        ]
        return
    if not state["present"] and state["baseline_present"] is not True:
        state["drift_status"] = "LEDGER_ABSENT"
        return
    if not state["present"]:
        state["drift_status"] = "LEDGER_REMOVED"
        state["drift_errors"] = [
            "removal: {0} exists at the baseline commit but not in the working "
            "tree; deleting the ledger resets the shrink-only ratchet and is "
            "never a legal transition -- the only legal terminal state is an "
            "empty entries list with the file still present".format(
                BASELINE_RED_LEDGER_RELATIVE_PATH
            )
        ]
        return
    if state["baseline_present"] is not True:
        # CF2-P0-1: bootstrap is expansion.  With no ledger at the
        # authoritative base commit every entry is self-reported by this
        # candidate and anchored to nothing the gate can read independently --
        # ``observed_commit`` is syntax-checked, never resolved or corroborated
        # -- so accepting them here let the introducing PR register the reds
        # its own diff created and then match them against its own current
        # failures for exit 0.  The introducing run therefore never earns
        # PASS_WITH_REGISTERED_BASELINE; entries only become load-bearing from
        # the first commit whose base already carries them, i.e. after an
        # Owner merge, exactly like every other expansion.
        #
        # This is the designed shipping form, not a defect and not a staging
        # instruction: the change that introduces a populated ledger fails its
        # own drift check and merges red, and the three-state policy starts
        # deciding from the next commit.  It is deliberately NOT split into
        # "add an empty file now, register in a follow-up PR" -- that would be
        # a post-merge obligation, which this project does not create.
        state["drift_status"] = "LEDGER_INTRODUCED"
        entries = state["entries"] if isinstance(state["entries"], list) else None
        if entries is not None and not entries:
            # An inert ledger (introduced with zero entries) excuses nothing,
            # so the shrink-only ratchet may legally start from empty.
            return
        state["drift_errors"] = [
            "bootstrap: {0} does not exist at the authoritative baseline commit "
            "{1}, so all {2} of its entries are this candidate's own unverified "
            "claim; the run that introduces registered baseline reds can never "
            "clear itself.  This run therefore fails by design.  The entries "
            "become load-bearing once the Owner has merged them, from the first "
            "commit whose base already carries the ledger; no follow-up change "
            "is owed.".format(
                BASELINE_RED_LEDGER_RELATIVE_PATH,
                state["baseline_commit"],
                len(entries) if entries is not None else "unparsed",
            )
        ]
        return
    current_by_id = {entry["check_id"]: entry for entry in state["entries"]}
    baseline_by_id = {entry["check_id"]: entry for entry in state["baseline_entries"]}
    drift_errors: List[str] = []
    shrunk = bool(set(baseline_by_id) - set(current_by_id))
    for check_id, entry in current_by_id.items():
        baseline_entry = baseline_by_id.get(check_id)
        if baseline_entry is None:
            drift_errors.append(
                "expansion: new ledger entry for check " + check_id
            )
            continue
        relation = fingerprint_relation(
            entry["failure_fingerprint"], baseline_entry["failure_fingerprint"]
        )
        if relation == "GREW":
            drift_errors.append(
                "expansion: fingerprint for check {0} grew or changed relative "
                "to the baseline ledger".format(check_id)
            )
        elif relation == "SHRUNK":
            shrunk = True
    if drift_errors:
        state["drift_status"] = "LEDGER_EXPANDED"
        state["drift_errors"] = drift_errors
    elif shrunk:
        state["drift_status"] = "LEDGER_SHRUNK"
    else:
        state["drift_status"] = "LEDGER_UNCHANGED"


def load_baseline_red_ledger_state(
    root: Path, baseline: Optional[str]
) -> Dict[str, Any]:
    """Read the working tree ledger and its blob at the baseline commit.

    ``baseline`` must be an authoritative commit (--base, DPS_BASELINE_COMMIT,
    or GITHUB_BASE_SHA).  ``None`` -- including the resolve_baseline HEAD
    fallback, which would make every drift comparison a vacuous
    self-comparison -- is recorded as BASELINE_COMMIT_UNAVAILABLE and fails
    closed whenever the ledger is load-bearing.
    """

    state: Dict[str, Any] = {
        "ledger_path": BASELINE_RED_LEDGER_RELATIVE_PATH,
        "present": False,
        "ledger_sha256": None,
        "entries": None,
        "errors": [],
        "baseline_commit": baseline,
        "baseline_present": None,
        "baseline_ledger_sha256": None,
        "baseline_entries": None,
        "drift_status": None,
        "drift_errors": [],
    }
    path = root / BASELINE_RED_LEDGER_RELATIVE_PATH
    if path.is_file():
        state["present"] = True
        try:
            text = path.read_text(encoding="utf-8-sig")
        except OSError as exc:
            state["errors"].append(
                "cannot read {0}: {1}".format(BASELINE_RED_LEDGER_RELATIVE_PATH, exc)
            )
        else:
            state["ledger_sha256"] = sha256_text(text)
            try:
                state["entries"] = parse_baseline_red_ledger(
                    text, "working tree ledger"
                )
            except Phase0Error as exc:
                state["errors"].append(str(exc))
    if baseline is not None:
        blob_reference = baseline + ":" + BASELINE_RED_LEDGER_RELATIVE_PATH
        # ``git cat-file -e <commit>:<path>`` fails identically when the path is
        # absent and when the commit itself is unreadable (a shallow fetch, a
        # pruned object).  Only the first is "no ledger at the baseline"; the
        # second is a missing baseline authority and must be an error, never a
        # bootstrap classification the run could be measured against.
        commit_probe = run_command(
            ["git", "cat-file", "-e", baseline + "^{commit}"],
            root,
            timeout_seconds=30,
        )
        probe = run_command(
            ["git", "cat-file", "-e", blob_reference], root, timeout_seconds=30
        )
        if commit_probe.exit_code != 0:
            state["errors"].append(
                "baseline commit {0} is not readable in this checkout: the "
                "ledger cannot be compared against it".format(baseline)
            )
        elif probe.exit_code != 0:
            state["baseline_present"] = False
        else:
            state["baseline_present"] = True
            blob = run_command(
                ["git", "cat-file", "blob", blob_reference], root, timeout_seconds=30
            )
            if blob.exit_code != 0:
                state["errors"].append(
                    "cannot read baseline ledger blob {0}: exit code {1}".format(
                        blob_reference, blob.exit_code
                    )
                )
            else:
                state["baseline_ledger_sha256"] = sha256_text(blob.output)
                try:
                    state["baseline_entries"] = parse_baseline_red_ledger(
                        blob.output, "baseline ledger"
                    )
                except Phase0Error as exc:
                    state["errors"].append(str(exc))
    _evaluate_ledger_drift(state)
    return state


def baseline_red_ledger_drift_check(state: Mapping[str, Any]) -> Dict[str, Any]:
    details = {
        "ledger_path": state["ledger_path"],
        "ledger_present": state["present"],
        "ledger_sha256": state["ledger_sha256"],
        "entry_count": len(state["entries"]) if isinstance(state["entries"], list) else 0,
        "baseline_commit": state["baseline_commit"],
        "baseline_ledger_present": state["baseline_present"],
        "baseline_ledger_sha256": state["baseline_ledger_sha256"],
        "drift_status": state["drift_status"],
    }
    if state["drift_errors"]:
        log = "\n".join(
            ["ERROR: baseline red ledger failed the shrink-only drift rule:"]
            + [" - " + message for message in state["drift_errors"]]
            + [
                "ledger expansion, removal, or an unverifiable baseline requires "
                "an Owner-merged change; this gate only accepts shrinking or "
                "unchanged ledgers against an authoritative --base"
            ]
        )
        return new_check(
            BASELINE_RED_DRIFT_CHECK_ID, True, "FAIL", None, 1, 0, log, details
        )
    return new_check(
        BASELINE_RED_DRIFT_CHECK_ID,
        True,
        "PASS",
        None,
        0,
        0,
        "baseline red ledger drift status: " + str(state["drift_status"]),
        details,
    )


def evaluate_baseline_red_policy(
    checks: Sequence[Mapping[str, Any]], state: Mapping[str, Any]
) -> Dict[str, Any]:
    """Classify the run against the ledger; only exact matches may pass.

    Returns the ``baseline_red`` evidence block.  ``result`` is
    ``LEDGER_INACTIVE`` (no entries and nothing blocking: the ledger never
    rewrites the verdict), ``REGISTERED_BASELINE_ONLY`` (every ledger entry is
    exactly matched and no other required check is non-PASS), or
    ``BLOCKED``."""

    entries = state["entries"] if isinstance(state["entries"], list) else []
    checks_by_id: Dict[str, Mapping[str, Any]] = {
        str(check.get("id")): check for check in checks
    }
    required_failures = sorted(
        str(check.get("id"))
        for check in checks
        if check.get("required") is True and check.get("status") == "FAIL"
    )
    required_non_fail_non_pass = sorted(
        str(check.get("id"))
        for check in checks
        if check.get("required") is True
        and check.get("status") not in ("PASS", "FAIL")
    )
    registered_ids = {entry["check_id"] for entry in entries}
    unregistered = [
        check_id for check_id in required_failures if check_id not in registered_ids
    ]
    block_reasons: List[str] = []
    entry_results: List[Dict[str, Any]] = []
    if state["errors"]:
        block_reasons.append(
            "ledger unreadable or invalid: " + "; ".join(state["errors"])
        )
    if state["drift_errors"]:
        block_reasons.append(
            "ledger drift check failed; see " + BASELINE_RED_DRIFT_CHECK_ID
        )
    for entry in entries:
        check_id = entry["check_id"]
        registered_fingerprint = entry["failure_fingerprint"]
        check = checks_by_id.get(check_id)
        observed_fingerprint: Optional[Dict[str, Any]] = None
        if check is None:
            verdict = "STALE_MISSING_CHECK"
        elif check.get("required") is not True:
            verdict = "STALE_NOT_REQUIRED"
        elif check.get("status") == "PASS":
            verdict = "STALE_GREEN"
        elif check.get("status") != "FAIL":
            verdict = "UNMATCHABLE_STATUS_" + str(check.get("status"))
        elif (
            (check.get("details") or {}).get("reason")
            == TEST_FILE_SET_MISMATCH_REASON
        ):
            # Parent-process file-set self-check failed: the suite never ran
            # and its log is not a unittest stream.  Refuse fingerprint
            # matching outright; details are parent-authored, so a child
            # process can never clear (or set) this reason.
            verdict = TEST_FILE_SET_MISMATCH_REASON
        else:
            details = check.get("details") or {}
            observed_fingerprint = compute_failure_fingerprint(
                str(registered_fingerprint.get("kind")),
                str(check.get("log") or ""),
                required_inventory=details.get("required_inventory"),
            )
            if observed_fingerprint.get("parse_error"):
                verdict = "UNPARSEABLE_FAILURE"
            elif observed_fingerprint.get("count", 0) == 0:
                verdict = "UNPARSEABLE_FAILURE"
            else:
                relation = fingerprint_relation(
                    observed_fingerprint, registered_fingerprint
                )
                verdict = {
                    "MATCH": "MATCHED",
                    "SHRUNK": "STALE_PARTIALLY_GREEN",
                }.get(relation, "MISMATCH")
        entry_results.append(
            {
                "check_id": check_id,
                "registered_batch": entry.get("registered_batch"),
                "verdict": verdict,
                "registered_fingerprint": registered_fingerprint,
                "observed_fingerprint": observed_fingerprint,
            }
        )
        if verdict == "MATCHED":
            continue
        if verdict in (
            "STALE_GREEN",
            "STALE_PARTIALLY_GREEN",
            "STALE_MISSING_CHECK",
            "STALE_NOT_REQUIRED",
        ):
            block_reasons.append(
                "ledger entry {0} is stale ({1}); shrink {2} in the same PR".format(
                    check_id, verdict, BASELINE_RED_LEDGER_RELATIVE_PATH
                )
            )
        elif verdict == "MISMATCH":
            block_reasons.append(
                "check {0} failure set does not match its registered fingerprint; "
                "treated as an unregistered red".format(check_id)
            )
        else:
            block_reasons.append(
                "ledger entry {0} cannot be matched: {1}".format(check_id, verdict)
            )
    for check_id in unregistered:
        block_reasons.append(
            "required check {0} FAIL is not registered in {1}".format(
                check_id, BASELINE_RED_LEDGER_RELATIVE_PATH
            )
        )
    for check_id in required_non_fail_non_pass:
        block_reasons.append(
            "required check {0} has status {1}; only a literal FAIL can match "
            "a registered baseline red".format(
                check_id, checks_by_id[check_id].get("status")
            )
        )
    if block_reasons:
        result = "BLOCKED"
    elif not entries:
        result = "LEDGER_INACTIVE"
    else:
        result = "REGISTERED_BASELINE_ONLY"
    notes: List[str] = []
    if result == "REGISTERED_BASELINE_ONLY":
        notes.append(
            "overall_status {0}: {1} registered baseline red(s) remain".format(
                OVERALL_PASS_WITH_REGISTERED_BASELINE, len(entries)
            )
        )
        for entry in entries:
            notes.append(
                "registered baseline red: {0} (batch {1}, {2} failure item(s))".format(
                    entry["check_id"],
                    entry.get("registered_batch"),
                    entry["failure_fingerprint"].get("count"),
                )
            )
    elif result == "BLOCKED":
        notes.extend(block_reasons)
    return {
        "ledger_path": state["ledger_path"],
        "ledger_present": state["present"],
        "ledger_sha256": state["ledger_sha256"],
        "baseline_commit": state["baseline_commit"],
        "baseline_ledger_present": state["baseline_present"],
        "baseline_ledger_sha256": state["baseline_ledger_sha256"],
        "drift_status": state["drift_status"],
        "drift_errors": list(state["drift_errors"]),
        "ledger_errors": list(state["errors"]),
        "entries": entry_results,
        "required_failures": required_failures,
        "required_non_fail_non_pass": required_non_fail_non_pass,
        "unregistered_required_failures": unregistered,
        "result": result,
        "block_reasons": block_reasons,
        "notes": notes,
    }


def apply_baseline_red_policy(overall_status: str, baseline_red: Mapping[str, Any]) -> str:
    """Fold the ledger verdict into the overall status, fail closed."""

    result = baseline_red.get("result")
    if result == "REGISTERED_BASELINE_ONLY" and overall_status == "FAIL":
        return OVERALL_PASS_WITH_REGISTERED_BASELINE
    if result == "BLOCKED":
        return "FAIL"
    return overall_status


def gate_exit_code(overall_status: str, require_literal_pass: bool) -> int:
    """Exit policy: release paths demand the literal PASS, nothing weaker."""

    if require_literal_pass:
        return 0 if overall_status == "PASS" else 1
    return 0 if overall_status in PASSING_OVERALL_STATUSES else 1


# -----------------------------------------------------------------------------


def in_process_check(check_id: str, operation: Any) -> Dict[str, Any]:
    started = time.monotonic()
    try:
        details = operation()
        if details is None:
            details = {}
        log = json.dumps(details, ensure_ascii=False, sort_keys=True, indent=2)
        return new_check(
            check_id,
            True,
            "PASS",
            None,
            0,
            int((time.monotonic() - started) * 1000),
            log,
            details if isinstance(details, Mapping) else {},
        )
    except Phase0Error as exc:
        return new_check(
            check_id,
            True,
            "FAIL",
            None,
            1,
            int((time.monotonic() - started) * 1000),
            "ERROR: " + str(exc),
        )
    except Exception as exc:  # pragma: no cover - last-resort evidence boundary
        return new_check(
            check_id,
            True,
            "INFRA_ERROR",
            None,
            1,
            int((time.monotonic() - started) * 1000),
            "UNEXPECTED ERROR: {0}: {1}".format(type(exc).__name__, exc),
        )


# The pre-migration commit the R0-B dual-run froze its corpus from.  It lives here
# rather than beside the fixtures because this file is in CANDIDATE_TRUST_PATHS:
# its bytes are bound into the candidate trust anchor, so re-pointing the corpus at
# a different commit invalidates that anchor instead of being a quiet data edit.
# Naming it in the test module would have left the choice of "which commit" in
# candidate-writable code, where ancestry alone is satisfied by any older commit.
# Rebasing the batch onto a new base means re-freezing the fixtures and updating
# this line together.
R0B_FROZEN_BASELINE_COMMIT = "8f63593d4f262ec1496b05300da75a71b86eaab4"


# MF5: the executed-test floor is the pre-ledger floor of 167 plus the 107
# tests of the baseline red ledger guard suite; both constants are module
# level so the guard suite can pin itself against them.  It is a floor, not a
# census: the real Tests/ci discovery on main runs well above it.  The 107 is
# the measured method count of Tests/ci/test_baseline_red_ledger.py, pinned by
# test_guard_suite_methods_are_pinned_into_the_gate_inventory.
PHASE0_MINIMUM_ADVERSARIAL_TESTS = 274
PHASE0_REQUIRED_UNITTEST_INVENTORY = frozenset(
    {
        "test_missing_standard_module_layout_is_rejected",
        "test_placeholder_only_src_is_rejected",
        "test_standard_layout_symlink_is_rejected",
        "test_ignored_only_standard_directory_is_not_reproducible",
        "test_missing_runtime_entrypoint_is_rejected",
        "test_missing_artifact_build_path_is_rejected",
        "test_cross_module_src_project_reference_is_rejected",
        "test_shell_metacharacter_is_rejected",
        "test_unknown_environment_prefix_is_rejected",
        "test_manifest_cannot_declare_trusted_executor_postgres_environment",
        "test_manifest_cannot_declare_legacy_baseline_anchor",
        "test_repository_venv_direct_test_runs_isolated_unittest",
        "test_repository_venv_requires_isolated_mode",
        "test_unknown_repository_venv_interpreter_is_rejected",
        "test_repository_venv_direct_test_rejects_extra_arguments",
        "test_legacy_anchor_is_injected_only_for_exact_static_suite",
        "test_missing_writable_and_repository_local_legacy_anchors_fail_closed",
        "test_repository_workflow_builds_venv_and_external_anchor_boundary",
        "test_repository_workflow_limits_dps_writes_to_exact_dotnet_output_islands",
        "test_dotnet_output_islands_cover_exact_solution_project_set",
        "test_candidate_gate_forwards_only_legacy_anchor_to_cumulative_phase0",
        "test_candidate_git_uses_canonical_safe_directory_with_system_config_disabled",
        "test_candidate_git_blob_uses_the_same_canonical_safe_directory",
        "test_path_traversal_is_rejected",
        "test_symlink_escape_is_rejected",
        "test_zero_unittests_fails_even_with_exit_zero",
        "test_skipped_unittest_fails_even_with_exit_zero",
        "test_plain_stdout_pass_is_not_test_evidence",
        "test_bash_suite_cannot_pass_from_a_different_test_category",
        "test_bash_suite_preserves_strict_category_specific_floor",
        "test_bash_suite_rejects_non_literal_category_floor",
        "test_bash_category_must_match_declared_suite_type",
        "test_bash_suite_rejects_extra_argument",
        "test_json_tool_is_not_semantic_contract_evidence",
        "test_timeout_is_infrastructure_error",
        "test_missing_required_static_suite_generates_failure",
        "test_required_suite_missing_command_generates_failure",
        "test_solution_omitting_project_fails_before_build",
        "test_missing_dotnet_pin_is_rejected",
        "test_mutable_action_tag_is_rejected",
        "test_latest_runner_image_is_rejected",
        "test_unapproved_action_is_rejected",
        "test_node_patch_version_mismatch_is_rejected",
        "test_trusted_environment_ignores_path_and_dps_node_injection",
        "test_unlocked_fixed_node_candidate_fails_closed",
        "test_trusted_environment_rejects_path_override",
        "test_trusted_environment_forwards_candidate_postgres_keys",
        "test_trusted_dotnet_ignores_ambient_path_and_home",
        "test_trusted_environment_uses_private_non_ambient_state",
        "test_manifest_cannot_disable_the_executor_network_sandbox",
        "test_world_accessible_trusted_state_is_rejected",
        "test_restore_platform_failure_is_infrastructure_error",
        "test_command_timeout_kills_the_posix_process_group",
        "test_phase0_output_is_restricted_to_lowercase_ignored_reports_ci",
        "test_clean_checkout_is_formal_evidence_eligible",
        "test_modified_tracked_test_file_blocks_formal_evidence",
        "test_untracked_production_file_blocks_formal_evidence",
        "test_dirty_diagnostic_workspace_never_issues_formal_level",
        "test_global_required_unittest_skip_is_not_pass",
        "test_global_required_unittest_expected_failure_is_not_pass",
        "test_release_git_commit_is_rejected",
        "test_release_git_push_in_command_substitution_is_rejected",
        "test_release_missing_candidate_bom_validator_is_rejected",
        "test_release_comment_only_invocations_are_rejected",
        "test_release_network_command_is_rejected",
        "test_release_deployment_command_is_rejected",
        "test_release_file_deletion_is_rejected",
        "test_release_arbitrary_python_is_rejected",
        "test_runner_comment_only_markers_are_rejected",
        "test_runner_fixed_success_is_rejected",
        "test_runner_if_false_operations_are_not_reachable",
        "test_runner_calls_after_return_are_not_reachable",
        "test_workflow_echo_gate_is_rejected",
        "test_workflow_always_false_gate_step_is_rejected",
        "test_workflow_pip_without_require_hashes_is_rejected",
        "test_workflow_diagnostic_mode_is_rejected",
        "test_candidate_level_is_schema_locked_to_null",
        "test_candidate_trust_paths_are_retained_and_factory_independent",
        "test_audit_contract_cannot_be_retargeted_to_production_source",
        "test_policy_floor_is_injected_into_every_effective_plan",
        "test_duplicate_unittest_summaries_cannot_raise_the_count_floor",
        "test_isolated_python_ignores_shadow_unittest_module",
        "test_postgres_password_value_is_redacted_even_without_key_name",
        "test_evidence_output_is_restricted_to_ignored_reports_ci",
        "test_json_evidence_read_rejects_symlink_and_binds_one_file_sha",
        "test_parallel_companion_writes_remain_distinct",
        "test_empty_phase0_check_inventory_is_never_accepted",
        "test_weak_receipt_empty_suites_forgery_is_rejected",
        "test_contract_policy_is_exactly_the_required_contract_inventory",
        "test_integration_policy_is_exactly_the_required_integration_inventory",
        # RebuildPlan 4.2.3 old/new dual-run for the R0-B receipt migration.  Naming
        # these here makes the frozen migration corpus load-bearing: deleting or
        # renaming it fails this gate instead of silently shrinking coverage.
        "test_every_frozen_file_matches_its_recorded_digest",
        "test_old_schema_rejects_all_current_manifests",
        "test_new_schema_rejects_all_34_baseline_manifests",
        "test_reintroducing_the_factory_resolver_breaks_the_new_gate",
        "test_stale_receipt_is_rejected_after_a_manifest_edit",
        # The attack corpus is only evidence while these hold: the corpus file
        # cannot be neutered into no-op mutations or weakened verdicts, because the
        # expectations are pinned in test code rather than read from the corpus.
        "test_corpus_declares_exactly_the_pinned_attack_classes",
        "test_declared_expectations_match_the_pinned_verdicts",
        "test_every_rejecting_sample_actually_mutates_its_base",
        "test_pinned_attack_verdicts_hold_against_both_schemas",
        # RebuildPlan 4.2.3 requires freezing the old validator, not just the old
        # schema.  These anchor the frozen corpus to the immutable baseline commit
        # blob and prove the validator is unchanged, so reusing the current
        # validator for both dual-run sides stays sound and fail-closed.
        "test_frozen_fixtures_equal_the_baseline_commit_blobs",
        "test_receipt_validator_is_unchanged_in_this_batch",
        # Which commit those anchors bind to must come from this runner, never from
        # a file the candidate change can rewrite.  Naming the fail-closed cases
        # here keeps the external baseline authority itself load-bearing.
        "test_absent_injection_fails_closed",
        "test_empty_injection_fails_closed",
        "test_revision_expressions_are_refused",
        "test_unknown_commit_fails_closed",
        "test_a_baseline_that_does_not_descend_from_the_corpus_fails_closed",
        "test_a_baseline_older_than_the_corpus_fails_closed",
        "test_a_repo_local_planted_git_on_path_is_ignored",
        "test_provenance_repointed_at_another_ancestor_fails_closed",
        "test_the_frozen_constant_is_what_the_corpus_declares",
        "test_every_new_trust_bearing_artifact_is_byte_bound",
        # A breaking shape takes a new identity, so the URI that named the
        # pre-migration schema must keep naming it.
        "test_each_major_keeps_its_own_schema_identity",
        # Which majors exist is decided by the registry in phase0, not by whatever
        # module-manifest*.schema.json a candidate leaves on disk.
        "test_a_planted_schema_cannot_introduce_a_major_of_its_own",
        "test_a_registered_schema_may_not_relabel_its_major",
        # const alone is compared with Python equality, where 1 == True, so the v2
        # boolean fields bind ``type`` where the const lives.
        "test_v2_boolean_consts_refuse_numeric_impostors",
        "test_v2_release_eligibility_const_is_type_bound",
        # Draft 2020-12 calls 1.0 an integer; the hand-rolled evaluator must not be
        # stricter than the reference it is pinned against.
        "test_integral_floats_satisfy_integer_exactly_as_draft_2020_12_says",
        "test_the_frozen_commit_is_accepted_and_returned",
        # The injected baseline is the *current* base tip, so it moves.  Naming this
        # keeps the gate from silently regressing to an equality check that would
        # pass today and fail on the next unrelated merge.
        "test_a_base_that_has_advanced_past_the_corpus_is_still_accepted",
        # F9 now holds every signed manifest to the exact schema of its declared
        # major, using a stdlib evaluator external_gate had to hand-roll.  These
        # keep that evaluator pinned to a reference Draft 2020-12 implementation
        # instead of trusting it because it was written carefully.
        "test_the_live_corpus_is_accepted_by_both_implementations",
        "test_the_subset_evaluator_agrees_with_draft_2020_12_on_every_mutation",
        "test_the_mutations_are_real_rejections_and_not_no_ops",
        # R0-C: the candidate Release BOM validator migrated into Tools/ci
        # (RebuildPlan 4.3) -- its suite came with it and is trust-bearing for
        # the same reason the file is byte-bound: an emptied body must not
        # keep reading as green.
        "test_full_signed_bom_three_authorities_and_external_receipt_pass",
        # One static byte-for-byte composition corpus is consumed independently
        # by the ordinary Tools/ci validator and the Control Plane authority.
        # Naming the Python side here prevents a future candidate from deleting
        # the only proof that the full gate-valid tree reaches generation 2.
        "test_shared_release_binding_compat_corpus_passes_real_validator",
        "test_missing_or_tampered_external_receipt_fails_closed",
        "test_unpublished_hyphenated_trust_contract_identity_is_rejected",
        "test_worker_authority_must_match_manifest_descriptor_artifact",
        "test_route_authority_must_match_supervisor_artifact",
        "test_challenge_authority_must_match_policy_artifact",
        "test_three_runtime_key_roles_are_pairwise_distinct",
        "test_native_stop_v1_scope_or_contract_is_rejected",
        "test_timestamp_uses_100ns_exact_z_and_2020_lower_bound",
        "test_same_native_spki_cannot_move_worker_incarnation_across_boms",
        "test_artifact_provenance_and_evidence_tampering_fail_closed",
        "test_duplicate_receipt_member_and_old_mixed_shape_fail_closed",
        "test_policy_roles_and_key_purposes_are_separated",
        # R0-D retained-authority pin: the surviving Tools/ci validator remains
        # bound to the retained deployed policy after the module-side copy is
        # retired.  The operational anchor entry remains separately registered.
        "test_the_code_bound_policy_digest_matches_the_retained_policy",
        # The owner provisioned the native-stop signer in the deployed policy
        # and re-anchored the code-bound digest on 2026-07-21, so this
        # constructor tripwire is green.  It proves constructor instantiation
        # only -- the completion criterion is the end-to-end main() happy-path
        # test with an owner-signed fixture receipt.
        "test_the_operational_anchor_entry_constructs_the_release_validator",
        # TODAY-reachable behaviour of the actual CLI entry release.sh invokes.
        "test_the_cli_refuses_to_start_without_the_receipt_argument",
        "test_the_cli_reports_an_untrusted_bom_signer_as_a_structured_fail",
        # The candidate verdict and the native-stop trust receipt signer payload
        # must name the
        # exact canonical full signed BOM bytes that activation consumes.  A
        # signature over a canonical payload cannot bless a reordered or
        # trailing-LF full wire with a different digest.
        "test_candidate_and_previous_bom_require_exact_canonical_wire",
        "test_native_stop_receipt_signer_payload_builder_requires_the_exact_canonical_bom",
        "test_canonical_number_corpus_matches_cross_language_contract",
        "test_canonical_string_corpus_matches_cross_language_contract",
        "test_canonical_number_limits_and_nonfinite_builder_inputs_fail_closed",
        "test_noncanonical_receipt_wires_fail_closed_with_the_same_signature",
        "test_noncanonical_receipt_signature_base64_alias_fails_closed",
        "test_rsa_signature_representative_must_be_less_than_modulus",
        "test_unsigned_owner_receipt_fixture_is_production_builder_output",
        "test_field_sets_are_identical",
        "test_signature_field_is_pinned_on_both_sides",
        # R0-C external Release BOM signer contract: repository code receives
        # only the exact public profile and conformance vectors.  All eight
        # invariants are required inventory, not optional documentation.
        "test_01_auth_spec_is_exactly_the_r0c_deployed_rsa_pss_profile",
        "test_02_private_key_and_exact_crypto_boundaries_are_normative",
        "test_03_canonical_final_wire_is_persisted_and_reused",
        "test_04_auth_spec_and_canonicalization_corpora_are_hash_bound",
        "test_05_deployed_policy_exposes_one_public_bom_key_in_profile",
        "test_06_public_positive_vectors_prove_signed_and_stable_pair",
        "test_07_every_adversarial_vector_fails_its_bound_invariant",
        "test_08_repository_contract_artifacts_contain_no_private_key",
        # R0-C F1 completion: the end-to-end main() happy path with the
        # owner-signed native-stop-trust receipt fixture (signer provisioned
        # 2026-07-21) -- pinned so an emptied body cannot read as green.
        "test_main_happy_path_with_owner_signed_receipt_passes",
        # MF5: the baseline red ledger guard suite
        # (Tests/ci/test_baseline_red_ledger.py) protects the ledger gate
        # itself.  Naming every guard test here means deleting or renaming
        # any of them fails this gate instead of silently un-guarding the
        # ledger.
        "test_added_ci_test_file_is_intercepted_before_the_subprocess",
        "test_address_and_timestamp_drift_is_a_new_cause",
        "test_all_green_with_emptied_ledger_stays_pass",
        "test_all_green_with_no_ledger_stays_pass",
        "test_bootstrap_and_expansion_share_one_rule",
        "test_bootstrap_ledger_blocks_even_when_every_entry_matches",
        "test_bootstrap_ledger_cannot_buy_its_own_exit_zero",
        "test_cause_blind_fingerprint_can_never_be_a_match",
        "test_cause_blind_v1_document_fails_closed",
        "test_checkout_prefix_alias_is_the_only_path_rule_left",
        "test_ci_test_file_pin_equals_the_candidate_trust_path_set",
        "test_ci_test_file_set_pin_matches_the_working_tree",
        "test_committed_repository_ledger_pins_the_reseeded_fingerprints",
        "test_committed_repository_ledger_stays_strictly_valid",
        "test_count_mismatch_is_rejected",
        "test_detail_block_without_a_rule_line_is_a_parse_error",
        "test_dotnet_ambiguous_test_summary_is_a_parse_error",
        "test_dotnet_count_shape_invariants_are_rejected",
        "test_dotnet_cross_file_migration_changes_the_fingerprint",
        "test_dotnet_fingerprint_dedupes_repeated_diagnostics",
        "test_dotnet_fingerprint_pins_test_counts_and_build_marker",
        "test_dotnet_fingerprint_strips_the_given_root_prefix",
        "test_dotnet_line_number_drift_changes_the_fingerprint",
        "test_dotnet_partial_fix_is_a_mismatch_requiring_owner_reseed",
        "test_dotnet_relations_are_exact_match_or_growth_only",
        "test_dotnet_test_count_shrink_is_a_mismatch",
        "test_dropping_the_literal_pass_flag_breaks_the_release_allowlist",
        "test_duplicate_check_id_is_rejected",
        "test_duplicate_ran_summaries_are_a_parse_error",
        "test_empty_detail_block_body_is_a_parse_error",
        "test_entry_removal_is_a_shrink_and_matching_still_passes",
        "test_error_set_fingerprint_is_order_independent",
        "test_error_set_hash_swap_is_expansion",
        "test_every_hex_address_stays_in_the_digest",
        "test_every_temporary_path_segment_stays_in_the_digest",
        "test_exact_fingerprint_match_passes_with_registered_baseline",
        "test_executed_count_shrink_is_a_mismatch",
        "test_expansion_blocks_even_when_observed_failures_match",
        "test_explicit_base_equal_to_head_is_not_authoritative",
        "test_extra_failure_inside_fingerprint_fails_as_mismatch",
        "test_failure_cause_digest_normalizes_only_unstable_noise",
        "test_failure_reason_digests_must_be_sha256_hex",
        "test_failure_reasons_must_cover_exactly_the_failed_tests",
        "test_file_set_mismatch_never_enters_fingerprint_matching",
        "test_fingerprint_item_removal_is_a_shrink",
        "test_forged_detail_block_breaks_the_summary_cross_check",
        "test_frame_directory_stays_in_the_digest",
        "test_frame_shaped_line_in_a_message_never_swallows_its_detail",
        "test_fully_green_check_with_ledger_entry_is_stale_and_fails",
        "test_gate_exit_requires_literal_pass_when_flagged",
        "test_grown_fingerprint_is_expansion",
        "test_guard_suite_methods_are_pinned_into_the_gate_inventory",
        "test_infra_error_on_registered_check_fails",
        "test_inline_set_repr_order_does_not_change_the_digest",
        "test_introducing_an_empty_ledger_is_allowed",
        "test_kind_change_is_growth",
        "test_ledger_and_guard_suite_are_candidate_trust_paths",
        "test_malformed_ledger_blocks_even_an_all_green_run",
        "test_missing_baseline_commit_with_absent_ledger_fails",
        "test_missing_baseline_commit_with_inert_ledger_stays_green",
        "test_missing_baseline_commit_with_load_bearing_ledger_fails",
        "test_missing_failed_summary_is_a_parse_error",
        "test_missing_inventory_after_deleting_a_carrier_file_is_a_mismatch",
        "test_module_docstring_states_the_machine_protection_boundaries",
        "test_nested_discovery_surface_under_tests_ci_is_intercepted",
        "test_new_entry_is_expansion_and_fails_the_drift_check",
        "test_new_failure_reason_on_a_registered_test_is_a_mismatch",
        "test_normalization_prose_matches_the_surviving_rules",
        "test_optional_top_level_notes_are_validated",
        "test_ordered_lines_outside_a_set_block_keep_their_order",
        "test_overall_status_literal_is_never_pass",
        "test_parse_error_fingerprint_is_rejected",
        "test_partially_green_check_is_stale_and_fails_with_shrink_hint",
        "test_print_forged_failure_lines_outside_detail_blocks_are_ignored",
        "test_registering_the_drift_check_itself_is_rejected",
        "test_release_script_pins_the_literal_pass_flag",
        "test_removing_the_ledger_file_fails_the_drift_check",
        "test_resolve_baseline_marks_the_head_fallback_non_authoritative",
        "test_same_class_name_in_another_module_is_a_mismatch",
        "test_same_identity_with_conflicting_causes_is_a_parse_error",
        "test_same_test_failing_for_a_new_reason_is_growth",
        "test_set_difference_item_order_does_not_change_the_digest",
        "test_shrink_with_a_new_reason_on_the_survivor_is_growth",
        "test_sourceless_bytecode_under_tests_ci_is_refused",
        "test_stray_banner_parse_error_quotes_the_offending_lines",
        "test_stray_separator_cannot_truncate_a_cause",
        "test_subtest_failures_keep_distinct_identities",
        "test_suite_child_never_reads_an_in_repository_pycache",
        "test_symlinked_test_file_is_refused_whatever_its_name",
        "test_temporary_root_child_without_a_tail_stays_in_the_digest",
        "test_timestamps_stay_in_the_digest_wherever_they_appear",
        "test_unchanged_failure_reason_still_matches_through_the_pipeline",
        "test_unittest_entry_without_failure_reasons_is_rejected",
        "test_unittest_fingerprint_binds_the_failure_cause",
        "test_unittest_fingerprint_handles_setupclass_errors",
        "test_unittest_fingerprint_keeps_full_module_qualified_names",
        "test_unittest_fingerprint_records_executed_and_missing_inventory",
        "test_unittest_relation_shrinks_only_with_stable_executed_and_inventory",
        "test_unknown_fingerprint_kind_fails_closed",
        "test_unparseable_failure_log_fails_closed",
        "test_unpinned_importable_top_level_file_is_refused",
        "test_unreadable_baseline_commit_is_never_read_as_a_bootstrap",
        "test_unregistered_red_fails",
        "test_unsorted_failed_tests_are_rejected",
        "test_valid_ledger_round_trips",
        "test_workflow_baseline_expression_covers_merge_group",
        "test_wrong_schema_version_is_rejected",
    }
)


# The exact Tests/ci carrier file set, pinned inside the gate itself.  Every
# unittest fingerprint input (failed tests, executed count, missing inventory,
# the summary cross-check) is read from the merged stdout/stderr stream of the
# discovered test processes' own interpreter, so a hostile test file added to
# or edited into Tests/ci can rewrite that stream and wash a new red into a
# byte-exact registered fingerprint.  The parent gate process therefore
# enumerates Tests/ci with its own os.listdir/os.walk -- an input the child
# cannot influence -- and refuses to run (FAIL, reason TEST_FILE_SET_MISMATCH,
# never entering fingerprint matching) unless the discovered surface is
# exactly this pinned set: no extra or missing top-level test_*.py, and no
# __init__.py or nested test_*.py anywhere under Tests/ci that discovery
# could import.  This constant must stay identical to the Tests/ci test
# entries of run_candidate_gate.CANDIDATE_TRUST_PATHS (guarded by
# test_ci_test_file_pin_equals_the_candidate_trust_path_set), so changing any
# Tests/ci test file is a candidate trust-root change requiring Owner merge.
PHASE0_CI_TEST_DIRECTORY = ("Tests", "ci")
PHASE0_CI_TEST_FILE_PATTERN = "test_*.py"
PHASE0_PINNED_CI_TEST_FILES = (
    "test_baseline_red_ledger.py",
    "test_candidate_bom_validator.py",
    "test_candidate_bom_validator_e2e.py",
    "test_candidate_gate.py",
    "test_candidate_policy.py",
    "test_f2_contract_schemas.py",
    "test_legacy_sessionrunner_strangler.py",
    "test_manifest_schema_subset_evaluator.py",
    "test_module_impact.py",
    "test_phase0_gate.py",
    "test_r0b_receipt_migration_dual_run.py",
    "test_release_bom_field_set_dual_pin.py",
    "test_release_bom_signer_contract.py",
)
TEST_FILE_SET_MISMATCH_REASON = "TEST_FILE_SET_MISMATCH"

# Tests/ci is sys.path[0] for the whole discovery run, so its import surface
# is every file the import machinery is willing to load -- not just the
# test_*.py names discovery globs.  An unpinned Tests/ci/json.py shadows the
# stdlib module a pinned test imports; an unpinned Tests/ci/json.pyc does the
# same through SourcelessFileLoader.  Inert data (.json/.md/.txt) is left
# alone: no import hook loads it.
PHASE0_CI_BYTECODE_SUFFIXES = frozenset({".pyc", ".pyo"})
PHASE0_CI_IMPORTABLE_SUFFIXES = frozenset(
    {suffix.casefold() for suffix in importlib.machinery.all_suffixes()}
    | PHASE0_CI_BYTECODE_SUFFIXES
    # .pth is only consumed inside site directories and a .zip only once it
    # is on sys.path, but both are import-machinery inputs and Tests/ci has
    # no legitimate use for either, so they are refused rather than argued
    # about.
    | {".pth", ".zip"}
)
PHASE0_BYTECODE_CACHE_DIRECTORY = "__pycache__"


def _ci_test_file_set_errors(root: Path) -> List[str]:
    """Parent-process check that Tests/ci is exactly the pinned surface.

    Never reads or trusts child-process output.  Returns human-readable
    errors; any non-empty result must fail the unittest check closed with
    reason TEST_FILE_SET_MISMATCH before the suite subprocess is started.
    """

    directory = root.joinpath(*PHASE0_CI_TEST_DIRECTORY)
    errors: List[str] = []
    try:
        names = sorted(os.listdir(directory))
    except OSError as exc:
        return ["cannot enumerate " + "/".join(PHASE0_CI_TEST_DIRECTORY) + ": " + str(exc)]
    expected = sorted(PHASE0_PINNED_CI_TEST_FILES)
    observed: List[str] = []
    for name in names:
        entry = directory / name
        # Symlinks never enter the observed set: a symlink is a name that
        # does not describe its own bytes.  They are reported by the walk
        # below (which sees every depth uniformly), so this loop only has to
        # keep them out of the pinned comparison -- a pinned name backed by a
        # symlink must still read as missing.
        if entry.is_symlink() or not entry.is_file():
            continue
        suffix = Path(name).suffix.casefold()
        if fnmatch.fnmatch(name, PHASE0_CI_TEST_FILE_PATTERN):
            observed.append(name)
        elif (
            suffix in PHASE0_CI_IMPORTABLE_SUFFIXES
            # Bytecode gets the more specific "sourceless bytecode" message
            # from the walk below; do not report it twice.
            and suffix not in PHASE0_CI_BYTECODE_SUFFIXES
        ):
            errors.append("unpinned discovery surface: importable file " + name)
    for name in sorted(set(observed) - set(expected)):
        errors.append("unpinned test file present: " + name)
    for name in sorted(set(expected) - set(observed)):
        errors.append("pinned test file missing (or not a regular file): " + name)
    # unittest discovery imports package __init__.py files and recurses into
    # packages, so any __init__.py or nested test_*.py under Tests/ci is an
    # unpinned import surface even though the top-level file set matches.
    # Symlinked directories are refused outright: discovery follows them while
    # this walk (followlinks=False) cannot see what they contain.
    for current, directories, filenames in os.walk(directory):
        current_path = Path(current)
        for name in sorted(directories):
            if (current_path / name).is_symlink():
                errors.append(
                    "unpinned discovery surface: symlinked directory "
                    + (current_path / name).relative_to(directory).as_posix()
                )
        for filename in sorted(filenames):
            path = current_path / filename
            relative = path.relative_to(directory).as_posix()
            # Any symlink at any depth is refused whatever it is named.  The
            # earlier "is_file() and not is_symlink()" enumeration silently
            # dropped symlinks instead, so a symlink carrying an unpinned
            # name was neither an extra top-level file nor a nested surface
            # while discovery still imported it (the pinned-name side of the
            # same condition did fail closed, which is what hid the hole).
            # run_candidate_gate._test_tree_sha256 already refuses symlinked
            # entries outright; this is the same rule.
            if path.is_symlink():
                errors.append("unpinned discovery surface: symlinked file " + relative)
                continue
            if (
                Path(filename).suffix.casefold() in PHASE0_CI_BYTECODE_SUFFIXES
                and current_path.name != PHASE0_BYTECODE_CACHE_DIRECTORY
            ):
                # Bytecode outside __pycache__ is a SourcelessFileLoader
                # surface that sys.pycache_prefix does not govern.  Bytecode
                # inside __pycache__ is deliberately NOT refused here -- see
                # _suite_bytecode_cache_prefix for why enumeration cannot be
                # the closure for it.
                errors.append(
                    "unpinned discovery surface: sourceless bytecode " + relative
                )
                continue
            nested = filename == "__init__.py" or (
                current_path != directory
                and fnmatch.fnmatch(filename, PHASE0_CI_TEST_FILE_PATTERN)
            )
            if nested:
                errors.append("unpinned discovery surface: " + relative)
    return sorted(errors)


def _suite_bytecode_cache_prefix() -> str:
    """A fresh empty bytecode cache directory for the suite child.

    A PEP 552 unchecked-hash .pyc carries no verifiable link to its source:
    Tests/ci/__pycache__/test_x.cpython-312.pyc replaces the entire import of
    test_x.py while test_x.py stays byte-identical, so a parent that pins
    NAMES pins nothing about the BYTES that run.  Refusing __pycache__ by
    enumeration cannot be the answer -- static-ci.yml runs a discover over
    Tests/ci before this gate, so the gate would fail on its own workflow's
    honest cache.  Instead the parent redirects sys.pycache_prefix to a fresh
    empty directory outside the repository, which makes every in-repository
    __pycache__ unreachable for both reading and writing.  -B is not a
    substitute: it only stops writes.  The directory is left in place for the
    lifetime of the process so the running gate can be audited, and removed
    at interpreter exit.
    """

    prefix = tempfile.mkdtemp(prefix="phase0-suite-pycache-")
    atexit.register(shutil.rmtree, prefix, ignore_errors=True)
    return prefix


def run_phase0_unittests(baseline: Optional[str]) -> Dict[str, Any]:
    minimum_adversarial_tests = PHASE0_MINIMUM_ADVERSARIAL_TESTS
    required_inventory = PHASE0_REQUIRED_UNITTEST_INVENTORY
    started = time.monotonic()
    file_set_errors = _ci_test_file_set_errors(ROOT)
    if file_set_errors:
        return new_check(
            "phase0-adversarial-unit-tests",
            True,
            "FAIL",
            ["internal:parent-process-tests-ci-file-set-enumeration"],
            1,
            int((time.monotonic() - started) * 1000),
            "ERROR: "
            + TEST_FILE_SET_MISMATCH_REASON
            + ": Tests/ci is not the pinned carrier file set; the unittest "
            + "suite was not started and fingerprint matching is refused:\n"
            + "\n".join(" - " + message for message in file_set_errors),
            {
                "reason": TEST_FILE_SET_MISMATCH_REASON,
                "expected_test_files": sorted(PHASE0_PINNED_CI_TEST_FILES),
                "file_set_errors": file_set_errors,
            },
        )
    try:
        cache_prefix = _suite_bytecode_cache_prefix()
    except OSError as exc:
        # No isolated bytecode cache means the child could load an
        # in-repository __pycache__, so there is no run worth making.
        return new_check(
            "phase0-adversarial-unit-tests",
            True,
            "FAIL",
            ["internal:parent-process-tests-ci-file-set-enumeration"],
            1,
            int((time.monotonic() - started) * 1000),
            "ERROR: "
            + TEST_FILE_SET_MISMATCH_REASON
            + ": cannot allocate an isolated bytecode cache for the suite "
            + "child; the unittest suite was not started: "
            + str(exc),
            {
                "reason": TEST_FILE_SET_MISMATCH_REASON,
                "expected_test_files": sorted(PHASE0_PINNED_CI_TEST_FILES),
                "file_set_errors": ["cannot isolate the suite bytecode cache: " + str(exc)],
            },
        )
    command = [
        sys.executable,
        "-I",
        "-X",
        "pycache_prefix=" + cache_prefix,
        "-m",
        "unittest",
        "discover",
        "-v",
        "-s",
        "Tests/ci",
        "-p",
        "test_*.py",
    ]
    # The R0-B dual-run anchors refuse to take the baseline commit from the
    # candidate tree, so this runner supplies the one it resolved itself (--base,
    # or DPS_BASELINE_COMMIT / GITHUB_BASE_SHA via resolve_baseline).  When
    # baseline resolution failed there is no authority to pass on and the anchors
    # fail closed rather than fall back to an inherited value.
    suite_environment = dict(os.environ)
    if baseline:
        suite_environment["DPS_BASELINE_COMMIT"] = baseline
    else:
        suite_environment.pop("DPS_BASELINE_COMMIT", None)
    result = run_command(command, ROOT, timeout_seconds=180, env=suite_environment)
    check = check_from_command("phase0-adversarial-unit-tests", True, result)
    executed, summary_reason = _executed_test_count(
        TrustedInvocation(command, "python-unittest", minimum_adversarial_tests),
        result.output,
    )
    missing_inventory = sorted(
        test_name for test_name in required_inventory if test_name not in result.output
    )
    if summary_reason:
        missing_inventory.append("invalid unittest summary: " + summary_reason)
    check["details"]["required_inventory"] = sorted(required_inventory)
    return enforce_unittest_evidence(
        check,
        result,
        executed,
        minimum_adversarial_tests,
        missing_inventory,
        "Phase0 adversarial tests",
    )


def run_external_gate_unittests() -> Dict[str, Any]:
    minimum_tests = 25
    command = [
        sys.executable,
        "-I",
        "-m",
        "unittest",
        "discover",
        "-s",
        "Tools/verification/tests",
        "-p",
        "test_*.py",
    ]
    with _trusted_test_environment_scope({}) as environment:
        result = run_command(
            command,
            ROOT,
            timeout_seconds=180,
            env=environment,
        )
    check = check_from_command(
        "external-gate-adversarial-unit-tests",
        True,
        result,
        {
            "module_id": "evidence-service",
            "test_type": "unit",
            "minimum_tests": minimum_tests,
            "external_gates_executed": False,
        },
    )
    executed, summary_reason = _executed_test_count(
        TrustedInvocation(command, "python-unittest", minimum_tests),
        result.output,
    )
    return enforce_unittest_evidence(
        check,
        result,
        executed,
        minimum_tests,
        (() if summary_reason is None else ("invalid unittest summary: " + summary_reason,)),
        "external gate adversarial tests",
    )


def _within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _safe_phase0_output_path(root: Path, raw: Path, label: str) -> Path:
    root_path = root.resolve()
    path = raw if raw.is_absolute() else root_path / raw
    reports_root = root_path / "Reports" / "ci"
    try:
        lexical = Path(os.path.abspath(os.fspath(path)))
        relative_to_root = lexical.relative_to(root_path)
        relative_to_reports = lexical.relative_to(reports_root)
    except (OSError, RuntimeError, ValueError) as exc:
        raise Phase0Error(
            label + " must be written under ignored Reports/ci: " + str(exc)
        )
    if not relative_to_reports.parts:
        raise Phase0Error(label + " must name a JSON file under Reports/ci")
    for part in relative_to_reports.parts:
        if re.fullmatch(r"[a-z0-9][a-z0-9._-]*", part) is None:
            raise Phase0Error(
                label + " path components must use lowercase ASCII safe names"
            )
    current = root_path
    for part in relative_to_root.parts:
        current = current / part
        if current.is_symlink():
            raise Phase0Error(label + " path may not traverse a symlink")
    try:
        candidate = lexical.resolve(strict=False)
        allowed = reports_root.resolve(strict=False)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error(label + " path cannot be resolved: " + str(exc))
    if not _within(candidate, allowed) or candidate == allowed:
        raise Phase0Error(label + " must be written under ignored Reports/ci")
    if candidate.suffix != ".json":
        raise Phase0Error(label + " must use a lowercase .json filename")
    if candidate.name.endswith(PUBLICATION_MARKER_SUFFIX):
        raise Phase0Error(label + " may not occupy a publication marker path")
    artifacts = (
        candidate,
        _publication_marker_path(candidate),
        _publication_claim_path(candidate),
    )
    for artifact in artifacts:
        if artifact.is_symlink():
            raise Phase0Error(label + " publication paths may not be symlinks")
        if artifact.exists() and not artifact.is_file():
            raise Phase0Error(label + " publication paths must be regular files")
        relative = artifact.relative_to(root_path).as_posix()
        tracked = run_command(
            ["git", "ls-files", "--error-unmatch", "--", relative],
            root,
            timeout_seconds=30,
        )
        if tracked.exit_code == 0:
            raise Phase0Error(label + " may not overwrite a tracked publication file")
        ignored = run_command(
            ["git", "check-ignore", "--quiet", "--no-index", "--", relative],
            root,
            timeout_seconds=30,
        )
        if ignored.exit_code != 0:
            raise Phase0Error(label + " publication paths must be Git-ignored")
    return candidate


def _publication_marker_path(path: Path) -> Path:
    return path.with_name(path.name + PUBLICATION_MARKER_SUFFIX)


def _publication_claim_path(path: Path) -> Path:
    return path.with_name(path.name + PUBLICATION_CLAIM_SUFFIX)


def _publication_record(
    status: str,
    run_id: str,
    payload_name: str,
    payload_sha256: Optional[str],
    payload_size: Optional[int],
) -> Dict[str, Any]:
    record: Dict[str, Any] = {
        "schema_version": PUBLICATION_SCHEMA_VERSION,
        "status": status,
        "run_id": run_id,
        "payload_name": payload_name,
        "payload_sha256": payload_sha256,
        "payload_size": payload_size,
    }
    record["record_sha256"] = sha256_text(stable_json(record))
    return record


def _publication_json_bytes(value: Mapping[str, Any]) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    ).encode("utf-8")


def _decode_publication_record(
    payload: bytes,
    label: str,
    payload_name: str,
    allowed_statuses: set[str],
) -> Dict[str, Any]:
    try:
        marker = json.loads(payload.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise Phase0Error(label + " is invalid: " + str(exc))
    if not isinstance(marker, dict):
        raise Phase0Error(label + " must contain an object")
    expected_keys = {
        "schema_version",
        "status",
        "run_id",
        "payload_name",
        "payload_sha256",
        "payload_size",
        "record_sha256",
    }
    if set(marker) != expected_keys:
        raise Phase0Error(label + " fields are not canonical")
    supplied_record_sha = marker.get("record_sha256")
    marker_without_sha = dict(marker)
    marker_without_sha.pop("record_sha256", None)
    status = marker.get("status")
    digest = marker.get("payload_sha256")
    size = marker.get("payload_size")
    payload_binding_valid = (
        isinstance(digest, str)
        and re.fullmatch(r"[0-9a-f]{64}", digest) is not None
        and isinstance(size, int)
        and size >= 2
    )
    payload_binding_empty = digest is None and size is None
    if (
        marker.get("schema_version") != PUBLICATION_SCHEMA_VERSION
        or status not in allowed_statuses
        or not isinstance(marker.get("run_id"), str)
        or re.fullmatch(r"[0-9a-f]{32}", str(marker.get("run_id"))) is None
        or marker.get("payload_name") != payload_name
        or supplied_record_sha != sha256_text(stable_json(marker_without_sha))
        or (status == "COMMITTED" and not payload_binding_valid)
        or (
            status != "COMMITTED"
            and not payload_binding_valid
            and not payload_binding_empty
        )
    ):
        binding_kind = (
            "COMMITTED" if allowed_statuses == {"COMMITTED"} else "publication"
        )
        raise Phase0Error(
            label + " is not a valid " + binding_kind + " binding"
        )
    return marker


def _read_regular_file_at(
    directory_descriptor: int,
    name: str,
    label: str,
    maximum_bytes: int = MAX_PUBLICATION_FILE_BYTES,
) -> tuple[bytes, tuple[int, int, int]]:
    descriptor: Optional[int] = None
    try:
        flags = os.O_RDONLY
        if not hasattr(os, "O_NOFOLLOW"):
            raise OSError(
                errno.ENOTSUP,
                "committed evidence reads require O_NOFOLLOW",
            )
        flags |= os.O_NOFOLLOW
        descriptor = os.open(name, flags, dir_fd=directory_descriptor)
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode):
            raise Phase0Error(label + " is not a regular file")
        chunks: List[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, 1024 * 1024)
            if not chunk:
                break
            total += len(chunk)
            if total > maximum_bytes:
                raise Phase0Error(label + " exceeds the evidence size limit")
            chunks.append(chunk)
        final = os.fstat(descriptor)
        if (
            final.st_dev,
            final.st_ino,
            final.st_size,
        ) != (opened.st_dev, opened.st_ino, total):
            raise Phase0Error(label + " changed while it was being read")
        return b"".join(chunks), (final.st_dev, final.st_ino, final.st_size)
    except Phase0Error:
        raise
    except OSError as exc:
        raise Phase0Error("cannot safely read {0}: {1}".format(label, exc))
    finally:
        if descriptor is not None:
            os.close(descriptor)


def _open_existing_directory_no_follow(path: Path) -> int:
    if (
        not hasattr(os, "O_DIRECTORY")
        or not hasattr(os, "O_NOFOLLOW")
        or os.open not in os.supports_dir_fd
    ):
        raise OSError(
            errno.ENOTSUP,
            "committed evidence reads require directory descriptors and O_NOFOLLOW",
        )
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    descriptor = os.open(path.anchor, flags)
    try:
        for part in path.parts[1:]:
            child = os.open(part, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def _path_identity_at(
    directory_descriptor: int, name: str, label: str
) -> tuple[int, int, int]:
    try:
        current = os.stat(name, dir_fd=directory_descriptor, follow_symlinks=False)
    except OSError as exc:
        raise Phase0Error("cannot revalidate {0}: {1}".format(label, exc))
    if not stat.S_ISREG(current.st_mode):
        raise Phase0Error(label + " is no longer a regular file")
    return current.st_dev, current.st_ino, current.st_size


def _claim_absent_at(directory_descriptor: int, claim_name: str) -> None:
    try:
        os.stat(claim_name, dir_fd=directory_descriptor, follow_symlinks=False)
    except FileNotFoundError:
        return
    except OSError as exc:
        raise Phase0Error("cannot inspect evidence publication claim: " + str(exc))
    raise Phase0Error(
        "evidence publication is in progress, failed, or requires manual recovery"
    )


def _load_committed_json_object_with_sha(
    path: Path, label: str
) -> tuple[Dict[str, Any], str]:
    """Read one explicitly COMMITTED payload and its integrity marker.

    A lingering exclusive claim is always a denial, including after a crashed
    or failed writer.  That intentionally requires a new output path or manual
    review rather than treating an uncertain payload as evidence.
    """

    absolute = Path(os.path.abspath(os.fspath(path)))
    directory_descriptor: Optional[int] = None
    try:
        directory_descriptor = _open_existing_directory_no_follow(absolute.parent)
        claim_name = _publication_claim_path(absolute).name
        marker_name = _publication_marker_path(absolute).name
        _claim_absent_at(directory_descriptor, claim_name)
        marker_payload, marker_identity = _read_regular_file_at(
            directory_descriptor,
            marker_name,
            label + " publication marker",
            maximum_bytes=64 * 1024,
        )
        marker = _decode_publication_record(
            marker_payload,
            label + " publication marker",
            absolute.name,
            {"COMMITTED"},
        )

        payload, payload_identity = _read_regular_file_at(
            directory_descriptor, absolute.name, label
        )
        payload_sha = hashlib.sha256(payload).hexdigest()
        if (
            len(payload) != marker["payload_size"]
            or payload_sha != marker["payload_sha256"]
        ):
            raise Phase0Error(label + " does not match its COMMITTED integrity binding")
        try:
            value = json.loads(payload.decode("utf-8-sig"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise Phase0Error("invalid {0}: {1}".format(label, exc))
        if not isinstance(value, dict):
            raise Phase0Error(label + " must contain an object")

        _claim_absent_at(directory_descriptor, claim_name)
        if _path_identity_at(
            directory_descriptor, marker_name, label + " publication marker"
        ) != marker_identity:
            raise Phase0Error(label + " publication marker changed during read")
        if _path_identity_at(directory_descriptor, absolute.name, label) != payload_identity:
            raise Phase0Error(label + " changed during committed read")
        return value, payload_sha
    except Phase0Error:
        raise
    except OSError as exc:
        raise Phase0Error("cannot safely read {0}: {1}".format(label, exc))
    finally:
        if directory_descriptor is not None:
            os.close(directory_descriptor)


class EvidencePublication:
    """Fail-closed, single-writer publication for one logical evidence path."""

    def __init__(self, path: Path, *, run_id: Optional[str] = None):
        self.path = Path(os.path.abspath(os.fspath(path)))
        self.run_id = run_id or _new_publication_run_id()
        if re.fullmatch(r"[0-9a-f]{32}", self.run_id) is None:
            raise ValueError("evidence publication run id is invalid")
        self._directory_descriptor: Optional[int] = None
        self._claim_descriptor: Optional[int] = None
        self._staged_sha256: Optional[str] = None
        self._staged_size: Optional[int] = None
        self._committed = False
        self._aborted = False
        self._claim_released = False
        self._durability_uncertain = False

    @property
    def claim_path(self) -> Path:
        return _publication_claim_path(self.path)

    @property
    def marker_path(self) -> Path:
        return _publication_marker_path(self.path)

    def _atomic_write(self, name: str, payload: bytes) -> None:
        if self._directory_descriptor is None:
            raise RuntimeError("evidence publication has not been acquired")
        temporary_name = "publication-{0}-{1}.tmp".format(
            self.run_id, os.urandom(8).hex()
        )
        descriptor: Optional[int] = None
        try:
            descriptor = os.open(
                temporary_name,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
                dir_fd=self._directory_descriptor,
            )
            _write_all(descriptor, payload)
            os.fsync(descriptor)
            completed = descriptor
            descriptor = None
            os.close(completed)
            os.rename(
                temporary_name,
                name,
                src_dir_fd=self._directory_descriptor,
                dst_dir_fd=self._directory_descriptor,
            )
            os.fsync(self._directory_descriptor)
        finally:
            if descriptor is not None:
                os.close(descriptor)
            if self._directory_descriptor is not None:
                try:
                    os.unlink(temporary_name, dir_fd=self._directory_descriptor)
                except FileNotFoundError:
                    pass

    def _release_claim(self) -> None:
        if self._directory_descriptor is None or self._claim_released:
            return
        os.unlink(self.claim_path.name, dir_fd=self._directory_descriptor)
        try:
            os.fsync(self._directory_descriptor)
        except BaseException:
            # The unlink is visible in this process but its durability is now
            # uncertain. Re-create the quarantine claim before propagating the
            # failure so local readers and a complete evidence-directory
            # artifact both continue to fail closed.
            replacement_descriptor: Optional[int] = None
            try:
                replacement_descriptor = os.open(
                    self.claim_path.name,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=self._directory_descriptor,
                )
                claim = _publication_record(
                    "PREPARING", self.run_id, self.path.name, None, None
                )
                _write_all(
                    replacement_descriptor, _publication_json_bytes(claim)
                )
                os.fsync(replacement_descriptor)
                os.fsync(self._directory_descriptor)
            except FileExistsError:
                # A claim at this name is already a denial for every reader.
                pass
            except BaseException:
                # Preserve the original durability failure. The caller marks
                # the publication uncertain; no success may be reported.
                pass
            finally:
                if replacement_descriptor is not None:
                    try:
                        os.close(replacement_descriptor)
                    except OSError:
                        pass
            raise
        self._claim_released = True

    def _existing_marker_status(self) -> Optional[str]:
        if self._directory_descriptor is None:
            raise RuntimeError("evidence publication has not been acquired")
        try:
            marker_stat = os.stat(
                self.marker_path.name,
                dir_fd=self._directory_descriptor,
                follow_symlinks=False,
            )
        except FileNotFoundError:
            return None
        if not stat.S_ISREG(marker_stat.st_mode):
            raise Phase0Error("existing evidence publication marker is unsafe")
        payload, _ = _read_regular_file_at(
            self._directory_descriptor,
            self.marker_path.name,
            "existing evidence publication marker",
            maximum_bytes=64 * 1024,
        )
        marker = _decode_publication_record(
            payload,
            "existing evidence publication marker",
            self.path.name,
            {"PREPARING", "ABORTED", "COMMITTED"},
        )
        return str(marker["status"])

    def __enter__(self) -> "EvidencePublication":
        claim_created = False
        try:
            if not hasattr(os, "O_NOFOLLOW"):
                raise OSError(
                    errno.ENOTSUP,
                    "secure evidence publication requires O_NOFOLLOW",
                )
            self._directory_descriptor = _open_directory_no_follow(self.path.parent)
            try:
                self._claim_descriptor = os.open(
                    self.claim_path.name,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=self._directory_descriptor,
                )
            except FileExistsError as exc:
                raise BlockingIOError(
                    errno.EBUSY,
                    "another run owns this evidence path or a failed run requires recovery",
                    str(self.claim_path),
                ) from exc
            claim_created = True
            claim = _publication_record(
                "PREPARING", self.run_id, self.path.name, None, None
            )
            _write_all(self._claim_descriptor, _publication_json_bytes(claim))
            os.fsync(self._claim_descriptor)
            os.fsync(self._directory_descriptor)
            existing_status = self._existing_marker_status()
            if existing_status == "COMMITTED":
                self._release_claim()
                raise FileExistsError(
                    errno.EEXIST,
                    "committed evidence is immutable; choose a new --evidence path",
                    str(self.path),
                )
            if existing_status == "PREPARING":
                self._durability_uncertain = True
                raise Phase0Error(
                    "existing PREPARING publication requires manual recovery"
                )
            preparing = _publication_record(
                "PREPARING", self.run_id, self.path.name, None, None
            )
            self._atomic_write(
                self.marker_path.name, _publication_json_bytes(preparing)
            )
            return self
        except BaseException:
            if self._claim_descriptor is not None:
                os.close(self._claim_descriptor)
                self._claim_descriptor = None
            if self._directory_descriptor is not None:
                os.close(self._directory_descriptor)
                self._directory_descriptor = None
            # Once the exclusive claim exists, retain it on every failed
            # acquisition/publication path.  Readers therefore fail closed
            # even if a later fsync reported an uncertain outcome.
            if not claim_created:
                self._committed = False
            raise

    def stage(self, evidence: Mapping[str, Any]) -> None:
        if self._directory_descriptor is None or self._committed:
            raise RuntimeError("evidence publication is not active")
        payload = _publication_json_bytes(evidence)
        if len(payload) > MAX_PUBLICATION_FILE_BYTES:
            raise Phase0Error("evidence payload exceeds the 64 MiB limit")
        try:
            self._atomic_write(self.path.name, payload)
        except BaseException:
            self._durability_uncertain = True
            raise
        self._staged_sha256 = hashlib.sha256(payload).hexdigest()
        self._staged_size = len(payload)

    def commit(self) -> None:
        if (
            self._directory_descriptor is None
            or self._claim_descriptor is None
            or self._staged_sha256 is None
            or self._staged_size is None
            or self._committed
        ):
            raise RuntimeError("evidence publication has no staged payload")
        payload, _ = _read_regular_file_at(
            self._directory_descriptor, self.path.name, "staged evidence"
        )
        if (
            len(payload) != self._staged_size
            or hashlib.sha256(payload).hexdigest() != self._staged_sha256
        ):
            raise Phase0Error("staged evidence changed before COMMITTED publication")
        committed = _publication_record(
            "COMMITTED",
            self.run_id,
            self.path.name,
            self._staged_sha256,
            self._staged_size,
        )
        committed_payload = _publication_json_bytes(committed)
        try:
            self._atomic_write(
                self.marker_path.name, committed_payload
            )
            published_marker, _ = _read_regular_file_at(
                self._directory_descriptor,
                self.marker_path.name,
                "COMMITTED evidence marker",
                maximum_bytes=64 * 1024,
            )
            if published_marker != committed_payload:
                raise Phase0Error(
                    "COMMITTED evidence marker changed before claim release"
                )
            self._release_claim()
        except BaseException:
            self._durability_uncertain = True
            raise
        self._committed = True

    def abort(self) -> None:
        if (
            self._directory_descriptor is None
            or self._claim_descriptor is None
            or self._committed
            or self._aborted
            or self._claim_released
        ):
            return
        if self._durability_uncertain:
            raise Phase0Error(
                "uncertain publication remains quarantined for manual recovery"
            )
        aborted = _publication_record(
            "ABORTED",
            self.run_id,
            self.path.name,
            self._staged_sha256,
            self._staged_size,
        )
        aborted_payload = _publication_json_bytes(aborted)
        try:
            self._atomic_write(self.marker_path.name, aborted_payload)
            published_marker, _ = _read_regular_file_at(
                self._directory_descriptor,
                self.marker_path.name,
                "ABORTED evidence marker",
                maximum_bytes=64 * 1024,
            )
            if published_marker != aborted_payload:
                raise Phase0Error(
                    "ABORTED evidence marker changed before claim release"
                )
            self._release_claim()
        except BaseException:
            self._durability_uncertain = True
            raise
        self._aborted = True

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        abort_failure: Optional[BaseException] = None
        try:
            if (
                not self._committed
                and not self._aborted
                and not self._claim_released
                and not self._durability_uncertain
            ):
                self.abort()
        except BaseException as failure:
            abort_failure = failure
        finally:
            if self._claim_descriptor is not None:
                try:
                    os.close(self._claim_descriptor)
                except OSError:
                    pass
                self._claim_descriptor = None
            if self._directory_descriptor is not None:
                try:
                    os.close(self._directory_descriptor)
                except OSError:
                    pass
                self._directory_descriptor = None
        if abort_failure is not None and exc_type is None:
            raise abort_failure


def write_evidence(
    path: Path,
    evidence: Mapping[str, Any],
    *,
    publication: Optional[EvidencePublication] = None,
    commit: bool = True,
) -> None:
    """Publish one JSON payload, preserving the historical two-argument API."""

    if publication is not None:
        if Path(os.path.abspath(os.fspath(path))) != publication.path:
            raise Phase0Error("evidence publication path does not match its claim")
        publication.stage(evidence)
        if commit:
            publication.commit()
        return
    with EvidencePublication(path) as owned_publication:
        owned_publication.stage(evidence)
        owned_publication.commit()


def _safe_existing_path(
    root: Path,
    module_root: Path,
    raw: str,
    *,
    require_file: bool = False,
    require_directory: bool = False,
) -> Path:
    if not isinstance(raw, str) or not raw or "\\" in raw or "\x00" in raw:
        raise Phase0Error("test command contains an invalid path")
    relative_path = Path(raw)
    if relative_path.is_absolute() or any(part in ("", "..") for part in relative_path.parts):
        raise Phase0Error("test command path must be a contained repository-relative path: " + raw)
    candidate = root / relative_path
    cursor = root
    for part in relative_path.parts:
        cursor = cursor / part
        if cursor.is_symlink():
            raise Phase0Error("test command path may not traverse a symlink: " + raw)
    try:
        resolved = candidate.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("test command path is missing: {0}: {1}".format(raw, exc))
    resolved_root = root.resolve()
    resolved_module = module_root.resolve()
    if not _within(resolved, resolved_root) or not _within(resolved, resolved_module):
        raise Phase0Error("test command path escapes its module: " + raw)
    if require_file and not resolved.is_file():
        raise Phase0Error("test command requires a file: " + raw)
    if require_directory and not resolved.is_dir():
        raise Phase0Error("test command requires a directory: " + raw)
    return resolved


def _split_manifest_command(command: str) -> tuple[Dict[str, str], List[List[str]]]:
    if not isinstance(command, str) or not command.strip():
        raise Phase0Error("required suite command is missing")
    if len(command) > 8192:
        raise Phase0Error("required suite command is too long")
    without_separators = command.replace(" && ", " ")
    if "&" in without_separators or any(
        fragment in command for fragment in FORBIDDEN_COMMAND_FRAGMENTS
    ):
        raise Phase0Error("shell metacharacters are forbidden in required suite commands")
    try:
        tokens = shlex.split(command, comments=False, posix=True)
    except ValueError as exc:
        raise Phase0Error("required suite command cannot be parsed: " + str(exc))
    if not tokens:
        raise Phase0Error("required suite command is empty")
    if any(token.startswith("#") for token in tokens):
        raise Phase0Error("shell comments are forbidden in required suite commands")

    environment: Dict[str, str] = {}
    while tokens and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", tokens[0]):
        key, value = tokens.pop(0).split("=", 1)
        if key not in ALLOWED_MANIFEST_ENVIRONMENT:
            raise Phase0Error("unknown manifest test environment variable: " + key)
        if key in environment:
            raise Phase0Error("duplicate manifest test environment variable: " + key)
        environment[key] = value

    segments: List[List[str]] = []
    current: List[str] = []
    for token in tokens:
        if token == "&&":
            if not current:
                raise Phase0Error("empty command segment is forbidden")
            segments.append(current)
            current = []
            continue
        if "&" in token:
            raise Phase0Error("background or shell execution is forbidden")
        current.append(token)
    if not current:
        raise Phase0Error("required suite command ends with an empty segment")
    segments.append(current)
    if len(segments) > 2:
        raise Phase0Error("required suite command may contain at most restore then test")
    return environment, segments


def _dotnet_target(
    root: Path, module_root: Path, arguments: Sequence[str], operation: str
) -> tuple[Path, List[str]]:
    values = list(arguments)
    if operation == "test" and values[:1] == ["--project"]:
        values = values[1:]
    if not values:
        raise Phase0Error("dotnet {0} target is missing".format(operation))
    target_raw = values.pop(0)
    target = _safe_existing_path(root, module_root, target_raw)
    if target.is_dir():
        projects = [
            path
            for path in target.rglob("*.csproj")
            if path.is_file() and not path.is_symlink() and "obj" not in path.parts
        ]
        if not projects:
            raise Phase0Error("dotnet test target contains no project: " + target_raw)
    elif target.suffix.casefold() != ".csproj":
        raise Phase0Error("dotnet target must be a project or project directory: " + target_raw)
    return target, values


def _parse_dotnet_sequence(
    root: Path,
    module_root: Path,
    segments: Sequence[Sequence[str]],
    test_type: str,
    expected_test_category: Optional[str] = None,
) -> Sequence[TrustedInvocation]:
    if len(segments) != 2:
        raise Phase0Error("dotnet required suite must contain locked restore then test")
    expected_executable = "scripts/dotnet-pinned.sh"
    restore = list(segments[0])
    test = list(segments[1])
    if restore[:2] != [expected_executable, "restore"] or test[:2] != [expected_executable, "test"]:
        raise Phase0Error("only the pinned dotnet restore/test sequence is allowed")
    wrapper = root / expected_executable
    if not wrapper.is_file() or wrapper.is_symlink():
        raise Phase0Error("trusted dotnet wrapper is missing or is a symlink")
    restore_target, restore_tail = _dotnet_target(root, module_root, restore[2:], "restore")
    if restore_tail != ["--locked-mode"]:
        raise Phase0Error("dotnet restore must use only --locked-mode")
    test_target, test_tail = _dotnet_target(root, module_root, test[2:], "test")
    if restore_target != test_target:
        raise Phase0Error("dotnet restore and test targets must be identical")
    if test_tail[:3] != ["--configuration", "Release", "--no-restore"]:
        raise Phase0Error("dotnet test must use Release and --no-restore")
    runner_tail = test_tail[3:]
    if runner_tail[:1] == ["--"]:
        runner_tail = runner_tail[1:]
    if len(runner_tail) != 6:
        raise Phase0Error("dotnet test runner arguments are not the trusted fixed shape")
    category = expected_test_category or test_type.title()
    if runner_tail[0] != "--filter-trait" or runner_tail[1] != "Category=" + category:
        raise Phase0Error("dotnet test must select the declared suite category")
    if runner_tail[2] != "--minimum-expected-tests" or not runner_tail[3].isdigit():
        raise Phase0Error("dotnet test must declare a numeric minimum test count")
    minimum_tests = int(runner_tail[3])
    if minimum_tests < 1:
        raise Phase0Error("dotnet minimum expected tests must be positive")
    if runner_tail[4:] != ["--fail-skips", "on"]:
        raise Phase0Error("dotnet test must fail skipped tests")
    shell = "/bin/bash"
    return (
        TrustedInvocation(
            [
                shell,
                str(wrapper),
                "restore",
                str(restore_target),
                *_trusted_restore_flags(root),
            ],
            "restore",
            0,
        ),
        TrustedInvocation([shell, str(wrapper), *test[1:]], "dotnet-test", minimum_tests),
    )


def _audit_dotnet_test_script(
    root: Path, module_root: Path, script: Path, selected_category: str
) -> tuple[str, int]:
    lines = script.read_text(encoding="utf-8-sig").splitlines()
    expected_common_prefix = [
        "#!/usr/bin/env bash",
        "set -euo pipefail",
        "",
        'if [[ "$#" -ne 1 ]]; then',
        '  echo "Usage: $0 <Unit|Contract|Integration>" >&2',
        "  exit 64",
        "fi",
        "",
        'case "$1" in',
    ]
    expected_legacy_tail = [
        "  Unit|Contract|Integration)",
        '    suite_category="$1"',
        "    ;;",
        "  *)",
        '    echo "Unknown suite category: $1" >&2',
        "    exit 64",
        "    ;;",
        "esac",
        "",
        'repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"',
        'cd "$repo_root"',
    ]
    category_floor = 1
    if lines[:9] != expected_common_prefix:
        raise Phase0Error("module operations/test.sh is outside the trusted fixed template")
    if len(lines) == 25 and lines[9:20] == expected_legacy_tail:
        restore_line = lines[20]
        test_lines = lines[21:25]
        expected_floor_token = "1"
    elif len(lines) == 34:
        floors: Dict[str, int] = {}
        for category, offset in (("Unit", 9), ("Contract", 13), ("Integration", 17)):
            if (
                lines[offset] != "  " + category + ")"
                or lines[offset + 1] != '    suite_category="$1"'
                or lines[offset + 3] != "    ;;"
            ):
                raise Phase0Error(
                    "module operations/test.sh is outside the trusted fixed template"
                )
            match = re.fullmatch(
                r"    minimum_expected_tests=([1-9][0-9]{0,5})",
                lines[offset + 2],
            )
            if match is None:
                raise Phase0Error(
                    "module operations/test.sh has an invalid category test floor"
                )
            floors[category] = int(match.group(1))
        expected_hardened_tail = [
            "  *)",
            '    echo "Unknown suite category: $1" >&2',
            "    exit 64",
            "    ;;",
            "esac",
            "",
            'repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"',
            'cd "$repo_root"',
        ]
        if lines[21:29] != expected_hardened_tail:
            raise Phase0Error(
                "module operations/test.sh is outside the trusted fixed template"
            )
        if selected_category not in floors:
            raise Phase0Error("module test script selected an unknown category")
        category_floor = floors[selected_category]
        restore_line = lines[29]
        test_lines = lines[30:34]
        expected_floor_token = "$minimum_expected_tests"
    else:
        raise Phase0Error("module operations/test.sh is outside the trusted fixed template")
    try:
        restore = shlex.split(restore_line, comments=False, posix=True)
        test_command = " ".join(
            line.rstrip()[:-1].rstrip()
            if line.rstrip().endswith("\\")
            else line.strip()
            for line in test_lines
        )
        test = shlex.split(test_command, comments=False, posix=True)
    except ValueError as exc:
        raise Phase0Error("module operations/test.sh cannot be parsed: " + str(exc))
    if restore[:3] != ["bash", "scripts/dotnet-pinned.sh", "restore"]:
        raise Phase0Error("module operations/test.sh must call the pinned restore wrapper")
    if test[:3] != ["bash", "scripts/dotnet-pinned.sh", "test"]:
        raise Phase0Error("module operations/test.sh must call the pinned test wrapper")
    restore_target, restore_tail = _dotnet_target(root, module_root, restore[3:], "restore")
    test_target, test_tail = _dotnet_target(root, module_root, test[3:], "test")
    if restore_target != test_target or restore_tail != ["--locked-mode"]:
        raise Phase0Error("module test script restore/test targets or lock mode differ")
    expected_test_tail = [
        "--configuration",
        "Release",
        "--no-restore",
        "--",
        "--filter-trait",
        "Category=$suite_category",
        "--minimum-expected-tests",
        expected_floor_token,
        "--fail-skips",
        "on",
    ]
    if test_tail != expected_test_tail:
        raise Phase0Error(
            "module test script must use the fixed category-isolated Release test arguments"
        )
    return restore[3], category_floor


def _parse_python_invocation(
    root: Path,
    module_root: Path,
    segment: Sequence[str],
    test_type: str,
) -> TrustedInvocation:
    python_prefix = [sys.executable, "-I"]
    values = list(segment)
    if not values:
        raise Phase0Error("only the pinned Phase0 Python interpreter is allowed")
    executable = values.pop(0)
    repository_venv = executable == REPOSITORY_VENV_PYTHON
    if repository_venv:
        expected_executable = Path(
            os.path.abspath(os.fspath(ROOT / REPOSITORY_VENV_PYTHON))
        )
        actual_executable = Path(os.path.abspath(sys.executable))
        expected_prefix = Path(os.path.abspath(os.fspath(ROOT / ".venv")))
        actual_prefix = Path(os.path.abspath(sys.prefix))
        if (
            actual_executable != expected_executable
            or actual_prefix != expected_prefix
            or sys.prefix == sys.base_prefix
            or tuple(sys.version_info[:3]) != REQUIRED_PYTHON
        ):
            raise Phase0Error(
                "repository .venv command requires the active pinned repository interpreter"
            )
        if values[:1] != ["-I"]:
            raise Phase0Error("repository .venv Python command must declare isolated mode")
        values.pop(0)
    elif executable not in PYTHON_NAMES:
        raise Phase0Error("only the pinned Phase0 Python interpreter is allowed")
    if values[:3] == ["-m", "unittest", "discover"]:
        if len(values) != 7 or values[3] != "-s" or values[5] != "-p":
            raise Phase0Error("unittest discovery must use the fixed -s/-p shape")
        test_directory = _safe_existing_path(
            root, module_root, values[4], require_directory=True
        )
        pattern = values[6]
        if (
            "/" in pattern
            or "\\" in pattern
            or re.fullmatch(r"test_[A-Za-z0-9_.\-*]+\.py", pattern) is None
        ):
            raise Phase0Error("unittest discovery pattern is unsafe")
        discovered = [
            path
            for path in test_directory.glob(pattern)
            if path.is_file() and not path.is_symlink()
        ]
        if not discovered:
            raise Phase0Error("unittest discovery currently matches zero test files")
        return TrustedInvocation(
            [*python_prefix, "-m", "unittest", "discover", "-s", str(test_directory), "-p", pattern],
            "python-unittest",
            1,
        )
    if values[:2] == ["-m", "unittest"]:
        if len(values) != 3:
            raise Phase0Error("direct unittest invocation must name exactly one test file")
        test_file = _safe_existing_path(root, module_root, values[2], require_file=True)
        if test_file.suffix.casefold() != ".py":
            raise Phase0Error("direct unittest target must be a Python test file")
        return TrustedInvocation(
            [
                *python_prefix,
                "-m",
                "unittest",
                "discover",
                "-s",
                str(test_file.parent),
                "-p",
                test_file.name,
            ],
            "python-unittest",
            1,
        )
    if values[:2] == ["-m", "json.tool"]:
        raise Phase0Error(
            "json.tool checks syntax only and is not semantic contract test evidence"
        )
    if not values:
        raise Phase0Error("direct Python suite target is missing")
    direct_values = list(values)
    script_raw = direct_values.pop(0)
    if script_raw.replace("\\", "/") == "Tools/ci/run_phase0_gate.py":
        raise Phase0Error("recursive Phase0 suite command is forbidden")
    script = _safe_existing_path(root, module_root, script_raw, require_file=True)
    if (
        repository_venv
        and not direct_values
        and script.parent == (module_root / "tests").resolve()
        and re.fullmatch(r"test_[A-Za-z0-9_.\-]+\.py", script.name) is not None
    ):
        return TrustedInvocation(
            [
                *python_prefix,
                "-m",
                "unittest",
                "discover",
                "-s",
                str(script.parent),
                "-p",
                script.name,
            ],
            "python-unittest",
            1,
        )
    if test_type != "static":
        raise Phase0Error("direct Python scripts are allowed only for static suites")
    if script.suffix.casefold() != ".py":
        raise Phase0Error("static Python suite target must be a .py file")
    if direct_values not in ([], ["--root", "."]):
        raise Phase0Error("static Python suite arguments are not allowlisted")
    return TrustedInvocation(
        [*python_prefix, str(script), *direct_values], "static-json", 1
    )


def parse_manifest_suite_command(
    root: Path,
    module_root: Path,
    module_id: str,
    suite: Mapping[str, Any],
    allowed_test_types: Sequence[str] = ("static", "unit"),
    expected_evidence_level: str = VERIFICATION_LEVEL,
    expected_test_category: Optional[str] = None,
) -> TrustedSuitePlan:
    suite_id = suite.get("id")
    test_type = suite.get("type")
    evidence_level = suite.get("evidenceLevel")
    declared = suite.get("command")
    if not isinstance(suite_id, str) or not suite_id:
        raise Phase0Error("required suite id is missing")
    allowed = tuple(allowed_test_types)
    if test_type not in allowed:
        raise Phase0Error(
            "required suite type {0!r} is not one of: {1}".format(
                test_type, ", ".join(allowed)
            )
        )
    if evidence_level != expected_evidence_level:
        raise Phase0Error(
            "required suite evidenceLevel must be exactly {0}".format(
                expected_evidence_level
            )
        )
    environment, segments = _split_manifest_command(declared)

    resolved_environment: Dict[str, str] = {}
    for key, value in environment.items():
        if key == "PYTHONPATH":
            resolved_environment[key] = str(
                _safe_existing_path(root, module_root, value, require_directory=True)
            )

    executable = segments[0][0] if segments and segments[0] else ""
    if executable == "scripts/dotnet-pinned.sh":
        invocations = _parse_dotnet_sequence(
            root,
            module_root,
            segments,
            test_type,
            expected_test_category=expected_test_category,
        )
    elif executable in PYTHON_NAMES or executable == REPOSITORY_VENV_PYTHON:
        if len(segments) != 1:
            raise Phase0Error("Python suites cannot use compound shell commands")
        invocations = (
            _parse_python_invocation(root, module_root, segments[0], test_type),
        )
    elif executable == "bash":
        if len(segments) != 1 or len(segments[0]) != 3:
            raise Phase0Error(
                "bash suite must name its audited operations/test.sh and exactly one category"
            )
        script = _safe_existing_path(
            root, module_root, segments[0][1], require_file=True
        )
        expected = module_root / "operations" / "test.sh"
        if script != expected.resolve() or script.is_symlink():
            raise Phase0Error("bash suite may only run its own operations/test.sh")
        category = segments[0][2]
        expected_category = expected_test_category or test_type.title()
        if category != expected_category or category not in {
            "Unit",
            "Contract",
            "Integration",
        }:
            raise Phase0Error(
                "bash suite category must exactly match the declared suite type"
            )
        test_target, script_minimum_tests = _audit_dotnet_test_script(
            root, module_root, script, category
        )
        wrapper = root / "scripts" / "dotnet-pinned.sh"
        if not wrapper.is_file() or wrapper.is_symlink():
            raise Phase0Error("trusted dotnet wrapper is missing or is a symlink")
        # The manifest may retain a conservative operations/test.sh entry, but
        # Phase0 does not execute that shell file.  It extracts the audited
        # project target and runs the hardened argv directly so the declared
        # category cannot be satisfied by a different remaining category.
        invocations = (
            TrustedInvocation(
                [
                    "/bin/bash",
                    str(wrapper),
                    "restore",
                    str(test_target),
                    *_trusted_restore_flags(root),
                ],
                "restore",
                0,
            ),
            TrustedInvocation(
                [
                    "/bin/bash",
                    str(wrapper),
                    "test",
                    test_target,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    "--",
                    "--filter-trait",
                    "Category=" + expected_category,
                    "--minimum-expected-tests",
                    str(script_minimum_tests),
                    "--fail-skips",
                    "on",
                ],
                "dotnet-test",
                script_minimum_tests,
            ),
        )
    else:
        raise Phase0Error("unknown or untrusted required suite executable: " + executable)

    return TrustedSuitePlan(
        module_id=module_id,
        suite_id=suite_id,
        test_type=test_type,
        evidence_level=evidence_level,
        declared_command=declared,
        environment=resolved_environment,
        invocations=invocations,
    )


@functools.lru_cache(maxsize=1)
def _trusted_dotnet_executable() -> Optional[Path]:
    probe_environment = {
        "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
    }
    for candidate in _trusted_dotnet_candidates():
        try:
            resolved = candidate.resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if not resolved.is_file() or not os.access(str(resolved), os.X_OK):
            continue
        probe = run_command(
            [str(resolved), "--version"],
            ROOT,
            timeout_seconds=30,
            env=probe_environment,
        )
        if probe.exit_code == 0 and probe.output.strip() == REQUIRED_DOTNET_SDK:
            return resolved
    return None


def _trusted_account_home() -> Optional[Path]:
    """Resolve the OS account home without consulting injectable env values."""

    if os.name != "posix":
        return None
    try:
        import pwd

        value = pwd.getpwuid(os.getuid()).pw_dir
        home = Path(value).resolve(strict=True)
    except (ImportError, KeyError, OSError, RuntimeError):
        return None
    return home if home.is_dir() else None


def _trusted_dotnet_candidates() -> Sequence[Path]:
    """Return fixed candidates; never select the .NET host from ambient PATH/HOME."""

    candidates: List[Path] = []
    home = _trusted_account_home()
    if home is not None:
        candidates.append(home / ".dotnet" / "dotnet")
    candidates.extend(
        [
            Path("/usr/local/share/dotnet/dotnet"),
            Path("/usr/share/dotnet/dotnet"),
            Path("/opt/homebrew/share/dotnet/dotnet"),
            Path("/opt/hostedtoolcache/dotnet")
            / REQUIRED_DOTNET_SDK
            / "x64"
            / "dotnet",
            Path("/opt/hostedtoolcache/dotnet")
            / REQUIRED_DOTNET_SDK
            / "arm64"
            / "dotnet",
        ]
    )
    return tuple(candidates)


def _locked_node_version() -> str:
    lock_path = ROOT / "toolchain.lock.json"
    if not lock_path.is_file() or lock_path.is_symlink():
        raise Phase0Error("toolchain.lock.json is missing or unsafe")
    try:
        payload = json.loads(lock_path.read_text(encoding="utf-8"))
        version = payload["node"]["version"]
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError) as exc:
        raise Phase0Error("toolchain.lock.json has no valid Node pin") from exc
    if not isinstance(version, str) or re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version) is None:
        raise Phase0Error("toolchain.lock.json has no valid Node pin")
    locked_version = "v" + version
    if locked_version != REQUIRED_NODE_VERSION:
        raise Phase0Error(
            "toolchain.lock.json Node pin disagrees with the Phase 0 policy"
        )
    return locked_version


def _trusted_node_candidates(locked_version: str) -> Sequence[Path]:
    """Return auditable candidates; never derive an executable from PATH."""

    version = locked_version.removeprefix("v")
    candidates: List[Path] = []
    home = _trusted_account_home()
    if home is not None:
        candidates.extend(
            [
                home / ".local" / "bin" / "node",
                home / ".nvm" / "versions" / "node" / ("v" + version) / "bin" / "node",
                home
                / ".cache"
                / "codex-runtimes"
                / "codex-primary-runtime"
                / "dependencies"
                / "node"
                / "bin"
                / "node",
            ]
        )
    candidates.extend(
        [
            Path("/Applications/ChatGPT.app/Contents/Resources/cua_node/bin/node"),
            Path("/opt/hostedtoolcache/node") / version / "x64" / "bin" / "node",
            Path("/opt/hostedtoolcache/node") / version / "arm64" / "bin" / "node",
        ]
    )
    return tuple(candidates)


@functools.lru_cache(maxsize=1)
def _trusted_node_executable() -> Optional[Path]:
    locked_version = _locked_node_version()
    probe_environment = {
        "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
        "PYTHONDONTWRITEBYTECODE": "1",
    }
    for candidate in _trusted_node_candidates(locked_version):
        try:
            resolved = candidate.resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if not resolved.is_file() or not os.access(str(resolved), os.X_OK):
            continue
        probe = run_command(
            [str(resolved), "--version"],
            ROOT,
            timeout_seconds=30,
            env=probe_environment,
        )
        if probe.exit_code == 0 and probe.output.strip() == locked_version:
            return resolved
    return None


def _trusted_restore_flags(root: Path) -> Sequence[str]:
    config = root / "NuGet.Config"
    if not config.is_file() or config.is_symlink():
        raise Phase0Error("trusted NuGet.Config is missing or unsafe")
    return (
        "--locked-mode",
        "--configfile",
        str(config.resolve()),
        "-p:RestoreUseStaticGraphEvaluation=true",
        "-p:NuGetAudit=true",
        "-p:NuGetAuditMode=all",
        "-p:TreatWarningsAsErrors=true",
        "-p:MSBuildEnableWorkloadResolver=false",
    )


def _restore_failure_is_infrastructure(exit_code: int, output: str) -> bool:
    if exit_code in (124, 127):
        return True
    normalized = output.casefold()
    infrastructure_markers = (
        "cssm_moduleload()",
        "unable to load the service index",
        "temporary failure in name resolution",
        "name or service not known",
        "network is unreachable",
        "connection refused",
        "no such host is known",
        "the ssl connection could not be established",
        "no space left on device",
    )
    return any(marker in normalized for marker in infrastructure_markers)


def _trusted_state_parent() -> Path:
    candidates = [Path("/private/tmp"), Path("/tmp")]
    if os.name != "posix":
        candidates = [Path(tempfile.gettempdir())]
    for candidate in candidates:
        try:
            if candidate.is_symlink():
                continue
            resolved = candidate.resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if resolved.is_dir():
            return resolved
    raise Phase0Error("no trusted temporary-state parent is available")


def _trusted_test_environment(
    extra: Mapping[str, str], state_root: Path
) -> Dict[str, str]:
    try:
        resolved_state = state_root.resolve(strict=True)
        state_stat = state_root.stat(follow_symlinks=False)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("trusted test state root is unavailable") from exc
    if state_root.is_symlink() or not resolved_state.is_dir():
        raise Phase0Error("trusted test state root is unsafe")
    if os.name == "posix" and (
        state_stat.st_uid != os.getuid() or state_stat.st_mode & 0o077
    ):
        raise Phase0Error("trusted test state root ownership or permissions are unsafe")

    path_entries = [str(Path(sys.executable).resolve().parent), "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
    dotnet_executable = _trusted_dotnet_executable()
    if dotnet_executable is not None:
        path_entries.insert(0, str(dotnet_executable.parent))
    node_executable = _trusted_node_executable()
    if node_executable is None:
        raise Phase0Error(
            "trusted Node {0} executable is unavailable at an approved fixed path".format(
                _locked_node_version().removeprefix("v")
            )
        )
    path_entries.insert(0, str(node_executable.parent))
    environment: Dict[str, str] = {
        "PATH": os.pathsep.join(dict.fromkeys(path_entries)),
        "PYTHONDONTWRITEBYTECODE": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "0",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true",
        "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER": "1",
        "DOTNET_CLI_UI_LANGUAGE": "en-US",
        "DOTNET_NOLOGO": "1",
        "MSBUILDDISABLENODEREUSE": "1",
        "MSBUILDUSESERVER": "0",
        "MSBuildEnableWorkloadResolver": "false",
        "TESTINGPLATFORM_TELEMETRY_OPTOUT": "1",
        "DPS_NODE": str(node_executable),
        "HOME": str(resolved_state / "home"),
        "DOTNET_CLI_HOME": str(resolved_state / "dotnet-home"),
        "NUGET_HTTP_CACHE_PATH": str(resolved_state / "nuget-http-cache"),
        "NUGET_SCRATCH": str(resolved_state / "nuget-scratch"),
        "TMPDIR": str(resolved_state / "tmp"),
        "LANG": "C",
        "LC_ALL": "C",
    }
    account_home = _trusted_account_home()
    if account_home is not None:
        package_cache = account_home / ".nuget" / "packages"
        if package_cache.is_symlink():
            raise Phase0Error("trusted NuGet package cache cannot be a symlink")
        environment["NUGET_PACKAGES"] = str(package_cache)
    if dotnet_executable is not None:
        environment["DPS_DOTNET"] = str(dotnet_executable)
    if sys.platform == "darwin":
        environment["DOTNET_NUGET_SIGNATURE_VERIFICATION"] = "false"
    for sandbox_key in ("CODEX_SANDBOX", "CODEX_SANDBOX_NETWORK_DISABLED"):
        sandbox_value = os.environ.get(sandbox_key)
        if sandbox_value:
            environment[sandbox_key] = sandbox_value
    unknown = sorted(set(extra).difference(ALLOWED_TRUSTED_EXECUTOR_ENVIRONMENT))
    if unknown:
        raise Phase0Error(
            "unknown trusted test environment override: " + ", ".join(unknown)
        )
    environment.update(extra)
    return environment


@contextlib.contextmanager
def _trusted_test_environment_scope(
    extra: Mapping[str, str],
):
    state_root = Path(
        tempfile.mkdtemp(prefix="dps-phase0-executor-", dir=str(_trusted_state_parent()))
    )
    try:
        os.chmod(state_root, 0o700)
        for child in (
            "home",
            "dotnet-home",
            "nuget-http-cache",
            "nuget-scratch",
            "tmp",
        ):
            path = state_root / child
            path.mkdir(mode=0o700)
        yield _trusted_test_environment(extra, state_root)
    finally:
        try:
            if state_root.is_symlink():
                state_root.unlink(missing_ok=True)
            else:
                shutil.rmtree(state_root, ignore_errors=True)
        except OSError:
            pass


def _absolute_path_has_symlink_component(path: Path) -> bool:
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current = current / part
        if current.is_symlink():
            return True
    return False


def _validated_legacy_anchor_path(root: Path, raw: str) -> Path:
    if not isinstance(raw, str) or not raw:
        raise Phase0Error("trusted legacy baseline anchor is missing")
    anchor = Path(raw)
    if not anchor.is_absolute():
        raise Phase0Error("trusted legacy baseline anchor path must be absolute")
    if _absolute_path_has_symlink_component(anchor):
        raise Phase0Error(
            "trusted legacy baseline anchor path and parents may not be symlinks"
        )
    try:
        resolved = anchor.resolve(strict=True)
        anchor_stat = resolved.stat(follow_symlinks=False)
        parent_stat = resolved.parent.stat(follow_symlinks=False)
        repository_root = root.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error(
            "trusted legacy baseline anchor is unavailable: " + str(exc)
        )
    if _within(resolved, repository_root):
        raise Phase0Error(
            "trusted legacy baseline anchor must be outside the repository"
        )
    if not stat.S_ISREG(anchor_stat.st_mode):
        raise Phase0Error("trusted legacy baseline anchor must be a regular file")
    if anchor_stat.st_mode & 0o222:
        raise Phase0Error("trusted legacy baseline anchor must be read-only")
    verifier_uid = os.geteuid() if hasattr(os, "geteuid") else None
    if verifier_uid is None:
        raise Phase0Error(
            "trusted legacy baseline anchor ownership is unsupported"
        )
    if anchor_stat.st_uid == verifier_uid or parent_stat.st_uid == verifier_uid:
        raise Phase0Error(
            "trusted legacy baseline anchor must be owned by another identity"
        )
    if os.access(str(resolved.parent), os.W_OK):
        raise Phase0Error(
            "trusted legacy baseline anchor parent must not be writable"
        )
    return resolved


def _trusted_suite_plan_with_ambient_environment(
    root: Path,
    plan: TrustedSuitePlan,
    ambient: Optional[Mapping[str, str]] = None,
) -> TrustedSuitePlan:
    if (
        plan.module_id != LEGACY_BASELINE_MODULE_ID
        or plan.suite_id != LEGACY_BASELINE_SUITE_ID
    ):
        return plan
    if LEGACY_BASELINE_ANCHOR_ENV in plan.environment:
        raise Phase0Error(
            "legacy baseline anchor may not be declared by a module manifest"
        )
    source = os.environ if ambient is None else ambient
    anchor = _validated_legacy_anchor_path(
        root, source.get(LEGACY_BASELINE_ANCHOR_ENV, "")
    )
    environment = dict(plan.environment)
    environment[LEGACY_BASELINE_ANCHOR_ENV] = str(anchor)
    return replace(plan, environment=environment)


def run_locked_solution_build(root: Path, timeout_seconds: int = 600) -> Dict[str, Any]:
    started = time.monotonic()
    solution = root / "Dps.slnx"
    wrapper = root / "scripts" / "dotnet-pinned.sh"
    details: Dict[str, Any] = {
        "module_id": "evidence-service",
        "test_id": "solution-locked-restore-build",
        "test_type": "static",
        "evidence_level": VERIFICATION_LEVEL,
    }
    if not solution.is_file() or solution.is_symlink():
        return new_check(
            "solution-locked-restore-build",
            True,
            "FAIL",
            None,
            1,
            0,
            "ERROR: Dps.slnx is missing or unsafe",
            details,
        )
    if not wrapper.is_file() or wrapper.is_symlink():
        return new_check(
            "solution-locked-restore-build",
            True,
            "INFRA_ERROR",
            None,
            127,
            0,
            "ERROR: scripts/dotnet-pinned.sh is missing or unsafe",
            details,
        )
    try:
        import xml.etree.ElementTree as element_tree

        document = element_tree.parse(str(solution))
        declared = {
            element.get("Path")
            for element in document.findall(".//Project")
            if isinstance(element.get("Path"), str)
        }
    except Exception as exc:
        return new_check(
            "solution-locked-restore-build",
            True,
            "FAIL",
            None,
            1,
            0,
            "ERROR: Dps.slnx cannot be parsed: " + str(exc),
            details,
        )
    actual = {
        path.relative_to(root).as_posix()
        for module_root in discover_registered_module_dirs(root)
        for path in module_root.rglob("*.csproj")
        if path.is_file()
        and not path.is_symlink()
        and "bin" not in path.parts
        and "obj" not in path.parts
    }
    missing = sorted(actual.difference(declared))
    unknown = sorted(declared.difference(actual))
    if missing or unknown:
        message = []
        if missing:
            message.append("projects omitted from solution: " + ", ".join(missing))
        if unknown:
            message.append("solution declares missing/unknown projects: " + ", ".join(unknown))
        return new_check(
            "solution-locked-restore-build",
            True,
            "FAIL",
            None,
            1,
            0,
            "ERROR: " + "; ".join(message),
            details,
        )
    commands = (
        [
            "/bin/bash",
            str(wrapper),
            "restore",
            "Dps.slnx",
            *_trusted_restore_flags(root),
            "--disable-parallel",
        ],
        [
            "/bin/bash",
            str(wrapper),
            "build",
            "Dps.slnx",
            "--configuration",
            "Release",
            "--no-restore",
            "--maxcpucount:1",
        ],
    )
    output: List[str] = []
    effective: List[str] = []
    exit_code = 0
    failed_restore = False
    with _trusted_test_environment_scope({}) as environment:
        for index, command in enumerate(commands):
            effective.extend((["&&"] if effective else []) + command)
            result = run_command(command, root, timeout_seconds=timeout_seconds, env=environment)
            output.append(
                "--- locked solution segment {0} ---\n{1}".format(index + 1, result.output)
            )
            exit_code = result.exit_code
            failed_restore = index == 0 and exit_code != 0
            if exit_code != 0:
                break
    status = "PASS"
    if failed_restore and _restore_failure_is_infrastructure(exit_code, "\n".join(output)):
        status = "INFRA_ERROR"
    elif exit_code in (124, 127):
        status = "INFRA_ERROR"
    elif exit_code != 0:
        status = "FAIL"
    details.update(
        {
            "solution": "Dps.slnx",
            "solution_sha256": sha256_file(solution),
            "project_count": len(actual),
        }
    )
    return new_check(
        "solution-locked-restore-build",
        True,
        status,
        effective,
        exit_code,
        int((time.monotonic() - started) * 1000),
        "\n".join(output),
        details,
    )


def _forbidden_output_reason(output: str) -> Optional[str]:
    for match in re.finditer(
        r"(?im)(?:^|[\[{,]\s*)[\"']?(?:status|outcome)[\"']?\s*[:=]\s*[\"']?(SKIP|PARTIAL|NOT_RUN|INFRA_ERROR|NOT_APPLICABLE|FAIL)",
        output,
    ):
        return "required test reported forbidden outcome " + match.group(1).upper()
    for match in re.finditer(r"(?im)^\s*(?:skipped|skip)\s*[:=]\s*([0-9]+)\b", output):
        if int(match.group(1)) > 0:
            return "required test reported skipped tests"
    for match in re.finditer(r"(?i)\bskipped\s*=\s*([0-9]+)\b", output):
        if int(match.group(1)) > 0:
            return "required test reported skipped tests"
    for label, pattern in (
        ("expected failures", r"(?i)\bexpected\s+failures\s*=\s*([0-9]+)\b"),
        ("unexpected successes", r"(?i)\bunexpected\s+successes\s*=\s*([0-9]+)\b"),
    ):
        for match in re.finditer(pattern, output):
            if int(match.group(1)) > 0:
                return "required test reported " + label
    for match in re.finditer(r"(?im)^\s*failed\s*[:=]\s*([0-9]+)\b", output):
        if int(match.group(1)) > 0:
            return "required test reported failures despite exit zero"
    if re.search(r"(?im)^\s*(?:\[(?:SKIP|PARTIAL|NOT_RUN)\]|SKIP|PARTIAL|NOT_RUN)\s*$", output):
        return "required test emitted a non-PASS outcome"
    return None


def enforce_unittest_evidence(
    check: Dict[str, Any],
    result: CommandResult,
    executed: int,
    minimum_tests: int,
    missing_inventory: Sequence[str],
    label: str,
) -> Dict[str, Any]:
    check["details"]["executed_tests"] = executed
    check["details"]["minimum_tests"] = minimum_tests
    check["details"]["missing_inventory"] = list(missing_inventory)
    problems: List[str] = []
    if executed < minimum_tests:
        problems.append(
            "required at least {0}, ran {1}".format(minimum_tests, executed)
        )
    if missing_inventory:
        problems.append("missing inventory: " + ", ".join(missing_inventory))
    forbidden = _forbidden_output_reason(result.output)
    if forbidden:
        problems.append(forbidden)
    if result.exit_code == 0 and problems:
        check["status"] = "FAIL"
        check["exit_code"] = 1
        check["log"] += "ERROR: {0} are not eligible evidence: {1}\n".format(
            label, "; ".join(problems)
        )
        check["log_sha256"] = sha256_text(check["log"])
    return check


def _executed_test_count(invocation: TrustedInvocation, output: str) -> tuple[int, Optional[str]]:
    forbidden = _forbidden_output_reason(output)
    if forbidden:
        return 0, forbidden
    if invocation.kind == "python-unittest":
        matches = re.findall(r"(?m)^Ran ([0-9]+) tests?\b", output)
        if len(matches) != 1:
            return 0, "unittest must emit exactly one canonical Ran summary"
        canonical_tail = re.search(
            r"(?ms)(?:^|\r?\n)Ran [0-9]+ tests? in [^\r\n]+\r?\n\r?\nOK(?: \([^\r\n]+\))?\s*\Z",
            output,
        )
        if canonical_tail is None:
            return 0, "unittest output does not end with one canonical success summary"
        count = int(matches[0])
        return count, None if count >= invocation.minimum_tests else "unittest executed zero or fewer than the required tests"
    if invocation.kind == "dotnet-test":
        matches = re.findall(r"(?im)^\s*total:\s*([0-9]+)\s*$", output)
        if len(matches) != 1:
            return 0, "dotnet test must emit exactly one total summary"
        count = int(matches[0])
        return count, None if count >= invocation.minimum_tests else "dotnet test executed zero or fewer than the required tests"
    if invocation.kind == "static-json":
        try:
            payload = json.loads(output.strip())
        except (TypeError, json.JSONDecodeError):
            return 0, "static suite did not emit one structured JSON result"
        if not isinstance(payload, Mapping):
            return 0, "static suite result must be a JSON object"
        status_shape = (
            payload.get("status") == "PASS"
            and payload.get("test_type") == "static"
            and payload.get("verification_level") == VERIFICATION_LEVEL
        )
        baseline_shape = (
            payload.get("ok") is True
            and isinstance(payload.get("schema_version"), str)
            and str(payload.get("schema_version")).startswith("dps.")
            and "STATIC" in str(payload.get("scope", "")).upper()
            and payload.get("errors") == []
            and any(
                isinstance(value, int) and not isinstance(value, bool) and value > 0
                for key, value in payload.items()
                if key.endswith("_count")
            )
        )
        if not (status_shape or baseline_shape):
            return 0, "static suite did not emit an eligible structured PASS result"
        return 1, None
    return 0, "trusted suite plan has no executable test phase"


def execute_manifest_suite(root: Path, plan: TrustedSuitePlan, timeout_seconds: int = 300) -> Dict[str, Any]:
    started = time.monotonic()
    combined_output: List[str] = []
    effective: List[str] = []
    final_exit = 0
    final_kind: Optional[TrustedInvocation] = None
    final_test_output = ""
    failed_restore = False
    with _trusted_test_environment_scope(plan.environment) as environment:
        for index, invocation in enumerate(plan.invocations):
            effective.extend((["&&"] if effective else []) + list(invocation.argv))
            result = run_command(
                invocation.argv,
                root,
                timeout_seconds=timeout_seconds,
                env=environment,
            )
            combined_output.append(
                "--- trusted segment {0}: {1} ---\n{2}".format(
                    index + 1, invocation.kind, result.output
                )
            )
            final_exit = result.exit_code
            failed_restore = invocation.kind == "restore" and result.exit_code != 0
            if result.exit_code != 0:
                break
            if invocation.kind != "restore":
                final_kind = invocation
                final_test_output = result.output
    output = "\n".join(combined_output)
    details: Dict[str, Any] = {
        "module_id": plan.module_id,
        "test_id": plan.suite_id,
        "test_type": plan.test_type,
        "evidence_level": plan.evidence_level,
        "declared_command": plan.declared_command,
        "declared_command_sha256": sha256_text(plan.declared_command),
        "effective_command": effective,
        "executed_tests": 0,
    }
    python_invocations = [
        invocation
        for invocation in plan.invocations
        if invocation.argv
        and Path(os.path.abspath(os.fspath(invocation.argv[0])))
        == Path(os.path.abspath(sys.executable))
    ]
    if python_invocations:
        details["python_interpreter"] = {
            "path": os.path.abspath(sys.executable),
            "sha256": sha256_file(Path(sys.executable)),
            "isolated": all("-I" in invocation.argv for invocation in python_invocations),
        }
    if LEGACY_BASELINE_ANCHOR_ENV in plan.environment:
        anchor_path = Path(plan.environment[LEGACY_BASELINE_ANCHOR_ENV])
        anchor_stat = anchor_path.stat(follow_symlinks=False)
        parent_stat = anchor_path.parent.stat(follow_symlinks=False)
        details["trusted_external_anchor"] = {
            "environment_key": LEGACY_BASELINE_ANCHOR_ENV,
            "path": str(anchor_path),
            "sha256": sha256_file(anchor_path),
            "size": anchor_path.stat().st_size,
            "owner_uid": anchor_stat.st_uid,
            "mode": stat.S_IMODE(anchor_stat.st_mode),
            "parent_owner_uid": parent_stat.st_uid,
            "parent_mode": stat.S_IMODE(parent_stat.st_mode),
            "verifier_uid": os.geteuid() if hasattr(os, "geteuid") else None,
        }
    status = "PASS"
    if failed_restore and _restore_failure_is_infrastructure(final_exit, output):
        status = "INFRA_ERROR"
    elif final_exit == 124 or final_exit == 127:
        status = "INFRA_ERROR"
    elif final_exit != 0:
        status = "FAIL"
    elif final_kind is None:
        status = "FAIL"
        final_exit = 1
        output += "\nERROR: no actual test phase was executed\n"
        details["executed_tests"] = 0
    else:
        count, reason = _executed_test_count(final_kind, final_test_output)
        details["executed_tests"] = count
        details["minimum_tests"] = final_kind.minimum_tests
        if reason:
            status = "FAIL"
            final_exit = 1
            output += "\nERROR: " + reason + "\n"
    return new_check(
        "manifest:{0}:{1}".format(plan.module_id, plan.suite_id),
        True,
        status,
        effective,
        final_exit,
        int((time.monotonic() - started) * 1000),
        output,
        details,
    )


def _module_test_failure(module_id: str, suffix: str, message: str) -> Dict[str, Any]:
    return new_check(
        "manifest:{0}:{1}".format(module_id, suffix),
        True,
        "FAIL",
        None,
        1,
        0,
        "ERROR: " + message,
        {
            "module_id": module_id,
            "test_id": suffix,
            "test_type": "static",
            "evidence_level": VERIFICATION_LEVEL,
            "executed_tests": 0,
        },
    )


def run_required_module_static_tests(root: Path, timeout_seconds: int = 300) -> List[Dict[str, Any]]:
    checks: List[Dict[str, Any]] = []
    try:
        module_roots = discover_registered_module_dirs(root)
    except Phase0Error as exc:
        return [_module_test_failure("evidence-service", "module-discovery", str(exc))]
    for module_root in module_roots:
        module_id = module_root.name
        manifest_path = module_root / "module.yaml"
        if not manifest_path.is_file() or manifest_path.is_symlink():
            checks.append(
                _module_test_failure(module_id, "required-static", "module manifest is missing or unsafe")
            )
            continue
        try:
            manifest = load_json_compatible_yaml(manifest_path)
        except Phase0Error as exc:
            checks.append(_module_test_failure(module_id, "required-static", str(exc)))
            continue
        declared_id = manifest_module_id(manifest)
        if declared_id != module_id:
            checks.append(
                _module_test_failure(module_id, "required-static", "manifest module id does not match its directory")
            )
            continue
        tests = manifest.get("tests")
        suites = tests.get("suites") if isinstance(tests, Mapping) else None
        if not isinstance(suites, list):
            checks.append(
                _module_test_failure(module_id, "required-static", "tests.suites is missing")
            )
            continue
        suite_ids: set[str] = set()
        duplicates: set[str] = set()
        for suite in suites:
            if isinstance(suite, Mapping) and isinstance(suite.get("id"), str):
                if suite["id"] in suite_ids:
                    duplicates.add(suite["id"])
                suite_ids.add(suite["id"])
        if duplicates:
            checks.append(
                _module_test_failure(
                    module_id,
                    "required-static",
                    "duplicate suite ids: " + ", ".join(sorted(duplicates)),
                )
            )
            continue
        required = [
            suite
            for suite in suites
            if isinstance(suite, Mapping)
            and suite.get("required") is True
            and suite.get("evidenceLevel") == VERIFICATION_LEVEL
        ]
        if not required:
            checks.append(
                _module_test_failure(
                    module_id,
                    "required-static",
                    "module declares no required REPOSITORY_STATIC_VERIFIED suite",
                )
            )
            continue
        for suite in required:
            suite_id = str(suite.get("id", "invalid-suite"))
            try:
                plan = parse_manifest_suite_command(root, module_root, module_id, suite)
                plan = _trusted_suite_plan_with_ambient_environment(root, plan)
            except Phase0Error as exc:
                checks.append(_module_test_failure(module_id, suite_id, str(exc)))
                continue
            check = execute_manifest_suite(root, plan, timeout_seconds=timeout_seconds)
            check["details"]["manifest"] = str(manifest_path.relative_to(root))
            check["details"]["manifest_sha256"] = sha256_file(manifest_path)
            checks.append(check)
    return checks


def load_or_issue_receipt(
    baseline: str, receipt_path: Optional[str]
) -> Dict[str, Any]:
    if receipt_path:
        path = Path(receipt_path)
        if not path.is_absolute():
            path = ROOT / path
        try:
            value, _ = _load_committed_json_object_with_sha(
                path, "instruction receipt"
            )
        except Exception as exc:
            raise Phase0Error("cannot read instruction receipt: {0}".format(exc))
        if value.get("baseline_commit") != baseline:
            raise Phase0Error(
                "instruction receipt baseline does not match the authorized gate baseline"
            )
        valid, message, current = validate_instruction_receipt(ROOT, value)
        if not valid:
            raise Phase0Error(message)
        return current

    receipt = resolve_instruction_receipt(ROOT, baseline)
    valid, message, current = validate_instruction_receipt(ROOT, receipt)
    if not valid:
        raise Phase0Error(message)
    return current


def build_test_evidence_records(
    checks: Sequence[Mapping[str, Any]],
    baseline: str,
    receipt_id: str,
    environment: Mapping[str, Any],
    started_at: str,
    finished_at: str,
) -> List[Dict[str, Any]]:
    scalar_environment = {
        key: value
        for key, value in environment.items()
        if value is None or isinstance(value, (str, int, float, bool))
    }
    records: List[Dict[str, Any]] = []
    for check in checks:
        check_id = str(check["id"])
        details = check.get("details")
        details = details if isinstance(details, Mapping) else {}
        command = check.get("command")
        rendered_command = (
            " ".join(str(value) for value in command)
            if isinstance(command, list) and command
            else "internal:" + check_id
        )
        declared_type = details.get("test_type")
        test_type = (
            str(declared_type)
            if declared_type
            in ("static", "unit", "contract", "integration", "windows", "device", "canary", "scale")
            else "unit" if "unit-test" in check_id else "static"
        )
        module_id = details.get("module_id")
        if not isinstance(module_id, str) or re.fullmatch(
            r"[a-z0-9]+(?:-[a-z0-9]+)*", module_id
        ) is None:
            module_id = "evidence-service"
        status = str(check["status"])
        records.append(
            {
                "schema_version": "dps.phase0-test-evidence/v1",
                "evidence_id": "phase0:{0}:{1}".format(
                    sha256_text(check_id + baseline)[:16], check_id
                ),
                "test_id": check_id,
                "module_id": module_id,
                "test_type": test_type,
                "required": check.get("required") is True,
                "status": status,
                "verification_level": VERIFICATION_LEVEL,
                "baseline_commit": baseline,
                "instruction_receipt_id": receipt_id,
                "runner_identity": "dps-phase0-gate",
                "command": rendered_command,
                "started_at": started_at,
                "finished_at": finished_at,
                "exit_code": check.get("exit_code"),
                "environment": scalar_environment,
                "artifacts": [
                    {
                        "path": "embedded:checks/{0}/log".format(check_id),
                        "sha256": check["log_sha256"],
                        "media_type": "text/plain",
                    }
                ],
                "reason": None if status == "PASS" else "required check did not pass",
            }
        )
    return records


def validate_test_evidence_records(records: Sequence[Mapping[str, Any]]) -> Dict[str, Any]:
    schema_path = (
        ROOT / "governance" / "schemas" / "phase0-test-evidence.schema.json"
    )
    if not schema_path.is_file():
        raise Phase0Error("test evidence JSON Schema is required")
    try:
        schema = json.loads(schema_path.read_text(encoding="utf-8-sig"))
    except Exception as exc:
        raise Phase0Error("invalid test evidence JSON Schema: {0}".format(exc))
    errors: List[str] = []
    for index, record in enumerate(records):
        errors.extend(
            "record[{0}] {1}".format(index, error)
            for error in validate_json_schema(record, schema)
        )
    if errors:
        raise Phase0Error("test evidence violates schema: " + "; ".join(errors))
    return {"record_count": len(records), "schema": str(schema_path.relative_to(ROOT))}


def _run_phase0_gate(
    arguments: argparse.Namespace,
    evidence_path: Path,
    receipt_path: Optional[Path],
    evidence_publication: EvidencePublication,
    receipt_publication: Optional[EvidencePublication],
) -> int:
    gate_started_at = dt.datetime.now(dt.timezone.utc).isoformat()
    checks: List[Dict[str, Any]] = []
    baseline: Optional[str] = None
    baseline_authoritative = False
    receipt: Dict[str, Any] = {}
    environment: Dict[str, Any] = {}
    workspace_check = workspace_cleanliness_check(
        ROOT, bool(arguments.diagnostic_workspace)
    )
    checks.append(workspace_check)
    formal_evidence_eligible = (
        workspace_check.get("details", {}).get("formal_evidence_eligible") is True
    )

    try:
        baseline, baseline_authoritative = resolve_baseline(arguments.base)
    except Phase0Error as exc:
        checks.append(
            new_check(
                "baseline-resolution",
                True,
                "INFRA_ERROR",
                None,
                1,
                0,
                "ERROR: " + str(exc),
            )
        )

    environment, toolchain_errors = toolchain_details(ROOT, arguments.node)
    environment["workspace_clean"] = (
        workspace_check.get("details", {}).get("clean") is True
    )
    environment["evidence_mode"] = (
        "WORKSPACE_DIAGNOSTIC_ONLY"
        if arguments.diagnostic_workspace
        else VERIFICATION_LEVEL
    )
    checks.append(
        new_check(
            "pinned-toolchain",
            True,
            "PASS" if not toolchain_errors else "INFRA_ERROR",
            None,
            0 if not toolchain_errors else 1,
            0,
            "toolchain accepted"
            if not toolchain_errors
            else "\n".join("ERROR: " + value for value in toolchain_errors),
            environment,
        )
    )

    validator_result = run_command(
        [sys.executable, "Tools/ci/validate_repo.py"], ROOT, timeout_seconds=180
    )
    checks.append(check_from_command("repository-validator", True, validator_result))
    shell_result = run_command(
        ["/bin/bash", "-n", "scripts/release.sh"], ROOT, timeout_seconds=30
    )
    checks.append(check_from_command("release-shell-syntax", True, shell_result))

    checks.append(
        in_process_check(
            "module-governance",
            lambda: validate_governance(ROOT, require_schema=True),
        )
    )
    checks.append(
        in_process_check("ci-fail-closed-policy", lambda: validate_ci_integrity(ROOT))
    )

    if baseline is not None:
        instruction_check = in_process_check(
            "instruction-resolution-and-staleness",
            lambda: load_or_issue_receipt(baseline, arguments.receipt_in),
        )
        checks.append(instruction_check)
        if instruction_check["status"] == "PASS":
            receipt = dict(instruction_check["details"])
    else:
        checks.append(
            new_check(
                "instruction-resolution-and-staleness",
                True,
                "NOT_RUN",
                None,
                None,
                0,
                "baseline resolution failed",
            )
        )

    checks.append(run_phase0_unittests(baseline))
    checks.append(run_external_gate_unittests())
    checks.append(run_locked_solution_build(ROOT))
    checks.extend(run_required_module_static_tests(ROOT))

    node_executable = environment.get("node_executable")
    if isinstance(node_executable, str):
        playwright_report = ROOT / "Reports" / "ci" / "playwright-config.json"
        playwright_result = run_command(
            [
                node_executable,
                "Tests/playwright_dps_test.js",
                "--mode",
                "config",
                "--report",
                str(playwright_report),
            ],
            ROOT,
            timeout_seconds=120,
        )
        checks.append(
            check_from_command("playwright-static-config", True, playwright_result)
        )
    else:
        checks.append(
            new_check(
                "playwright-static-config",
                True,
                "INFRA_ERROR",
                None,
                127,
                0,
                "Node 24 executable is unavailable",
            )
        )

    if receipt:
        checks.append(
            in_process_check(
                "instruction-receipt-final-staleness",
                lambda: (
                    {"receipt_id": receipt["receipt_id"]}
                    if validate_instruction_receipt(ROOT, receipt)[0]
                    else (_ for _ in ()).throw(
                        Phase0Error(validate_instruction_receipt(ROOT, receipt)[1])
                    )
                ),
            )
        )
    else:
        checks.append(
            new_check(
                "instruction-receipt-final-staleness",
                True,
                "NOT_RUN",
                None,
                None,
                0,
                "instruction receipt is unavailable",
            )
        )

    baseline_red_state = load_baseline_red_ledger_state(
        ROOT, baseline if baseline_authoritative else None
    )
    checks.append(baseline_red_ledger_drift_check(baseline_red_state))

    try:
        head_commit = git_output(ROOT, ["rev-parse", "HEAD"])
    except Phase0Error:
        head_commit = None
    digest = None
    if baseline is not None:
        try:
            digest = workspace_digest(ROOT, baseline)
        except Phase0Error:
            digest = None
    environment["head_commit_observed"] = head_commit

    pre_schema_finished_at = dt.datetime.now(dt.timezone.utc).isoformat()
    if (
        formal_evidence_eligible
        and baseline is not None
        and receipt.get("receipt_id")
    ):
        preview_records = build_test_evidence_records(
            checks,
            baseline,
            str(receipt["receipt_id"]),
            environment,
            gate_started_at,
            pre_schema_finished_at,
        )
        checks.append(
            in_process_check(
                "test-evidence-schema",
                lambda: validate_test_evidence_records(preview_records),
            )
        )
    elif not formal_evidence_eligible:
        checks.append(
            new_check(
                "test-evidence-schema",
                True,
                "PASS",
                None,
                0,
                0,
                "formal per-test evidence suppressed for a non-releasable workspace",
                {
                    "record_count": 0,
                    "formal_evidence_eligible": False,
                },
            )
        )
    else:
        checks.append(
            new_check(
                "test-evidence-schema",
                True,
                "NOT_RUN",
                None,
                None,
                0,
                "instruction receipt is unavailable",
            )
        )

    overall_status, summary = evaluate_checks(checks)
    baseline_red = evaluate_baseline_red_policy(checks, baseline_red_state)
    overall_status = apply_baseline_red_policy(overall_status, baseline_red)
    finished_at = dt.datetime.now(dt.timezone.utc).isoformat()
    test_evidence = (
        build_test_evidence_records(
            checks,
            baseline,
            str(receipt["receipt_id"]),
            environment,
            gate_started_at,
            finished_at,
        )
        if (
            formal_evidence_eligible
            and baseline is not None
            and receipt.get("receipt_id")
        )
        else []
    )
    gate_name, verification_level = evidence_classification(
        overall_status,
        formal_evidence_eligible,
        bool(arguments.diagnostic_workspace),
    )

    evidence: Dict[str, Any] = {
        "schema_version": "dps.phase0-evidence-bundle/v1",
        "gate": gate_name,
        "verification_level": verification_level,
        "overall_status": overall_status,
        "commit_sha": head_commit if formal_evidence_eligible else None,
        "head_commit_observed": head_commit,
        "baseline_commit": baseline,
        "workspace_sha256": digest,
        "started_at": gate_started_at,
        "finished_at": finished_at,
        "environment": environment,
        "instruction_receipt": receipt or None,
        "checks": checks,
        "test_evidence": test_evidence,
        "summary": summary,
        "baseline_red": baseline_red,
        "limitations": [
            "No Windows, ZennoDroid, ADB, GBrain, or real-device verification is claimed.",
            "Hosted CI cannot issue WINDOWS_VERIFIED or DEVICE_VERIFIED evidence.",
            (
                "COMMITTED publication markers detect concurrent, torn, or mismatched "
                "local writes, but do not replace separate OS identities for an "
                "untrusted runner and evidence issuer."
            ),
            (
                "This is non-releasable workspace diagnostic output; no formal "
                "verification level or commit attribution is issued."
                if arguments.diagnostic_workspace
                else "Formal evidence requires a completely clean checkout."
            ),
        ],
    }
    evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
    publication_revoked = False
    try:
        write_evidence(
            evidence_path,
            evidence,
            publication=evidence_publication,
            commit=False,
        )
        if formal_evidence_eligible:
            post_write_head = git_output(ROOT, ["rev-parse", "HEAD"])
            post_write_digest = (
                workspace_digest(ROOT, baseline) if baseline is not None else None
            )
            post_write_clean = workspace_cleanliness_check(ROOT, diagnostic=False)
            if (
                post_write_head != head_commit
                or post_write_digest != digest
                or post_write_clean.get("status") != "PASS"
            ):
                evidence["gate"] = VERIFICATION_LEVEL
                evidence["verification_level"] = None
                evidence["overall_status"] = "FAIL"
                evidence["commit_sha"] = None
                evidence["limitations"].append(
                    "Formal evidence was revoked because repository state changed during evidence publication."
                )
                evidence.pop("evidence_sha256", None)
                evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
                write_evidence(
                    evidence_path,
                    evidence,
                    publication=evidence_publication,
                    commit=False,
                )
                publication_revoked = True
        if (
            not publication_revoked
            and receipt_path is not None
            and receipt
            and receipt_publication is not None
        ):
            write_evidence(
                receipt_path,
                receipt,
                publication=receipt_publication,
                commit=False,
            )
            receipt_publication.commit()
        if formal_evidence_eligible and not publication_revoked:
            final_publish_head = git_output(ROOT, ["rev-parse", "HEAD"])
            final_publish_digest = (
                workspace_digest(ROOT, baseline) if baseline is not None else None
            )
            final_publish_clean = workspace_cleanliness_check(
                ROOT, diagnostic=False
            )
            if (
                final_publish_head != head_commit
                or final_publish_digest != digest
                or final_publish_clean.get("status") != "PASS"
            ):
                evidence["gate"] = VERIFICATION_LEVEL
                evidence["verification_level"] = None
                evidence["overall_status"] = "FAIL"
                evidence["commit_sha"] = None
                evidence["limitations"].append(
                    "Formal evidence was revoked by the final pre-COMMITTED repository check."
                )
                evidence.pop("evidence_sha256", None)
                evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
                write_evidence(
                    evidence_path,
                    evidence,
                    publication=evidence_publication,
                    commit=False,
                )
                publication_revoked = True
        evidence_publication.commit()
    except Exception as exc:
        print("ERROR: failed to write evidence: {0}".format(exc), file=sys.stderr)
        return 1

    if publication_revoked:
        print(
            "ERROR: repository state changed during evidence publication",
            file=sys.stderr,
        )
        return 1

    print("Phase 0 gate: " + overall_status)
    print("Verification level: " + (evidence["verification_level"] or "NONE"))
    print("Commit: " + str(head_commit))
    print("Evidence: " + str(evidence_path))
    print("COMMITTED marker: " + str(evidence_publication.marker_path))
    for check in checks:
        print("[{0}] {1}".format(check["status"], check["id"]))
    for note in baseline_red.get("notes", ()):
        print("NOTE: " + note, file=sys.stderr)
    if (
        arguments.require_literal_pass
        and overall_status == OVERALL_PASS_WITH_REGISTERED_BASELINE
    ):
        print(
            "ERROR: --require-literal-pass rejects "
            + OVERALL_PASS_WITH_REGISTERED_BASELINE
            + "; registered baseline reds are never releasable",
            file=sys.stderr,
        )
    return gate_exit_code(overall_status, bool(arguments.require_literal_pass))


def main(argv: Optional[Sequence[str]] = None) -> int:
    arguments = parse_arguments(argv)
    publication_run_id = _new_publication_run_id()
    try:
        raw_evidence_path = (
            Path(arguments.evidence)
            if arguments.evidence is not None
            else _default_phase0_evidence_path(publication_run_id)
        )
        evidence_path = _safe_phase0_output_path(
            ROOT, raw_evidence_path, "Phase0 evidence"
        )
        receipt_path = (
            _safe_phase0_output_path(
                ROOT, Path(arguments.receipt_out), "instruction receipt"
            )
            if arguments.receipt_out
            else None
        )
        if receipt_path is not None:
            same_existing_file = False
            if receipt_path.exists() and evidence_path.exists():
                try:
                    same_existing_file = os.path.samefile(receipt_path, evidence_path)
                except OSError:
                    same_existing_file = False
            if receipt_path == evidence_path or same_existing_file:
                raise Phase0Error(
                    "Phase0 evidence and instruction receipt must use different paths"
                )
    except Phase0Error as exc:
        print("ERROR: unsafe Phase0 output path: " + str(exc), file=sys.stderr)
        return 2

    try:
        with contextlib.ExitStack() as stack:
            evidence_publication = stack.enter_context(
                EvidencePublication(evidence_path, run_id=publication_run_id)
            )
            receipt_publication = (
                stack.enter_context(EvidencePublication(receipt_path))
                if receipt_path is not None
                else None
            )
            run_exit_code = _run_phase0_gate(
                arguments,
                evidence_path,
                receipt_path,
                evidence_publication,
                receipt_publication,
            )
    except (OSError, Phase0Error, RuntimeError) as exc:
        print("ERROR: evidence publication could not start: {0}".format(exc), file=sys.stderr)
        run_exit_code = 1

    overall_status, _ = evaluate_checks(
        [
            new_check(
                "phase0-runner-outcome",
                True,
                "PASS" if run_exit_code == 0 else "FAIL",
                None,
                run_exit_code,
                0,
                "Phase 0 runner completed"
                if run_exit_code == 0
                else "Phase 0 runner failed closed",
            )
        ]
    )
    return 0 if overall_status == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
