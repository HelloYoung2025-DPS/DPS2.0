using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.AuditMetrics.Contracts;
using Npgsql;

namespace Dps.AuditMetrics;

public sealed record AuditRelayTrustStateEnvelope(
    string SchemaVersion,
    string ContractId,
    Guid StateId,
    long Revision,
    string ActiveReleaseBomSha256,
    string RelayKeyId,
    string RelayPublicKeySha256,
    string RelayKeyStatus,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    string SignatureBase64)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "audit.relay-trust-state/v1";
    public const string Active = "ACTIVE";
    public const string Revoked = "REVOKED";
}

public sealed record VerifiedAuditRelayTrustState(
    Guid StateId,
    long Revision,
    string ActiveReleaseBomSha256,
    string RelayKeyId,
    string RelayPublicKeySha256,
    string RelayKeyStatus,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil);

public sealed class SignedAuditRelayTrustStateReader : IDisposable
{
    private const string CanonicalDomain = "dps.audit.relay-trust-state/v1";
    private readonly object _sync = new();
    private readonly ECDsa _rootPublicKey;
    private readonly PostgresAuditRelayTrustStateSource _source;
    private long _highestRevision;
    private string? _highestStateSha256;

    public SignedAuditRelayTrustStateReader(
        ReadOnlySpan<byte> rootSubjectPublicKeyInfo,
        PostgresAuditRelayTrustStateSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _rootPublicKey = ImportP256(rootSubjectPublicKeyInfo, nameof(rootSubjectPublicKeyInfo));
        _source = source;
    }

    public async ValueTask<VerifiedAuditRelayTrustState> ReadCurrentAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var state = await _source.ReadCurrentAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("Authoritative relay trust-state source returned no state.");
        return VerifyCurrent(state, now);
    }

    internal async ValueTask<VerifiedAuditRelayTrustState> ReadCurrentAsync(
        DateTimeOffset now,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var state = await _source.ReadCurrentAsync(connection, transaction, cancellationToken)
            ?? throw new UnauthorizedAccessException("Authoritative relay trust-state source returned no state.");
        return VerifyCurrent(state, now);
    }

    private VerifiedAuditRelayTrustState VerifyCurrent(
        AuditRelayTrustStateEnvelope state,
        DateTimeOffset now)
    {
        ValidateShape(state, now);
        byte[] signature;
        try { signature = Convert.FromBase64String(state.SignatureBase64); }
        catch (FormatException exception)
        {
            throw new UnauthorizedAccessException("Trust-state signature is not valid Base64.", exception);
        }

        var canonical = CanonicalBytes(state);
        var stateSha256 = Convert.ToHexStringLower(SHA256.HashData(canonical));
        bool verified;
        try
        {
            lock (_sync)
            {
                verified = _rootPublicKey.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }

        if (!verified)
        {
            throw new UnauthorizedAccessException("Trust-state signature verification failed.");
        }

        lock (_sync)
        {
            if (state.Revision < _highestRevision
                || (state.Revision == _highestRevision
                    && _highestStateSha256 is not null
                    && !AuditDigest.FixedEquals(stateSha256, _highestStateSha256)))
            {
                throw new UnauthorizedAccessException("Authoritative relay trust state attempted a revision rollback or rewrite.");
            }

            _highestRevision = state.Revision;
            _highestStateSha256 = stateSha256;
        }

        return new VerifiedAuditRelayTrustState(
            state.StateId,
            state.Revision,
            state.ActiveReleaseBomSha256,
            state.RelayKeyId,
            state.RelayPublicKeySha256,
            state.RelayKeyStatus,
            state.ValidFrom,
            state.ValidUntil);
    }

    public static byte[] CanonicalBytes(AuditRelayTrustStateEnvelope state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stream = new MemoryStream();
        WriteField(stream, CanonicalDomain);
        WriteField(stream, state.SchemaVersion);
        WriteField(stream, state.ContractId);
        WriteField(stream, state.StateId.ToString("N"));
        WriteField(stream, state.Revision.ToString(CultureInfo.InvariantCulture));
        WriteField(stream, state.ActiveReleaseBomSha256);
        WriteField(stream, state.RelayKeyId);
        WriteField(stream, state.RelayPublicKeySha256);
        WriteField(stream, state.RelayKeyStatus);
        WriteField(stream, state.ValidFrom.ToString("O", CultureInfo.InvariantCulture));
        WriteField(stream, state.ValidUntil.ToString("O", CultureInfo.InvariantCulture));
        return stream.ToArray();
    }

    public void Dispose() => _rootPublicKey.Dispose();

    private static void ValidateShape(AuditRelayTrustStateEnvelope state, DateTimeOffset now)
    {
        AuditContractGuard.RequireUtc(now, nameof(now));
        AuditContractGuard.RequireExact(
            state.SchemaVersion,
            AuditRelayTrustStateEnvelope.CurrentSchemaVersion,
            nameof(state.SchemaVersion));
        AuditContractGuard.RequireExact(
            state.ContractId,
            AuditRelayTrustStateEnvelope.CurrentContractId,
            nameof(state.ContractId));
        AuditContractGuard.RequireGuid(state.StateId, nameof(state.StateId));
        if (state.Revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(state.Revision));
        }

        AuditContractGuard.RequireSha256(state.ActiveReleaseBomSha256, nameof(state.ActiveReleaseBomSha256));
        AuditContractGuard.RequireOpaqueMetadata(state.RelayKeyId, 128, nameof(state.RelayKeyId));
        AuditContractGuard.RequireSha256(state.RelayPublicKeySha256, nameof(state.RelayPublicKeySha256));
        if (state.RelayKeyStatus is not (AuditRelayTrustStateEnvelope.Active or AuditRelayTrustStateEnvelope.Revoked))
        {
            throw new NotSupportedException($"Unknown relay key status '{state.RelayKeyStatus}'.");
        }

        AuditContractGuard.RequireUtc(state.ValidFrom, nameof(state.ValidFrom));
        AuditContractGuard.RequireUtc(state.ValidUntil, nameof(state.ValidUntil));
        if (state.ValidUntil <= state.ValidFrom || now < state.ValidFrom || now >= state.ValidUntil)
        {
            throw new UnauthorizedAccessException("Signed relay trust state is not currently valid.");
        }
    }

    private static ECDsa ImportP256(ReadOnlySpan<byte> subjectPublicKeyInfo, string parameterName)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length)
            {
                throw new ArgumentException("Public key contains trailing bytes.", parameterName);
            }

            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (!string.Equals(parameters.Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal)
                || parameters.Q.X is not { Length: 32 }
                || parameters.Q.Y is not { Length: 32 })
            {
                throw new ArgumentException("Trust root public key must use NIST P-256.", parameterName);
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static void WriteField(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
