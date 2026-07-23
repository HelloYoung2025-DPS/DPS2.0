using Dps.ControlPlaneHost.Contracts;
using Xunit;
using static Dps.ControlPlaneHost.Tests.ReleaseBindingRecoveryTestKit;

namespace Dps.ControlPlaneHost.Tests;

/// <summary>
/// F2 revision-fence behavior over the real in-memory truth store with all
/// binding state established through the production API (Activate / Revoke /
/// Rollback / CreateRecoveryAsync). The PostgreSQL twin of every scenario
/// lives in PostgresReleaseBindingTruthStoreIntegrationTests.
/// </summary>
public sealed class ReleaseBindingRecoveryFenceTests
{
    private static (ActiveReleaseBindingAuthority Authority, InMemoryReleaseBindingTruthStore Store)
        ActivatedAuthority(BomSigner signer, out string bomSha256, out long generation, out byte[] bom)
    {
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);
        string token;
        (bom, token) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var binding));
        bomSha256 = binding!.ReleaseBomSha256;
        generation = binding.Generation;
        return (authority, store);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task RecoveryCommitsTheFenceAndRedeliveryReplaysIdempotently()
    {
        using var signer = new BomSigner();
        var (authority, store) = ActivatedAuthority(
            signer, out var bomSha256, out var generation, out _);
        using var harness = new RecoveryLifecycleHarness(
            new PolicyBoundReleaseBomFactsSource(authority),
            store);
        var request = RecoveryRequest(bomSha256, generation);

        var first = await harness.RecoverAsync(request);
        Assert.Equal(bomSha256, first.Value.NextReleaseBomSha256);
        Assert.Equal(generation, first.Value.NextReleaseBomGeneration);

        // Crash window ② (fence committed, response lost): redelivery
        // re-signs, but the fence keys on recovery_id plus the deterministic
        // pre-signature content digest, so the replay commits idempotently.
        var redelivered = await harness.RecoverAsync(request);
        Assert.Equal(2, harness.RecoverySigner.CallCount);
        Assert.Equal(
            first.Value with { SignatureBase64 = ZeroSignature },
            redelivered.Value with { SignatureBase64 = ZeroSignature });

        // A distinct concurrent recovery at the same unadvanced binding
        // revision is its own fence row, not a conflict.
        var sibling = await harness.RecoverAsync(RecoveryRequest(
            bomSha256,
            generation,
            Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa")));
        Assert.Equal(bomSha256, sibling.Value.NextReleaseBomSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task FenceRefusesTheSignedEnvelopeWhenActivationLandsDuringSigning()
    {
        using var signer = new BomSigner();
        var (authority, store) = ActivatedAuthority(
            signer, out var bomSha256, out var generation, out var firstBom);

        // Frozen facts snapshot: the producer's pre/post facts re-reads stay
        // stale on purpose so the database-committed fence is the only gate
        // left — exactly the crash window ① race (fence issued, binding
        // advances, commit must refuse).
        Assert.True(authority.TryReadActive(Device, out var snapshot));
        using var harness = new RecoveryLifecycleHarness(
            new PolicyBoundReleaseBomFactsSource(new FrozenReader(snapshot!)),
            store);
        harness.RecoverySigner.WhileSigning = () =>
        {
            var (nextBom, nextToken) = signer.SignBom("bom-2", 2, firstBom);
            authority.Activate(
                Device,
                nextBom,
                signer.StableTwin(firstBom),
                nextToken);
        };

        await Assert.ThrowsAsync<ReleaseBindingRecoveryFenceConflictException>(() =>
            harness.RecoverAsync(RecoveryRequest(bomSha256, generation)));
        Assert.Equal(1, harness.RecoverySigner.CallCount);

        // The refused issuance left no residue: a fresh recovery pinned to
        // the advanced binding succeeds.
        Assert.True(authority.TryReadActive(Device, out var advanced));
        using var liveHarness = new RecoveryLifecycleHarness(
            new PolicyBoundReleaseBomFactsSource(authority),
            store);
        var recovered = await liveHarness.RecoverAsync(RecoveryRequest(
            advanced!.ReleaseBomSha256,
            advanced.Generation,
            Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb")));
        Assert.Equal(advanced.ReleaseBomSha256, recovered.Value.NextReleaseBomSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task FenceRefusesTheSignedEnvelopeWhenRevocationLandsDuringSigning()
    {
        using var signer = new BomSigner();
        var (authority, store) = ActivatedAuthority(
            signer, out var bomSha256, out var generation, out _);
        Assert.True(authority.TryReadActive(Device, out var snapshot));
        using var harness = new RecoveryLifecycleHarness(
            new PolicyBoundReleaseBomFactsSource(new FrozenReader(snapshot!)),
            store);
        harness.RecoverySigner.WhileSigning = () =>
            authority.Revoke(Device, generation);

        await Assert.ThrowsAsync<ReleaseBindingRecoveryFenceConflictException>(() =>
            harness.RecoverAsync(RecoveryRequest(bomSha256, generation)));
        Assert.Equal(1, harness.RecoverySigner.CallCount);

        // After revocation no fence can even be issued: revocation leaves no
        // recovery path, matching the anti-rollback posture.
        await Assert.ThrowsAsync<ActiveReleaseBindingException>(() =>
            harness.RecoverAsync(RecoveryRequest(bomSha256, generation)));
    }

    [Fact, Trait("Category", "Unit")]
    public void FenceStoreSemanticsFollowTheJournalHead()
    {
        using var signer = new BomSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey], store, () => Now);

        // No journal yet: issuance fails closed.
        Assert.Throws<ActiveReleaseBindingException>(
            () => store.IssueRecoveryFence(Device));

        var (bom1, token1) = signer.SignBom("bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var fence = store.IssueRecoveryFence(Device);
        Assert.Equal(1, fence.JournalSequence);
        Assert.Equal(Sha256Hex(bom1), fence.ReleaseBomSha256);

        var recoveryId = Guid.Parse("cccccccc-3333-4333-8333-cccccccccccc");
        var content = new string('a', 64);
        store.CommitRecoveryFence(fence, recoveryId, content);
        // Idempotent redelivery of the exact same content.
        store.CommitRecoveryFence(fence, recoveryId, content);
        // Same recovery id with different content fails closed.
        Assert.Throws<ReleaseBindingRecoveryFenceConflictException>(
            () => store.CommitRecoveryFence(fence, recoveryId, new string('b', 64)));

        // The journal advances (activation then rollback): the stale fence
        // loses its commit; a freshly issued fence commits at the new head.
        var (bom2, token2) = signer.SignBom("bom-2", 2, bom1);
        authority.Activate(Device, bom2, signer.StableTwin(bom1), token2);
        var staleFence = store.IssueRecoveryFence(Device);
        authority.Rollback(Device, token1);
        Assert.Throws<ReleaseBindingRecoveryFenceConflictException>(
            () => store.CommitRecoveryFence(
                staleFence,
                Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd"),
                content));
        var rolledBackFence = store.IssueRecoveryFence(Device);
        Assert.Equal(3, rolledBackFence.JournalSequence);
        Assert.Equal(Sha256Hex(bom1), rolledBackFence.ReleaseBomSha256);
        store.CommitRecoveryFence(
            rolledBackFence,
            Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd"),
            content);

        // Revocation closes the fence surface entirely.
        Assert.True(authority.TryReadActive(Device, out var active));
        authority.Revoke(Device, active!.Generation);
        Assert.Throws<ActiveReleaseBindingException>(
            () => store.IssueRecoveryFence(Device));
    }

    private sealed class FrozenReader(ActiveReleaseBindingV1 binding)
        : IActiveReleaseBindingReader
    {
        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? value)
        {
            value = binding;
            return true;
        }
    }
}
