# Operations

The bridge calls only the fixed loopback endpoint. It sends versioned poll/native-result exchanges with a fresh 256-bit nonce and the Windows process identity, validates returned identity and allowlisted action/step pairs, and never interprets unknown work as a coordinate click. The default constructor has no trust anchor and rejects before network I/O. A configured client accepts a directive only when a deployment-pinned RSA public key verifies the nonce, canonical UTC timestamp, complete body digest, and proof; replayed nonces fail closed.

The Mac auth test is a simulation only. The Windows capability probe must prove that the host actually enforces the selected peer ACL/authentication mode. Merely listening on `127.0.0.1` is not authentication and cannot satisfy `WINDOWS_VERIFIED`.

The bridge build uses the pinned repository .NET SDK, a locked private `Microsoft.NETFramework.ReferenceAssemblies` build dependency, `net40`, and `LangVersion 5`. A successful Mac build proves reproducible compilation only; it does not prove that the target ZennoDroid installation can load the assembly.

Edge Worker and signed configuration upgrades occur outside ZennoDroid. The bridge is frozen after Windows verification. Any bridge replacement uses a separately approved maintenance window unless the exact target installation has proven safe replacement without a ZennoDroid restart.
