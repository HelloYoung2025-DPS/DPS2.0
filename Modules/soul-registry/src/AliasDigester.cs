using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dps.SoulRegistry;

internal sealed class AliasDigester : IDisposable
{
    private readonly IReadOnlyList<KeyEntry> _keys;
    private readonly KeyEntry _currentKey;

    public AliasDigester(SoulRegistryOptions options)
    {
        _keys = options.AliasKeys
            .Select(static key => new KeyEntry(key.KeyId, key.CopyKeyBytes()))
            .OrderBy(static key => key.KeyId, StringComparer.Ordinal)
            .ToArray();
        _currentKey = _keys.Single(key => string.Equals(key.KeyId, options.CurrentKeyId, StringComparison.Ordinal));
    }

    public AliasReference CurrentReference(string tenantId, IdentityAliasKind kind, string rawAlias)
        => ComputeReference(_currentKey, tenantId, kind, Normalize(kind, rawAlias));

    public IReadOnlyList<AliasReference> AllReferences(string tenantId, IdentityAliasKind kind, string rawAlias)
    {
        var canonical = Normalize(kind, rawAlias);
        return _keys.Select(key => ComputeReference(key, tenantId, kind, canonical)).ToArray();
    }

    private static AliasReference ComputeReference(
        KeyEntry key,
        string tenantId,
        IdentityAliasKind kind,
        string canonicalAlias)
    {
        var input = Encoding.UTF8.GetBytes($"dps.alias/v1\n{tenantId.Length}:{tenantId}\n{KindName(kind)}\n{canonicalAlias}");
        var digest = HMACSHA256.HashData(key.KeyBytes, input);
        try
        {
            return new AliasReference(KindName(kind), Convert.ToHexStringLower(digest), key.KeyId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static string Normalize(IdentityAliasKind kind, string rawAlias)
    {
        if (string.IsNullOrWhiteSpace(rawAlias))
        {
            throw new ArgumentException("Identity alias is invalid.", nameof(rawAlias));
        }

        return kind switch
        {
            IdentityAliasKind.Email => NormalizeEmail(rawAlias),
            IdentityAliasKind.Phone => NormalizePhone(rawAlias),
            IdentityAliasKind.PlatformId => NormalizePlatformId(rawAlias),
            _ => throw new NotSupportedException("Unknown identity alias kind was rejected.")
        };
    }

    internal static string KindName(IdentityAliasKind kind) => kind switch
    {
        IdentityAliasKind.Email => "email",
        IdentityAliasKind.Phone => "phone",
        IdentityAliasKind.PlatformId => "platform_id",
        _ => throw new NotSupportedException("Unknown identity alias kind was rejected.")
    };

    private static string NormalizeEmail(string rawAlias)
    {
        var normalized = rawAlias.Trim().Normalize(NormalizationForm.FormKC);
        var at = normalized.LastIndexOf('@');
        if (at <= 0 || at == normalized.Length - 1 || normalized.IndexOf('@') != at || normalized.Length > 320)
        {
            throw new ArgumentException("Identity alias is invalid.", nameof(rawAlias));
        }

        var local = normalized[..at].ToLowerInvariant();
        string domain;
        try
        {
            domain = new IdnMapping().GetAscii(normalized[(at + 1)..]).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Identity alias is invalid.", nameof(rawAlias));
        }

        if (local.Length is 0 or > 64 || domain.Length is 0 or > 255 || !domain.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("Identity alias is invalid.", nameof(rawAlias));
        }

        return $"{local}@{domain}";
    }

    private static string NormalizePhone(string rawAlias)
    {
        var compact = new string(rawAlias
            .Trim()
            .Normalize(NormalizationForm.FormKC)
            .Where(static character => !char.IsWhiteSpace(character) && character is not ('-' or '(' or ')' or '.'))
            .ToArray());

        if (compact.Length is < 9 or > 16 || compact[0] != '+' || compact[1..].Any(static character => character is < '0' or > '9'))
        {
            throw new ArgumentException("Identity alias is invalid.", nameof(rawAlias));
        }

        return compact;
    }

    private static string NormalizePlatformId(string rawAlias)
    {
        var normalized = rawAlias.Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length is 0 or > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Identity alias is invalid.", nameof(rawAlias));
        }

        return normalized;
    }

    private sealed record KeyEntry(string KeyId, byte[] KeyBytes);

    public void Dispose()
    {
        foreach (var key in _keys)
        {
            CryptographicOperations.ZeroMemory(key.KeyBytes);
        }
    }
}
