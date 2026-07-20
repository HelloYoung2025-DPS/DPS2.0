using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

/// <summary>
/// Shared fixture kit for the recovery-fence tests (unit, in-memory store)
/// and the durable truth store integration tests (PostgreSQL): a runtime
/// RSA-PSS release BOM signer producing the exact canonical signed wire the
/// activation authority accepts, plus the minimal policy submission
/// lifecycle scaffolding needed to drive CreateRecoveryAsync through the
/// production API. All key material is generated per test run; nothing is
/// ever persisted or committed.
/// </summary>
internal static class ReleaseBindingRecoveryTestKit
{
    internal const string Soul =
        "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Device = "db_11111111111111111111111111111111";
    internal const string Account = "pa_22222222222222222222222222222222";
    internal const string Trace = "trace_33333333333333333333333333333333";
    internal const string Idempotency =
        "idem_4444444444444444444444444444444444444444444444444444444444444444";
    internal const string PriorBom =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string NativeBinding =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    internal const string Intent =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    internal const string EvidenceSha256 =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    internal const string ZeroSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    internal static readonly string ZeroSignature =
        Convert.ToBase64String(new byte[64]);
    internal static readonly DateTimeOffset Now =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    internal static readonly Guid SubmissionAttemptId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid ApprovalId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static readonly Guid ProposalId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");
    internal static readonly Guid CommandId =
        Guid.Parse("44444444-4444-4444-8444-444444444444");
    internal static readonly Guid LeaseId =
        Guid.Parse("55555555-5555-4555-8555-555555555555");

