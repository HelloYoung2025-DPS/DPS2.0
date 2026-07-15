using System.Security.Cryptography;
using System.Text;

namespace Dps.WindowsEdgeSupervisor;

/// <summary>
/// Immutable RSA public-key store loaded from an approved directory. A key is
/// addressable only by the SHA-256 fingerprint of its SubjectPublicKeyInfo, so
/// a caller cannot attach an arbitrary key to a trusted name at verification
/// time.
/// </summary>
public sealed class PinnedRsaTrustStore : IDisposable
{
    private const string KeyIdPrefix = "sha256_";
    private readonly IReadOnlyDictionary<string, RSA> _keys;

    private PinnedRsaTrustStore(IReadOnlyDictionary<string, RSA> keys)
    {
        _keys = keys;
        StoreFingerprint = ComputeSha256(
            Encoding.UTF8.GetBytes(string.Join("\n", keys.Keys.Order(StringComparer.Ordinal))));
    }

    public string StoreFingerprint { get; }
    internal IEnumerable<string> KeyIds => _keys.Keys;

    public static PinnedRsaTrustStore LoadFromDirectory(
        string approvedTrustRoot,
        IEnumerable<string> allowedKeyIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedTrustRoot);
        ArgumentNullException.ThrowIfNull(allowedKeyIds);

        var root = ResolveDirectory(Path.GetFullPath(approvedTrustRoot));
        var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
        try
        {
            foreach (var keyId in allowedKeyIds.Distinct(StringComparer.Ordinal))
            {
                ValidateKeyId(keyId);
                var lexicalPath = Path.Combine(root, keyId + ".pem");
                var physicalPath = ResolveFile(lexicalPath);
                if (!IsWithin(physicalPath, root))
                {
                    throw new InvalidOperationException("trusted key resolved outside the approved trust root");
                }

                var pem = File.ReadAllText(physicalPath);
                if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("the trust store accepts public keys only");
                }

                var key = RSA.Create();
                try
                {
                    key.ImportFromPem(pem);
                    var actualKeyId = ComputeKeyId(key.ExportSubjectPublicKeyInfo());
                    if (!string.Equals(actualKeyId, keyId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("trusted key content does not match its pinned key id");
                    }

                    keys.Add(keyId, key);
                }
                catch
                {
                    key.Dispose();
                    throw;
                }
            }

            if (keys.Count == 0)
            {
                throw new InvalidOperationException("at least one pinned trust key is required");
            }

            return new PinnedRsaTrustStore(keys);
        }
        catch
        {
            foreach (var key in keys.Values) key.Dispose();
            throw;
        }
    }

    public static string ComputeKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        KeyIdPrefix + ComputeSha256(subjectPublicKeyInfo);

    public bool VerifyPssSha256Base64(
        string keyId,
        ReadOnlySpan<byte> statement,
        string signatureBase64)
        => VerifyBase64(keyId, statement, signatureBase64, RSASignaturePadding.Pss);

    public bool VerifyPkcs1Sha256Base64(
        string keyId,
        ReadOnlySpan<byte> statement,
        string signatureBase64)
        => VerifyBase64(keyId, statement, signatureBase64, RSASignaturePadding.Pkcs1);

    internal RSA CloneRequiredPublicKey(string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var key))
            throw new CryptographicException("the requested RSA key is not present in the pinned trust store");
        var subjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo();
        try
        {
            var clone = RSA.Create();
            try
            {
                clone.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
                if (bytesRead != subjectPublicKeyInfo.Length)
                    throw new CryptographicException("pinned RSA public key contains trailing data");
                return clone;
            }
            catch
            {
                clone.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
        }
    }

    private bool VerifyBase64(
        string keyId,
        ReadOnlySpan<byte> statement,
        string signatureBase64,
        RSASignaturePadding padding)
    {
        if (!_keys.TryGetValue(keyId, out var key)) return false;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(Convert.ToBase64String(signature), signatureBase64, StringComparison.Ordinal))
        {
            return false;
        }

        return key.VerifyData(
            statement,
            signature,
            HashAlgorithmName.SHA256,
            padding);
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId) ||
            keyId.Length != KeyIdPrefix.Length + 64 ||
            !keyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal) ||
            !keyId.AsSpan(KeyIdPrefix.Length).ToString().All(
                character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("trusted key id must be sha256_<64 lowercase hex characters>");
        }
    }

    private static string ResolveDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
    }

    private static string ResolveFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("pinned trust key is missing", path);
        return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
