using System.Security.Cryptography;
using System.Text;

namespace Dps.PlatformAuthorizationAuthority.Contracts;

public static class PlatformAuthorizationContractValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported {name} '{actual}'.");
    }

    public static void RequireText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsSurrogate(character))
                throw new ArgumentException($"{name} contains a forbidden control or surrogate character.", name);
        }
        _ = StrictUtf8.GetByteCount(value);
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException($"{name} must be a non-default UTC timestamp.", name);
    }

    public static void RequireSoulId(string value) => RequirePrefixedHex(value, "soul_", 64, nameof(value));
    public static void RequireDeviceBindingId(string value) => RequirePrefixedHex(value, "db_", 32, nameof(value));
    public static void RequirePlatformAccountId(string value) => RequirePrefixedHex(value, "pa_", 32, nameof(value));
    public static void RequireTraceId(string value) => RequirePrefixedHex(value, "trace_", 32, nameof(value));
    public static void RequireIdempotencyKey(string value) => RequirePrefixedHex(value, "idem_", 64, nameof(value));
    public static void RequireSha256(string value, string name) => RequirePrefixedHex(value, string.Empty, 64, name);

    public static void RequireIdentifier(string value, string name)
    {
        RequireText(value, 64, name);
        var previousWasSeparator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (IsAsciiLower(character) || IsAsciiDigit(character))
            {
                previousWasSeparator = false;
                continue;
            }
            var isSeparator = character is '.' or '_' or '-';
            if (!isSeparator || index == 0 || index == value.Length - 1 || previousWasSeparator)
                throw new ArgumentException($"{name} must be a normalized lowercase ASCII identifier.", name);
            previousWasSeparator = true;
        }
    }

    public static void RequireKeyId(string value, string name)
    {
        RequireText(value, 64, name);
        if ((!IsAsciiLower(value[0]) && !IsAsciiDigit(value[0])) || value.Any(static character =>
                !IsAsciiLower(character) && !IsAsciiDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException($"Invalid {name}.", name);
    }

    public static void RequireAuthorizationEvidenceId(string value)
    {
        RequireText(value, 128, nameof(value));
        if (!value.StartsWith("approval_", StringComparison.Ordinal) || value.Length <= 9 ||
            value.AsSpan(9).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_-"))
            throw new ArgumentException("Invalid authorization_evidence_id.", nameof(value));
    }

    public static void RequireStatus(string value, string name)
    {
        if (value is not ("authorized" or "revoked" or "suspended"))
            throw new ArgumentOutOfRangeException(name);
    }

    public static void RequirePrefixedHex(string value, string prefix, int hexLength, string name)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + hexLength ||
            value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{name} is not a canonical lowercase hexadecimal identifier.", name);
    }

    public static void RequireCanonicalP256P1363Signature(string value, string name)
    {
        RequireText(value, 88, name);
        if (value.Length != 88 || !value.EndsWith("==", StringComparison.Ordinal) ||
            value.AsSpan(0, 86).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"))
            throw new ArgumentException($"{name} is not canonical Base64 for a 64-byte P-256 P1363 signature.", name);
        byte[] signature;
        try { signature = Convert.FromBase64String(value); }
        catch (FormatException exception)
        {
            throw new ArgumentException($"{name} is not valid Base64.", name, exception);
        }
        try
        {
            if (signature.Length != 64 || !string.Equals(Convert.ToBase64String(signature), value, StringComparison.Ordinal))
                throw new ArgumentException($"{name} is not canonical Base64 for a 64-byte P-256 P1363 signature.", name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';
    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
