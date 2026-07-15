using Dps.EvidenceService.Contracts;

namespace Dps.EvidenceService;

public enum EvidenceStoreDisposition
{
    Stored,
    DuplicateNoOp,
    Quarantined
}

public sealed record EvidenceStoreResult(
    EvidenceStoreDisposition Disposition,
    Guid EvidenceId,
    string BundleChecksum);

public sealed record EvidenceDigestRecord(
    Guid EvidenceId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string ModuleId,
    string Status,
    string VerificationLevel,
    string BaselineCommit,
    string InstructionReceiptSha256,
    string ReceiptSha256,
    string ArtifactSetSha256,
    string SourceReceiptSetSha256,
    string RunnerKeyId,
    string AttestationAlgorithm,
    DateTimeOffset AttestationIssuedAt,
    string AttestationSha256,
    string BundleChecksum,
    DateTimeOffset OccurredAt);

public interface IEvidenceStore
{
    Task<EvidenceStoreResult> SaveAsync(EvidenceBundle bundle, CancellationToken cancellationToken = default);
}

public sealed class EvidenceSubmissionService
{
    private readonly IEvidenceStore _store;
    private readonly CryptographicRunnerAttestationVerifier _attestationVerifier;

    public EvidenceSubmissionService(
        IEvidenceStore store,
        CryptographicRunnerAttestationVerifier attestationVerifier)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _attestationVerifier = attestationVerifier ?? throw new ArgumentNullException(nameof(attestationVerifier));
    }

    public Task<EvidenceStoreResult> SubmitAsync(
        EvidenceSubmissionCandidate candidate,
        SignedRunnerAttestationV1 attestation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(attestation);
        _attestationVerifier.Verify(candidate, attestation);
        var bundle = EvidenceBundle.CreateVerified(candidate, attestation);
        return _store.SaveAsync(bundle, cancellationToken);
    }
}

public sealed class InMemoryEvidenceStore : IEvidenceStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, EvidenceDigestRecord> _records = [];
    private readonly List<(Guid EvidenceId, string ExistingChecksum, string IncomingChecksum)> _quarantine = [];

    public Task<EvidenceStoreResult> SaveAsync(EvidenceBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var receipt = bundle.Receipt;
            if (_records.TryGetValue(receipt.EvidenceId, out var existing))
            {
                if (string.Equals(existing.BundleChecksum, bundle.Checksum, StringComparison.Ordinal))
                {
                    return Task.FromResult(new EvidenceStoreResult(
                        EvidenceStoreDisposition.DuplicateNoOp,
                        receipt.EvidenceId,
                        bundle.Checksum));
                }

                _quarantine.Add((receipt.EvidenceId, existing.BundleChecksum, bundle.Checksum));
                return Task.FromResult(new EvidenceStoreResult(
                    EvidenceStoreDisposition.Quarantined,
                    receipt.EvidenceId,
                    bundle.Checksum));
            }

            _records.Add(receipt.EvidenceId, ToDigestRecord(bundle));
            return Task.FromResult(new EvidenceStoreResult(
                EvidenceStoreDisposition.Stored,
                receipt.EvidenceId,
                bundle.Checksum));
        }
    }

    public EvidenceDigestRecord? Read(
        Guid evidenceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId)
    {
        lock (_sync)
        {
            var record = _records.GetValueOrDefault(evidenceId);
            return record is not null &&
                   string.Equals(record.SoulId, soulId, StringComparison.Ordinal) &&
                   string.Equals(record.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) &&
                   string.Equals(record.PlatformAccountId, platformAccountId, StringComparison.Ordinal)
                ? record
                : null;
        }
    }

    public int CountForSoul(string soulId)
    {
        lock (_sync)
        {
            return _records.Values.Count(item => string.Equals(item.SoulId, soulId, StringComparison.Ordinal));
        }
    }

    public int QuarantineCount
    {
        get
        {
            lock (_sync)
            {
                return _quarantine.Count;
            }
        }
    }

    private static EvidenceDigestRecord ToDigestRecord(EvidenceBundle bundle)
    {
        var receipt = bundle.Receipt;
        return new EvidenceDigestRecord(
            receipt.EvidenceId,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            receipt.TraceId,
            receipt.ModuleId,
            receipt.Status,
            receipt.VerificationLevel,
            receipt.BaselineCommit,
            receipt.InstructionReceiptSha256,
            bundle.ReceiptSha256,
            bundle.ArtifactSetSha256,
            bundle.SourceReceiptSetSha256,
            bundle.Attestation.Facts.RunnerKeyId,
            bundle.Attestation.Facts.Algorithm,
            bundle.Attestation.Facts.IssuedAt,
            bundle.AttestationSha256,
            bundle.Checksum,
            receipt.OccurredAt);
    }
}
