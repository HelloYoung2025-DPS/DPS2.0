#!/usr/bin/env python3
import json
import pathlib
import re
import sys


ROOT = pathlib.Path(__file__).resolve().parents[3]
MODULE = ROOT / "Modules" / "zenno-bridge"
SOURCE = "\n".join(path.read_text(encoding="utf-8") for path in sorted((MODULE / "src").rglob("*.cs")))
PROJECT = (MODULE / "src/Dps.ZennoBridge.csproj").read_text(encoding="utf-8")
LOCKFILE = MODULE / "src/packages.lock.json"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    exchange = json.loads((MODULE / "contracts/provided/edge.bridge.exchange.v1.schema.json").read_text(encoding="utf-8"))
    directive = json.loads((ROOT / "Modules/windows-edge-supervisor/contracts/provided/edge.bridge.directive.v1.schema.json").read_text(encoding="utf-8"))
    require(exchange["additionalProperties"] is False, "exchange must reject unknown fields")
    require(directive["additionalProperties"] is False, "directive must reject unknown fields")
    require(exchange["properties"]["producer_module"]["const"] == "zenno-bridge", "exchange producer mismatch")
    require(directive["properties"]["producer_module"]["const"] == "windows-edge-supervisor", "directive producer mismatch")
    require(exchange["properties"]["soul_id"]["pattern"] == r"^soul_[a-f0-9]{64}$(?![\s\S])", "canonical soul pattern missing")
    for schema in (exchange, directive):
        for field in ("soul_id", "device_binding_id", "platform_account_id"):
            require(schema["properties"][field]["type"] == "string", field + " must be a non-null canonical identity")
    require("auth_nonce" in exchange["required"], "exchange nonce is required")
    for field in ("auth_key_id", "auth_nonce", "auth_issued_at", "auth_body_sha256", "auth_proof"):
        require(field in directive["required"], "directive authentication field missing: " + field)
    require("<LangVersion>5</LangVersion>" in PROJECT, "bridge project must pin C# 5")
    require("<TargetFramework>net40</TargetFramework>" in PROJECT, "bridge target must remain conservative until Windows probe")
    require(LOCKFILE.is_file(), "bridge build dependency lockfile is required")
    require("<Nullable>disable</Nullable>" in PROJECT and "<ImplicitUsings>disable</ImplicitUsings>" in PROJECT,
            "bridge must neutralize modern root compiler features")

    forbidden_syntax = [r"\brecord\b", r"\binit\s*;", r"\basync\b", r"\bawait\b", r"\bnameof\s*\(", r"\?\.", r"\?\?=", r"\$\""]
    for pattern in forbidden_syntax:
        require(re.search(pattern, SOURCE) is None, "modern C# syntax found: " + pattern)

    forbidden_apis = ["System.Text.Json", "HttpClient", "Task<", "Assembly.Load", "Process.Start", "Microsoft.Win32.Registry"]
    for api in forbidden_apis:
        require(api not in SOURCE, "unprobed or modern API found: " + api)

    require("http://127.0.0.1:28741/dps/edge/v1/exchange" in SOURCE, "fixed loopback endpoint missing")
    require("WebRequest.Create(FixedEndpoint)" in SOURCE, "bridge may not accept an arbitrary endpoint")
    require("UseDefaultCredentials = true" in SOURCE, "loopback request must present the Windows process identity")
    require("Trusted loopback peer authentication is not configured." in SOURCE, "missing peer trust must fail before network I/O")
    require("Directive authentication nonce was replayed." in SOURCE, "response proof replay rejection is missing")
    require("rsa-pkcs1-sha256" in SOURCE, "directive proof algorithm is not explicitly pinned")
    require("subjectPublicKeyInfo" in SOURCE, "bridge key id is not derived from SubjectPublicKeyInfo")
    require("MaximumResponseBytes" in SOURCE and "exceeds the authenticated size limit" in SOURCE,
            "loopback response is not bounded before deserialization")
    require("IExtensibleDataObject" in SOURCE and "Unknown directive fields are forbidden." in SOURCE,
            "runtime must reject undeclared response fields")
    require("Unknown action kind." in SOURCE and "Unknown or mismatched step kind." in SOURCE, "fail-closed allowlists missing")
    require("ACK and WAIT directives must not carry command fields." in SOURCE,
            "non-command directive truth table is missing")
    require(re.search(r"\b(coordinate|tapx|tapy|screenx|screeny)\b", SOURCE, re.IGNORECASE) is None, "coordinate fallback surface found")
    require("GBrain" not in SOURCE and "Persona" not in SOURCE and "Interest" not in SOURCE, "forbidden memory ownership found")
    print(json.dumps({"status": "PASS", "test_type": "static", "verification_level": "REPOSITORY_STATIC_VERIFIED", "windows_verified": False}))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"status": "FAIL", "error": str(error), "windows_verified": False}), file=sys.stderr)
        raise SystemExit(1)
