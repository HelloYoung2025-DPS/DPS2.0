using Dps.ControlPlaneHost.Contracts;
using Xunit;
using static Dps.ControlPlaneHost.Tests.ReleaseBindingRecoveryTestKit;

namespace Dps.ControlPlaneHost.Tests;

/// <summary>
/// Deterministic acknowledgement-loss coverage for the release-binding
/// authority. The backing journal commits the transition and then models a
/// timeout before the caller receives the receipt. An exact retry must recover
/// the durable record through the production replay validator and return its
/// original receipt without appending a second row.
/// </summary>
public sealed class ReleaseBindingDurableExactRetryTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("activation")]
    [InlineData("revocation")]
    [InlineData("rollback")]
    public void ExactRetryAfterCommitAcknowledgementLossReturnsDurableReceipt(
        string transitionKind)
    {
        using var signer = new BomSigner();
        var store = new CommitThenTimeoutTruthStore();
        var authority = new ActiveReleaseBindingAuthority(
            [signer.TrustKey],
            store,
            () => Now);
        var (exactRequest, conflictingRequest) =
            ArrangeTransition(transitionKind, authority, signer);
        var rowsBefore = store.LoadAll().Count;

        store.TimeoutAfterNextCommit();
        Assert.Throws<TimeoutException>(() => exactRequest());

        var committedRows = store.LoadAll();
        Assert.Equal(rowsBefore + 1, committedRows.Count);
        var committedReceipt = committedRows[^1].Receipt;

        var replayed = exactRequest();

        Assert.Equal(committedReceipt, replayed);
        Assert.Equal(rowsBefore + 1, store.LoadAll().Count);
        Assert.Equal(
            committedReceipt,
            authority.ReadReceipts(Device)[^1]);

        // The durable exact-result recovery is not a conflict bypass:
        // a different request at the same postcondition still fails closed
        // and cannot append another journal row.
        Assert.Throws<ActiveReleaseBindingException>(() => conflictingRequest());
        Assert.Equal(rowsBefore + 1, store.LoadAll().Count);
    }

    private static (
        Func<ReleaseBindingReceiptV1> ExactRequest,
        Func<ReleaseBindingReceiptV1> ConflictingRequest)
        ArrangeTransition(
            string transitionKind,
            ActiveReleaseBindingAuthority authority,
            BomSigner signer)
    {
        switch (transitionKind)
        {
            case "activation":
            {
                var (bom, token) = signer.SignBom("bom-target", 1, null);
                var (conflictingBom, conflictingToken) =
                    signer.SignBom("bom-conflict", 1, null);
                return (
                    () => authority.Activate(Device, bom, token),
                    () => authority.Activate(
                        Device,
                        conflictingBom,
                        conflictingToken));
            }
            case "revocation":
            {
                var (bom, token) = signer.SignBom("bom-active", 1, null);
                authority.Activate(Device, bom, token);
                return (
                    () => authority.Revoke(Device, 1),
                    () => authority.Revoke(Device, 2));
            }
            case "rollback":
            {
                var (firstBom, firstToken) =
                    signer.SignBom("bom-first", 1, null);
                authority.Activate(Device, firstBom, firstToken);
                var (secondBom, secondToken) =
                    signer.SignBom("bom-second", 2, firstBom);
                authority.Activate(
                    Device,
                    secondBom,
                    signer.StableTwin(firstBom),
                    secondToken);
                authority.Revoke(Device, 2);
                return (
                    () => authority.Rollback(Device, firstToken),
                    () => authority.Rollback(Device, Token("wrong-rollback")));
            }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transitionKind),
                    transitionKind,
                    "unknown transition kind");
        }
    }

    private sealed class CommitThenTimeoutTruthStore
        : IReleaseBindingTruthStore,
          IActiveReleaseBindingRecoveryCoordinator
    {
        private readonly InMemoryReleaseBindingTruthStore _inner =
            InMemoryReleaseBindingTruthStore.CreateTestOnly();
        private int _timeoutAfterNextCommit;

        internal void TimeoutAfterNextCommit()
            => Interlocked.Exchange(ref _timeoutAfterNextCommit, 1);

        public void Append(ReleaseBindingTruthRecord record)
            => _inner.Append(record);

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll()
            => _inner.LoadAll();

        public long LoadDeviceHeadSequence(string deviceBindingId)
            => _inner.LoadDeviceHeadSequence(deviceBindingId);

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
            string deviceBindingId,
            long afterSequence)
            => _inner.LoadAfter(deviceBindingId, afterSequence);

        public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
            string deviceBindingId,
            long afterSequence)
            => _inner.LoadSnapshotAfter(deviceBindingId, afterSequence);

        public IReleaseBindingTransitionScope BeginTransition(
            string deviceBindingId)
            => new CommitThenTimeoutScope(
                this,
                _inner.BeginTransition(deviceBindingId));

        ValueTask<IActiveReleaseBindingRecoveryScope>
            IActiveReleaseBindingRecoveryCoordinator.AcquireAsync(
                string deviceBindingId,
                CancellationToken cancellationToken)
            => ((IActiveReleaseBindingRecoveryCoordinator)_inner).AcquireAsync(
                deviceBindingId,
                cancellationToken);

        private sealed class CommitThenTimeoutScope(
            CommitThenTimeoutTruthStore owner,
            IReleaseBindingTransitionScope inner)
            : IReleaseBindingTransitionScope
        {
            public void Append(ReleaseBindingTruthRecord record)
            {
                inner.Append(record);
                if (Interlocked.Exchange(
                        ref owner._timeoutAfterNextCommit,
                        0) == 1)
                {
                    throw new TimeoutException(
                        "simulated response loss after the journal commit");
                }
            }

            public void Dispose() => inner.Dispose();
        }
    }
}
