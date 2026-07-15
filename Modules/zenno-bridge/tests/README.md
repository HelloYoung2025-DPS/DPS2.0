# Test boundaries

The Mac static gate validates JSON, forbids modern C# syntax and disallowed APIs, verifies the fixed loopback endpoint, checks explicit action/step allowlists, and confirms that no coordinate fallback exists.

The required Contract gates run the Zenno-owned nine-case exchange corpus and the supervisor-owned twelve-case directive corpus, then serialize the exact linked C# 5 `BridgeExchange` production DTO and check its complete wire envelope against the owned schema. Poll/native-result and action/step truth, UTC, opaque IDs, nonce, digest, key ID, and canonical Base64 boundaries fail closed. These gates do not prove Windows peer authentication or ZennoDroid compatibility.

The Mac authentication simulation links the C# 5 source into a .NET 10 test host and proves pinned-key success, rogue-signature rejection, body-tamper rejection, replay rejection, and default fail-closed behavior. It is labelled `SIMULATION`; it does not prove Windows peer ACLs or ZennoDroid compatibility.

The Windows gate requires pinned PowerShell 7.6.2, Android Platform Tools 37.0.0-14910828 (the same versions enforced by the repository wrappers), ZennoDroid, the probed bridge ABI, unchanged ZennoDroid PID and start time, connection continuity, one hundred real A/B switches, rollback, and a 24-hour soak. Missing or mismatched prerequisites produce `WAITING_EXTERNAL` and non-zero exit; they never produce PASS.
