#!/usr/bin/env python3
import json
import pathlib
import shutil
import subprocess
import sys


def waiting(reason: str) -> int:
    print(json.dumps({"status": "WAITING_EXTERNAL", "verification_claim": None, "reason": reason}))
    return 2


def main() -> int:
    if sys.platform != "win32":
        return waiting("Windows host is required")
    pwsh = shutil.which("pwsh")
    if not pwsh:
        return waiting("PowerShell is required")
    script = pathlib.Path(__file__).with_name("Invoke-WindowsZennoGate.ps1")
    return subprocess.run([pwsh, "-NoProfile", "-File", str(script)], check=False).returncode


if __name__ == "__main__":
    raise SystemExit(main())
