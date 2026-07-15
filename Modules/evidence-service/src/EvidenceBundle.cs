using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Dps.EvidenceService.Contracts;

namespace Dps.EvidenceService;

public sealed class RawEvidenceArtifact
{
    private readonly ImmutableArray<byte> _content;

    private RawEvidenceArtifact(string artifactId, ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            throw new ArgumentException("ArtifactId is required.", nameof(artifactId));
        }

        ArtifactId = artifactId;
        _content = ImmutableArray.Create(content.ToArray());
    }

    public string ArtifactId { get; }

    public long SizeBytes => _content.Length;

    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(_content.AsSpan()));

    internal ReadOnlySpan<byte> Content => _content.AsSpan();

    public static RawEvidenceArtifact FromBytes(string artifactId, ReadOnlySpan<byte> content)
        => new(artifactId, content);

    public static RawEvidenceArtifact FromUtf8(string artifactId, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new RawEvidenceArtifact(artifactId, Encoding.UTF8.GetBytes(content));
    }
}

/// <summary>
/// An untrusted, immutable submission candidate. Creating a candidate validates
/// the receipt and raw artifact digests, but does not make the evidence trusted.
/// Only a separately signed runner attestation can promote it to a bundle.
/// </summary>
public sealed class EvidenceSubmissionCandidate
{
    private EvidenceSubmissionCandidate(
        TestEvidenceV1 receipt,
        ImmutableArray<RawEvidenceArtifact> rawArtifacts,
        string receiptSha256,
        string artifactSetSha256,
        string sourceReceiptSetSha256)
    {
        Receipt = receipt;
        RawArtifacts = rawArtifacts;
        ReceiptSha256 = receiptSha256;
        ArtifactSetSha256 = artifactSetSha256;
        SourceReceiptSetSha256 = sourceReceiptSetSha256;
    }

    public TestEvidenceV1 Receipt { get; }

    public IReadOnlyList<RawEvidenceArtifact> RawArtifacts { get; }

    public string ReceiptSha256 { get; }

    public string ArtifactSetSha256 { get; }

    public string SourceReceiptSetSha256 { get; }

    public static EvidenceSubmissionCandidate Create(
        TestEvidenceV1 receipt,
        IEnumerable<RawEvidenceArtifact> rawArtifacts)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(rawArtifacts);
        receipt.Validate();

        var immutableReceipt = receipt with
        {
            Artifacts = ImmutableArray.CreateRange(receipt.Artifacts
                .OrderBy(static item => item.ArtifactId, StringComparer.Ordinal)
                .ThenBy(static item => item.Sha256, StringComparer.Ordinal)),
            SourceReceipts = ImmutableArray.CreateRange(receipt.SourceReceipts
                .OrderBy(static item => item.ContractId, StringComparer.Ordinal)
                .ThenBy(static item => item.ProducerModule, StringComparer.Ordinal)
                .ThenBy(static item => item.Sha256, StringComparer.Ordinal))
        };
        var immutableRaw = ImmutableArray.CreateRange(rawArtifacts);

        ValidateRawArtifacts(immutableReceipt.Artifacts, immutableRaw);
        return new EvidenceSubmissionCandidate(
            immutableReceipt,
            immutableRaw,
            TestEvidenceCanonicalizer.ComputeSha256(immutableReceipt),
            ComputeArtifactSetSha256(immutableReceipt.Artifacts),
            ComputeSourceReceiptSetSha256(immutableReceipt.SourceReceipts));
    }

    private static void ValidateRawArtifacts(
        IReadOnlyList<EvidenceArtifactV1> declaredArtifacts,
        IEnumerable<RawEvidenceArtifact> rawArtifacts)
    {
        var rawById = new Dictionary<string, RawEvidenceArtifact>(StringComparer.Ordinal);
        foreach (var rawArtifact in rawArtifacts)
        {
            ArgumentNullException.ThrowIfNull(rawArtifact);
            if (!rawById.TryAdd(rawArtifact.ArtifactId, rawArtifact))
            {
                throw new ArgumentException("Raw artifact identifiers must be unique.", nameof(rawArtifacts));
            }
        }

        if (rawById.Count != declaredArtifacts.Count)
        {
            throw new InvalidOperationException("Every declared artifact must have exactly one raw artifact, with no extras.");
        }

        foreach (var declared in declaredArtifacts)
        {
            if (!rawById.TryGetValue(declared.ArtifactId, out var raw))
            {
                throw new InvalidOperationException($"Raw artifact '{declared.ArtifactId}' is missing.");
            }

            if (declared.SizeBytes != raw.SizeBytes ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(declared.Sha256),
                    SHA256.HashData(raw.Content)))
            {
                throw new InvalidOperationException($"Raw artifact '{declared.ArtifactId}' does not match its declared digest and size.");
            }
        }
    }

    private static string ComputeArtifactSetSha256(IEnumerable<EvidenceArtifactV1> artifacts)
    {
        var canonical = string.Join(
            "\n",
            artifacts.Select(static item =>
                $"{item.ArtifactId}\t{item.Sha256}\t{item.SizeBytes}\t{item.MediaType}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeSourceReceiptSetSha256(IEnumerable<SourceReceiptDigestV1> receipts)
    {
        var canonical = string.Join(
            "\n",
            receipts.Select(static item => $"{item.ContractId}\t{item.ProducerModule}\t{item.Sha256}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class EvidenceBundle
{
    private EvidenceBundle(
        EvidenceSubmissionCandidate candidate,
        SignedRunnerAttestationV1 attestation,
        string attestationSha256,
        string checksum)
    {
        Receipt = candidate.Receipt;
        RawArtifacts = candidate.RawArtifacts;
        ReceiptSha256 = candidate.ReceiptSha256;
        ArtifactSetSha256 = candidate.ArtifactSetSha256;
        SourceReceiptSetSha256 = candidate.SourceReceiptSetSha256;
        Attestation = attestation;
        AttestationSha256 = attestationSha256;
        Checksum = checksum;
    }

    public TestEvidenceV1 Receipt { get; }

    internal IReadOnlyList<RawEvidenceArtifact> RawArtifacts { get; }

    public string ReceiptSha256 { get; }

    public string ArtifactSetSha256 { get; }

    public string SourceReceiptSetSha256 { get; }

    public SignedRunnerAttestationV1 Attestation { get; }

    public string AttestationSha256 { get; }

    public string Checksum { get; }

    internal static EvidenceBundle CreateVerified(
        EvidenceSubmissionCandidate candidate,
        SignedRunnerAttestationV1 attestation)
    {
        var attestationSha256 = RunnerAttestationCanonicalizer.ComputeSha256(attestation);
        var canonical = string.Join(
            "\n",
            candidate.ReceiptSha256,
            candidate.ArtifactSetSha256,
            candidate.SourceReceiptSetSha256);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new EvidenceBundle(candidate, attestation, attestationSha256, checksum);
    }
}