    internal static string Token(string seed)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("token:" + seed)));

    internal static string Sha256Hex(byte[] value)
        => Convert.ToHexStringLower(SHA256.HashData(value));

    internal static ControlPlaneReconciliationRequest ReconciliationRequest()
        => new(
            Guid.Parse("66666666-6666-4666-8666-666666666666"),
            ApprovalSubmissionReconciliationV1.ConfirmedNotSubmitted,
            EvidenceSha256,
            Now,
            Now.AddMinutes(4));

    internal static ControlPlaneRecoveryRequest RecoveryRequest(
        string nextReleaseBomSha256,
        long nextReleaseBomGeneration,
        Guid? recoveryId = null)
        => new(
            recoveryId ?? Guid.Parse("77777777-7777-4777-8777-777777777777"),
            Guid.Parse("88888888-8888-4888-8888-888888888888"),
            Guid.Parse("99999999-9999-4999-8999-999999999999"),
            2,
            nextReleaseBomSha256,
            nextReleaseBomGeneration,
            new string('1', 64),
            new string('2', 64),
            "human_" + new string('3', 64),
            Now.AddMinutes(1),
            Now.AddMinutes(5));

    internal static ApprovalSubmissionStateExpectation Expectation(
        ApprovalSubmissionStateV1 state)
        => new(
            state.SubmissionAttemptId,
            state.ApprovalId,
            state.ProposalId,
            state.CommandId,
            state.LeaseId,
            state.Attempt,
            state.SoulId,
            state.DeviceBindingId,
            state.PlatformAccountId,
            state.TraceId,
            state.IdempotencyKey,
            state.ReleaseBomSha256,
            state.ReleaseBomGeneration,
            state.NativeRequestBindingSha256,
            state.SubmissionIntentSha256,
            state.State,
            state.PredecessorStateSha256,
            state.EvidenceSha256);

    internal static ApprovalSubmissionStateV1 SignedState(
        ECDsa key,
        string state,
        string? predecessor,
        string evidence,
        Guid? stateEventId = null)
    {
        var unsigned = new ApprovalSubmissionStateV1(
            ApprovalSubmissionStateV1.CurrentSchemaVersion,
            ApprovalSubmissionStateV1.CurrentContractId,
            ApprovalSubmissionStateV1.CurrentProducerModule,
            stateEventId ?? Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            SubmissionAttemptId,
            ApprovalId,
            ProposalId,
            CommandId,
            LeaseId,
            1,
            Soul,
            Device,
            Account,
            Trace,
            Idempotency,
            PriorBom,
            7,
            NativeBinding,
            Intent,
            state,
            predecessor,
            evidence,
            Now,
            "internal",
            ZeroSha256,
            ZeroSignature);
        var withDigest = unsigned with
        {
            StateSha256 = ApprovalSubmissionLifecycleBinding.ComputeStateSha256(unsigned)
        };
        var canonical = ApprovalSubmissionLifecycleBinding.CanonicalStateBytes(withDigest);
        try
        {
            var signature = key.SignData(
                canonical,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            try
            {
                return withDigest with
                {
                    SignatureBase64 = Convert.ToBase64String(signature)
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    internal sealed class KitReconciliationSigner
        : IControlPlaneReconciliationSigningAuthority
    {
        private readonly ECDsa _key;
        private readonly object _gate = new();

        internal KitReconciliationSigner(ECDsa key) => _key = key;

        public byte[] ExportSubjectPublicKeyInfo()
            => _key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> SignReconciliationAsync(
            ApprovalSubmissionReconciliationV1 unsignedReconciliation,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_key.SignData(
                    canonicalPayload.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
        }
    }

    internal sealed class KitRecoverySigner
        : IControlPlaneHumanRecoveryApprovalAuthority
    {
        private readonly ECDsa _key;
        private readonly object _gate = new();

        internal KitRecoverySigner(ECDsa key) => _key = key;

        internal int CallCount { get; private set; }

        /// <summary>
        /// Runs while the producer awaits the human recovery signer — the
        /// window between fence issuance and fence commit. Fence tests use
        /// it to advance the release binding mid-signature.
        /// </summary>
        internal Action? WhileSigning { get; set; }

        public byte[] ExportSubjectPublicKeyInfo()
            => _key.ExportSubjectPublicKeyInfo();

        public Task<byte[]> AuthorizeAndSignRecoveryAsync(
            ApprovalSubmissionRecoveryV1 unsignedRecovery,
            ReadOnlyMemory<byte> canonicalPayload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WhileSigning?.Invoke();
            lock (_gate)
            {
                CallCount++;
                return Task.FromResult(_key.SignData(
                    canonicalPayload.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
        }
    }

    /// <summary>
    /// One complete recovery lifecycle harness: distinct policy-state,
    /// reconciliation, and human-recovery keys, one consumer, one producer
    /// over caller-supplied binding facts and fence authorities. Recovery
    /// state is established exclusively through the production API
    /// (Consume → CreateReconciliationAsync → Consume → CreateRecoveryAsync).
    /// </summary>
    internal sealed class RecoveryLifecycleHarness : IDisposable
    {
        private readonly ECDsa _policyStateKey;
        private readonly ECDsa _reconciliationKey;
        private readonly ECDsa _recoveryKey;

        internal RecoveryLifecycleHarness(
            PolicyBoundReleaseBomFactsSource factsSource,
            IReleaseBindingRecoveryFenceAuthority fenceAuthority)
        {
            _policyStateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _reconciliationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _recoveryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Consumer = new ControlPlaneSubmissionStateConsumer(
                _policyStateKey.ExportSubjectPublicKeyInfo());
            RecoverySigner = new KitRecoverySigner(_recoveryKey);
            Producer = new ControlPlaneSubmissionLifecycleProducer(
                new KitReconciliationSigner(_reconciliationKey),
                RecoverySigner,
                Consumer.AuthorityFingerprintSha256,
                factsSource,
                fenceAuthority);
        }

        internal ControlPlaneSubmissionStateConsumer Consumer { get; }

        internal ControlPlaneSubmissionLifecycleProducer Producer { get; }

        internal KitRecoverySigner RecoverySigner { get; }

        /// <summary>
        /// Drives the production recovery path up to CreateRecoveryAsync:
        /// verified PENDING state, signed CONFIRMED_NOT_SUBMITTED
        /// reconciliation, verified RECONCILED state, then the recovery
        /// issuance itself (fence issuance, human signature, fence commit).
        /// </summary>
        internal async Task<SignedControlPlaneLifecycleEnvelope<ApprovalSubmissionRecoveryV1>>
            RecoverAsync(ControlPlaneRecoveryRequest request)
        {
            var pendingState = SignedState(
                _policyStateKey,
                ApprovalSubmissionStateV1.SubmissionPending,
                predecessor: null,
                evidence: Intent);
            var pending = Consumer.Consume(
                ApprovalSubmissionStateV1Codec.Serialize(pendingState),
                Expectation(pendingState));
            var reconciliation = await Producer.CreateReconciliationAsync(
                pending,
                ReconciliationRequest(),
                TestContext.Current.CancellationToken);
            var reconciledState = SignedState(
                _policyStateKey,
                ApprovalSubmissionStateV1.ReconciledNotSubmitted,
                pendingState.StateSha256,
                reconciliation.CommitmentSha256,
                stateEventId: Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
            var reconciled = Consumer.Consume(
                ApprovalSubmissionStateV1Codec.Serialize(reconciledState),
                Expectation(reconciledState));
            return await Producer.CreateRecoveryAsync(
                reconciled,
                reconciliation,
                request,
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Producer.Dispose();
            Consumer.Dispose();
            _policyStateKey.Dispose();
            _reconciliationKey.Dispose();
            _recoveryKey.Dispose();
        }
    }

    /// <summary>
    /// Runtime-generated RSA-PSS release BOM signer emitting the exact
    /// canonical sorted compact wire the activation authority accepts
    /// (mirrors the activation authority unit fixture).
    /// </summary>
    internal sealed class BomSigner : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);

        internal string KeyId { get; }
        internal string Identity { get; }

        internal BomSigner(
            string keyId = "test-bom-key-v1",
            string identity = "test-release-controller")
        {
            KeyId = keyId;
            Identity = identity;
        }

        internal ReleaseBomTrustKey TrustKey
        {
            get
            {
                var parameters = _rsa.ExportParameters(false);
                return new ReleaseBomTrustKey(
                    KeyId,
                    Identity,
                    Convert.ToHexStringLower(parameters.Modulus!),
                    65537);
            }
        }

        internal (byte[] Bom, string Token) SignBom(
            string bomId,
            long signerGeneration,
            byte[]? previousBom)
        {
            var token = Token(bomId);
            var tokenBytes = Convert.FromBase64String(token);
            var payload = new JsonObject
            {
                ["schema_version"] = "dps.release-bom/v1",
                ["bom_id"] = bomId,
                ["status"] = "SIGNED",
                ["integration_commit"] = new string('a', 40),
                ["created_at"] = "2026-07-14T00:00:00.0000001Z",
                ["release_bom_generation"] = signerGeneration,
                ["activation_token_sha256"] = Convert.ToHexStringLower(SHA256.HashData(tokenBytes)),
                ["modules"] = new JsonArray(),
                ["instruction_hashes"] = new JsonObject(),
                ["contracts"] = new JsonArray(),
                ["database_versions"] = new JsonObject(),
                ["dependency_dag_sha256"] = new string('b', 64),
                ["compatibility_matrix_sha256"] = new string('c', 64),
                ["feature_flags"] = new JsonObject(),
                ["kill_switches"] = new JsonArray(),
                ["ai_toolchain"] = new JsonObject(),
                ["evidence"] = new JsonArray(),
                ["risk"] = new JsonObject(),
                ["release_approval"] = new JsonObject(),
                ["rollout"] = new JsonObject(),
                ["rollback"] = new JsonObject(),
                ["previous_stable_bom"] = previousBom is null
                    ? null
                    : (JsonNode)("bom-previous-" + bomId),
                ["previous_stable_bom_sha256"] = previousBom is null
                    ? null
                    : Sha256Hex(previousBom),
                ["native_stop_authorities"] = new JsonArray(),
                ["device_route_assignment_authorities"] = new JsonArray(),
                ["native_stop_challenge_authorities"] = new JsonArray()
            };
            using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
            var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
            var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                .Concat(canonical)
                .ToArray();
            var signature = _rsa.SignData(
                message,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = "rsa-pss-sha256",
                ["key_id"] = KeyId,
                ["value"] = Convert.ToBase64String(signature)
            };
            using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
            return (ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement), token);
        }

        public void Dispose() => _rsa.Dispose();
    }
}
