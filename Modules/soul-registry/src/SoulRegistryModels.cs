using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dps.SoulRegistry;

public enum IdentityAliasKind
{
    Email,
    Phone,
    PlatformId
}

public sealed class AliasHmacKey : IDisposable
{
    private static readonly Regex KeyIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant);
    private readonly byte[] _keyBytes;
    private bool _disposed;

    public AliasHmacKey(string keyId, ReadOnlySpan<byte> keyBytes)
    {
        if (string.IsNullOrWhiteSpace(keyId) || !KeyIdPattern.IsMatch(keyId))
        {
            throw new ArgumentException("Alias HMAC key id is invalid.", nameof(keyId));
        }

        if (keyBytes.Length < 32)
        {
            throw new ArgumentException("Alias HMAC keys must contain at least 256 bits.", nameof(keyBytes));
        }

        KeyId = keyId;
        _keyBytes = keyBytes.ToArray();
    }

    public string KeyId { get; }

    internal byte[] CopyKeyBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _keyBytes.ToArray();
    }

    public override string ToString() => $"{nameof(AliasHmacKey)} {{ KeyId = {KeyId}, KeyBytes = [REDACTED] }}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_keyBytes);
        _disposed = true;
    }
}

public sealed record SoulRegistryOptions(
    string ConnectionString,
    string SchemaName,
    string CurrentKeyId,
    IReadOnlyList<AliasHmacKey> AliasKeys)
{
    private static readonly Regex SchemaPattern = new("^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(ConnectionString));
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || !SchemaPattern.IsMatch(SchemaName))
        {
            throw new ArgumentException("SchemaName must be a safe lowercase PostgreSQL identifier.", nameof(SchemaName));
        }

        ArgumentNullException.ThrowIfNull(AliasKeys);
        if (AliasKeys.Count == 0 || AliasKeys.Any(static key => key is null))
        {
            throw new ArgumentException("At least one alias HMAC key is required.", nameof(AliasKeys));
        }

        if (AliasKeys.Select(static key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != AliasKeys.Count)
        {
            throw new ArgumentException("Alias HMAC key ids must be unique.", nameof(AliasKeys));
        }

        if (!AliasKeys.Any(key => string.Equals(key.KeyId, CurrentKeyId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("CurrentKeyId must identify an active alias HMAC key.", nameof(CurrentKeyId));
        }
    }

    public override string ToString()
        => $"{nameof(SoulRegistryOptions)} {{ ConnectionString = [REDACTED], SchemaName = {SchemaName}, CurrentKeyId = {CurrentKeyId}, AliasKeys = [REDACTED] }}";
}

public sealed record AliasVerification(string EvidenceId, DateTimeOffset VerifiedAt, bool Verified = true)
{
    public override string ToString()
        => $"{nameof(AliasVerification)} {{ EvidenceId = [REDACTED], VerifiedAt = {VerifiedAt:O}, Verified = {Verified} }}";
}

public sealed record RegisterVerifiedAliasRequest(
    string SchemaVersion,
    string TenantId,
    IdentityAliasKind AliasKind,
    string RawAlias,
    AliasVerification Verification,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string? TargetSoulId = null)
{
    public const string CurrentSchemaVersion = "1.0.0";

    public override string ToString()
        => $"{nameof(RegisterVerifiedAliasRequest)} {{ SchemaVersion = {SchemaVersion}, TenantId = {TenantId}, AliasKind = {AliasKind}, RawAlias = [REDACTED], Verification = [REDACTED], DeviceBindingId = {DeviceBindingId}, PlatformAccountId = {PlatformAccountId}, TraceId = {TraceId}, IdempotencyKey = {IdempotencyKey}, OccurredAt = {OccurredAt:O}, TargetSoulId = {TargetSoulId} }}";
}

public sealed record ResolveSoulRequest(
    string SchemaVersion,
    string TenantId,
    IdentityAliasKind AliasKind,
    string RawAlias,
    AliasVerification Verification,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt)
{
    public const string CurrentSchemaVersion = "1.0.0";

    public override string ToString()
        => $"{nameof(ResolveSoulRequest)} {{ SchemaVersion = {SchemaVersion}, TenantId = {TenantId}, AliasKind = {AliasKind}, RawAlias = [REDACTED], Verification = [REDACTED], DeviceBindingId = {DeviceBindingId}, PlatformAccountId = {PlatformAccountId}, TraceId = {TraceId}, IdempotencyKey = {IdempotencyKey}, OccurredAt = {OccurredAt:O} }}";
}

public sealed record RevokeAliasRequest(
    string SchemaVersion,
    string TenantId,
    IdentityAliasKind AliasKind,
    string RawAlias,
    string ExpectedSoulId,
    string Reason,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt)
{
    public const string CurrentSchemaVersion = "1.0.0";

    public override string ToString()
        => $"{nameof(RevokeAliasRequest)} {{ SchemaVersion = {SchemaVersion}, TenantId = {TenantId}, AliasKind = {AliasKind}, RawAlias = [REDACTED], ExpectedSoulId = {ExpectedSoulId}, Reason = [REDACTED], TraceId = {TraceId}, IdempotencyKey = {IdempotencyKey}, OccurredAt = {OccurredAt:O} }}";
}

public enum SoulRegistryMutationStage
{
    RevokePersistedBeforeCommit
}

public delegate ValueTask SoulRegistryFaultInjector(
    SoulRegistryMutationStage stage,
    CancellationToken cancellationToken);

public sealed record AliasMetadata(
    string AliasKind,
    string AliasDigest,
    string AliasKeyId,
    DateTimeOffset VerifiedAt,
    DateTimeOffset? RevokedAt);

public class SoulRegistryException(string message) : InvalidOperationException(message);
public sealed class AliasNotFoundException() : SoulRegistryException("The verified identity alias was not found.");
public sealed class AliasRevokedException() : SoulRegistryException("The verified identity alias has been revoked.");
public sealed class AmbiguousAliasException() : SoulRegistryException("The verified identity alias is ambiguous.");
public sealed class AliasConflictException() : SoulRegistryException("The verified identity alias belongs to a different Soul.");
public sealed class IdempotencyConflictException() : SoulRegistryException("The idempotency key was reused for a different identity request.");
public sealed class CrossTenantIdentityException() : SoulRegistryException("The Soul does not belong to the requested tenant.");

internal readonly record struct AliasReference(string Kind, string Digest, string KeyId);

internal static class SecretComparison
{
    public static bool EqualsHex(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
