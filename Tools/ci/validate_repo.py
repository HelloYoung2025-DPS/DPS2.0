#!/usr/bin/env python3
"""Dependency-free repository validation used locally and in Hosted CI."""

from __future__ import print_function

import ast
import json
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def repository_files(pattern):
    command = [
        "git",
        "ls-files",
        "--cached",
        "--others",
        "--exclude-standard",
        pattern,
    ]
    output = subprocess.check_output(command, cwd=str(ROOT), text=True)
    return sorted(
        path
        for path in (ROOT / value for value in output.splitlines() if value)
        if path.is_file()
    )


def relative(path):
    return str(path.relative_to(ROOT))


def validate_removed_legacy_workflow(errors):
    forbidden = [ROOT / ".omo", ROOT / ".omo.conf", ROOT / "Tools" / "omo_guard"]
    for path in forbidden:
        if path.exists():
            errors.append("removed workflow path still exists: " + relative(path))


def validate_json(errors):
    files = repository_files("*.json")
    for path in files:
        try:
            json.loads(path.read_text(encoding="utf-8-sig"))
        except Exception as exc:
            errors.append("invalid JSON {0}: {1}".format(relative(path), exc))
    return len(files)


def validate_python(errors):
    files = repository_files("*.py")
    for path in files:
        try:
            ast.parse(path.read_text(encoding="utf-8-sig"), filename=relative(path))
        except Exception as exc:
            errors.append("invalid Python {0}: {1}".format(relative(path), exc))
    return len(files)


def validate_markdown_links(errors):
    files = repository_files("*.md")
    link_pattern = re.compile(r"\[[^\]]+\]\(([^)]+)\)")

    for path in files:
        content = path.read_text(encoding="utf-8-sig")
        for raw_target in link_pattern.findall(content):
            target = raw_target.strip().split(" ", 1)[0]
            if target.startswith(("http://", "https://", "#", "mailto:")):
                continue
            target_path = target.split("#", 1)[0]
            if not target_path:
                continue
            resolved = (path.parent / target_path).resolve()
            if not resolved.exists():
                errors.append(
                    "missing Markdown link target {0}: {1}".format(
                        relative(path), target
                    )
                )
    return len(files)


def validate_diff_whitespace(errors):
    result = subprocess.run(
        ["git", "diff", "--check"],
        cwd=str(ROOT),
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    if result.returncode != 0:
        errors.append("git diff --check failed:\n" + result.stdout.rstrip())


def main():
    errors = []
    validate_removed_legacy_workflow(errors)
    json_count = validate_json(errors)
    python_count = validate_python(errors)
    markdown_count = validate_markdown_links(errors)
    validate_diff_whitespace(errors)

    print(
        "validated json={0} python={1} markdown={2}".format(
            json_count, python_count, markdown_count
        )
    )

    if errors:
        for error in errors:
            print("ERROR: " + error)
        return 1

    print("repository validation passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
