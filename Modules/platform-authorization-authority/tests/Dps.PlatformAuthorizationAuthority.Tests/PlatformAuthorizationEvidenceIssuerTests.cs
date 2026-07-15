using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Dps.PlatformAuthorizationAuthority.Contracts;
using Xunit;

namespace Dps.PlatformAuthorizationAuthority.Tests;

public sealed class PlatformAuthorizationEvidenceIssuerTests
{
    private const string UnitCategory = "Unit";
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", UnitCategory)]
    public async Task Issuance_requires_verified_raw_proof_and_binds_only_trusted_runtime_bom_generation()
    {
        using var fixture = new IssuerFixture();
        var result = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);

        Assert.Equal(fixture.Runtime.Context.ReleaseBomSha256, result.Evidence.ReleaseBomSha256);
        Assert.Equal(fixture.Runtime.Context.ReleaseGeneration, result.Evidence.ReleaseGeneration);
        Assert.Equal(1, fixture.Verifier.Calls);
        Assert.Equal(1, fixture.Signer.SignCalls);
        Assert.DoesNotContain("raw-proof", Encoding.UTF8.GetString(result.ExactEnvelopeUtf8.Span), StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(PlatformAuthorizationEvidenceIssueRequest).GetProperties(),
            property => property.Name.Contains("Release", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Same_scope_and_idempotency_payload_replays_byte_identical_envelope_without_resigning()
    {
        using var fixture = new IssuerFixture();
        var first = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);
        var second = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.ExactEnvelopeUtf8.ToArray(), second.ExactEnvelopeUtf8.ToArray());
        Assert.Equal(first.EnvelopeSha256, second.EnvelopeSha256);
        Assert.Equal(1, fixture.Verifier.Calls);
        Assert.Equal(1, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Replay_under_any_different_active_runtime_trust_context_fails_closed()
    {
        Action<TestRuntimeContextProvider>[] mutations =
        {
            runtime => runtime.SetContext(runtime.Context with
            {
                ReleaseBomSha256 = new string('d', 64),
                ReleaseGeneration = runtime.Context.ReleaseGeneration + 1
            }),
            runtime => runtime.SetContext(runtime.Context with
            {
                TrustEpoch = runtime.Context.TrustEpoch + 1
            }),
            runtime => runtime.SetContext(runtime.Context with
            {
                RuntimeContextSha256 = new string('e', 64)
            })
        };

        foreach (var mutate in mutations)
        {
            using var fixture = new IssuerFixture();
            _ = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);
            mutate(fixture.Runtime);

            await Assert.ThrowsAsync<PlatformAuthorizationIdempotencyConflictException>(async () =>
                await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
            Assert.Equal(1, fixture.Store.QuarantineCount);
            Assert.Equal(1, fixture.Verifier.Calls);
            Assert.Equal(1, fixture.Signer.SignCalls);
        }
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Expired_exact_replay_fails_closed_in_the_authority_itself()
    {
        using var fixture = new IssuerFixture();
        _ = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);
        fixture.Time.Advance(TimeSpan.FromMinutes(11));

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(1, fixture.Verifier.Calls);
        Assert.Equal(1, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Public_envelope_memory_is_a_defensive_clone_not_a_mutable_internal_alias()
    {
        using var fixture = new IssuerFixture();
        var issued = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);
        var issuedView = issued.ExactEnvelopeUtf8;
        Assert.True(MemoryMarshal.TryGetArray(issuedView, out var issuedSegment));
        Assert.NotNull(issuedSegment.Array);
        var issuedOriginal = issuedSegment.Array![issuedSegment.Offset];
        issuedSegment.Array[issuedSegment.Offset] ^= 0x7f;
        Assert.Equal(issuedOriginal, issued.ExactEnvelopeUtf8.Span[0]);

        var receipt = new PlatformAuthorizationExactEnvelopeReceipt(new string('a', 64), new byte[] { 1, 2, 3 }, replayed: false);
        var receiptView = receipt.EnvelopeUtf8;
        Assert.True(MemoryMarshal.TryGetArray(receiptView, out var receiptSegment));
        Assert.NotNull(receiptSegment.Array);
        receiptSegment.Array![receiptSegment.Offset] = 9;
        Assert.Equal(1, receipt.EnvelopeUtf8.Span[0]);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Different_payload_under_same_scope_and_idempotency_key_is_quarantined_fail_closed()
    {
        using var fixture = new IssuerFixture();
        _ = await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation);

        await Assert.ThrowsAsync<PlatformAuthorizationIdempotencyConflictException>(async () =>
            await fixture.Issuer.IssueAsync(
                fixture.Request,
                Encoding.UTF8.GetBytes("different-raw-proof"),
                TestCancellation));
        Assert.Equal(1, fixture.Store.QuarantineCount);
        Assert.Equal(1, fixture.Verifier.Calls);
        Assert.Equal(1, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Verifier_rejection_never_persists_or_signs_an_envelope()
    {
        using var fixture = new IssuerFixture();
        fixture.Verifier.Reject = true;

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Store.StoredCount);
        Assert.Equal(0, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Verified_proof_scope_mismatch_fails_closed_before_signing()
    {
        using var fixture = new IssuerFixture();
        fixture.Verifier.Transform = proof => proof with
        {
            SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Store.StoredCount);
        Assert.Equal(0, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Expired_verified_proof_is_rejected_even_when_its_shape_is_valid()
    {
        using var fixture = new IssuerFixture();
        fixture.Verifier.Transform = proof => proof with
        {
            VerifiedAt = fixture.Time.GetUtcNow().AddMinutes(-5),
            ValidUntil = fixture.Time.GetUtcNow().AddTicks(-1)
        };

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Runtime_bom_generation_change_during_signing_aborts_before_receipt_persistence()
    {
        using var fixture = new IssuerFixture();
        fixture.Runtime.ChangeAfterFirstRead = true;

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Store.StoredCount);
        Assert.Equal(1, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Production_composition_rejects_external_signer_with_wrong_p256_root()
    {
        using var fixture = new IssuerFixture();
        using var external = new TestExternalP256SignatureProvider();
        Assert.Empty(typeof(PlatformAuthorizationEvidenceIssuer).GetConstructors());
        Assert.DoesNotContain(
            typeof(PlatformAuthorizationEvidenceIssuer).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            method => method.Name == "CreateProduction");
        var productionIssuer = PlatformAuthorizationEvidenceIssuer.CreateProduction(
            fixture.Verifier,
            fixture.Runtime,
            fixture.Store,
            external,
            fixture.Time);

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await productionIssuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Store.StoredCount);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Composed_verifier_identity_mutation_fails_closed()
    {
        using var fixture = new IssuerFixture();
        fixture.Verifier.VerifierId = "changed-verifier";

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Verifier.Calls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Occurrence_outside_two_minute_clock_skew_fails_before_proof_verification()
    {
        using var fixture = new IssuerFixture();
        var staleRequest = fixture.Request with { OccurredAt = fixture.Time.GetUtcNow().AddMinutes(-3) };

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(staleRequest, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Verifier.Calls);
        Assert.Equal(0, fixture.Store.StoredCount);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Proof_validity_window_over_fifteen_minutes_is_rejected()
    {
        using var fixture = new IssuerFixture();
        fixture.Verifier.Transform = proof => proof with
        {
            VerifiedAt = fixture.Time.GetUtcNow().AddMinutes(-1),
            ValidUntil = fixture.Time.GetUtcNow().AddMinutes(15)
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
        Assert.Equal(0, fixture.Signer.SignCalls);
    }

    [Fact, Trait("Category", UnitCategory)]
    public async Task Receipt_store_cannot_return_noncanonical_or_tampered_envelope_bytes()
    {
        using var fixture = new IssuerFixture();
        fixture.Store.TamperReturnedBytes = true;

        await Assert.ThrowsAsync<PlatformAuthorizationIssuanceException>(async () =>
            await fixture.Issuer.IssueAsync(fixture.Request, fixture.RawProof, TestCancellation));
    }

    private sealed class IssuerFixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = new(2026, 7, 15, 0, 5, 0, TimeSpan.Zero);

        internal IssuerFixture()
        {
            Time = new AdjustableTimeProvider(Now);
            Verifier = new TestProofVerifier(Time);
            Runtime = new TestRuntimeContextProvider();
            Store = new TestOnlyInMemoryReceiptStore();
            Signer = new TestOnlyEvidenceSigner();
            Issuer = PlatformAuthorizationEvidenceIssuer.CreateForTests(Verifier, Runtime, Store, Signer, Time);
        }

        internal AdjustableTimeProvider Time { get; }
        internal TestProofVerifier Verifier { get; }
        internal TestRuntimeContextProvider Runtime { get; }
        internal TestOnlyInMemoryReceiptStore Store { get; }
        internal TestOnlyEvidenceSigner Signer { get; }
        internal PlatformAuthorizationEvidenceIssuer Issuer { get; }
        internal byte[] RawProof { get; } = Encoding.UTF8.GetBytes("raw-proof-test-fixture-only");
        internal PlatformAuthorizationEvidenceIssueRequest Request { get; } = new(
            "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "pa_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "trace_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "idem_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Now,
            "approval_fixture-1",
            "fixture-app",
            new string('a', 64),
            "tenant-hmac-v1",
            7,
            "authorized",
            1,
            "fixture-proof-v1");

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(RawProof);
            Signer.Dispose();
            Store.Dispose();
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class TestProofVerifier(AdjustableTimeProvider timeProvider) : IPlatformAuthorizationProofVerifier
    {
        internal int Calls { get; private set; }
        internal bool Reject { get; set; }
        internal Func<VerifiedPlatformAuthorizationProof, VerifiedPlatformAuthorizationProof>? Transform { get; set; }
        public string VerifierId { get; set; } = "fixture-proof-verifier";
        public string ProofFormat { get; } = "fixture-proof-v1";

        public ValueTask<VerifiedPlatformAuthorizationProof> VerifyAsync(
            PlatformAuthorizationProofVerificationContext context,
            ReadOnlyMemory<byte> rawProof,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Reject || rawProof.IsEmpty)
                throw new PlatformAuthorizationIssuanceException("Untrusted fixture proof was rejected.");
            var proof = new VerifiedPlatformAuthorizationProof(
                VerifierId,
                ProofFormat,
                context.RawProofSha256,
                context.SoulId,
                context.DeviceBindingId,
                context.PlatformAccountId,
                context.AuthorizationEvidenceId,
                context.Platform,
                context.AliasDigest,
                context.AliasKeyId,
                context.AliasKeyEpoch,
                context.TargetStatus,
                context.AuthorizationRevision,
                timeProvider.GetUtcNow().AddMinutes(-1),
                timeProvider.GetUtcNow().AddMinutes(10));
            return ValueTask.FromResult(Transform?.Invoke(proof) ?? proof);
        }
    }

    private sealed class TestRuntimeContextProvider : ITrustedPlatformAuthorizationRuntimeContextProvider
    {
        internal TrustedPlatformAuthorizationRuntimeContext Context { get; private set; } = new(
            new string('b', 64),
            23,
            5,
            new string('c', 64));
        internal bool ChangeAfterFirstRead { get; set; }
        internal int Calls { get; private set; }

        internal void SetContext(TrustedPlatformAuthorizationRuntimeContext context) => Context = context;

        public ValueTask<TrustedPlatformAuthorizationRuntimeContext> GetActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(ChangeAfterFirstRead && Calls > 1
                ? Context with { ReleaseGeneration = Context.ReleaseGeneration + 1, RuntimeContextSha256 = new string('d', 64) }
                : Context);
        }
    }

    private sealed class TestOnlyEvidenceSigner : IPlatformAuthorizationEvidenceSigner, IDisposable
    {
        private readonly ECDsa _algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal int SignCalls { get; private set; }

        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignCalls++;
            return ValueTask.FromResult(_algorithm.SignData(
                canonicalPayload.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public ValueTask VerifyAsync(
            ReadOnlyMemory<byte> canonicalPayload,
            ReadOnlyMemory<byte> signature,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_algorithm.VerifyData(
                    canonicalPayload.Span,
                    signature.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new PlatformAuthorizationIssuanceException("Test signature verification failed.");
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _algorithm.Dispose();
    }

    private sealed class TestOnlyInMemoryReceiptStore : IDurablePlatformAuthorizationEvidenceReceiptStore, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<PlatformAuthorizationEvidenceReceiptKey, (string PayloadSha256, byte[] Envelope)> _receipts = [];
        public string StoreId { get; set; } = "test-only-in-memory-receipts";
        public long TrustEpoch { get; set; } = 1;
        internal int QuarantineCount { get; private set; }
        internal int StoredCount => _receipts.Count;
        internal bool TamperReturnedBytes { get; set; }

        public async ValueTask<PlatformAuthorizationExactEnvelopeReceipt> GetOrCreateExactAsync(
            PlatformAuthorizationEvidenceReceiptKey key,
            string payloadSha256,
            Func<CancellationToken, ValueTask<byte[]>> createEnvelope,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_receipts.TryGetValue(key, out var existing))
                {
                    if (!string.Equals(existing.PayloadSha256, payloadSha256, StringComparison.Ordinal))
                    {
                        QuarantineCount++;
                        throw new PlatformAuthorizationIdempotencyConflictException(
                            "A different payload attempted to reuse the exact authorization receipt key.");
                    }
                    return Receipt(payloadSha256, existing.Envelope, replayed: true);
                }
                var envelope = await createEnvelope(cancellationToken).ConfigureAwait(false);
                _receipts.Add(key, (payloadSha256, envelope.ToArray()));
                try { return Receipt(payloadSha256, envelope, replayed: false); }
                finally { CryptographicOperations.ZeroMemory(envelope); }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            foreach (var receipt in _receipts.Values) CryptographicOperations.ZeroMemory(receipt.Envelope);
            _gate.Dispose();
        }

        private PlatformAuthorizationExactEnvelopeReceipt Receipt(string payloadSha256, byte[] envelope, bool replayed)
        {
            if (!TamperReturnedBytes) return new PlatformAuthorizationExactEnvelopeReceipt(payloadSha256, envelope, replayed);
            var tampered = new byte[envelope.Length + 1];
            envelope.CopyTo(tampered, 0);
            tampered[^1] = (byte)' ';
            try { return new PlatformAuthorizationExactEnvelopeReceipt(payloadSha256, tampered, replayed); }
            finally { CryptographicOperations.ZeroMemory(tampered); }
        }
    }

    private sealed class TestExternalP256SignatureProvider : IExternalP256SignatureProvider, IDisposable
    {
        private readonly ECDsa _algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string ProviderId => "test-external-signer";
        public string IssuerKeyId => PlatformAuthorizationAuthorityTrustMetadata.CurrentIssuerKeyId;

        public ValueTask<byte[]> ExportSubjectPublicKeyInfoAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_algorithm.ExportSubjectPublicKeyInfo());
        }

        public ValueTask<byte[]> SignSha256P1363Async(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_algorithm.SignData(
                canonicalPayload.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public void Dispose() => _algorithm.Dispose();
    }
}
