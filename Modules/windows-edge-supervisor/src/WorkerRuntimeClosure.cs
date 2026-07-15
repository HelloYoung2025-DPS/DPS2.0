using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor;

public sealed record WorkerRuntimeManifestEntryV1(
    [property: JsonPropertyName("relative_path"), JsonRequired] string RelativePath,
    [property: JsonPropertyName("sha256"), JsonRequired] string Sha256);

public sealed record WorkerRuntimeManifestV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("files"), JsonRequired] WorkerRuntimeManifestEntryV1[] Files);

/// <summary>
/// Strict, signed inventory for the complete immutable Worker application base.
/// The manifest itself is signed by digest but is deliberately not recursively
/// listed inside its own file array.
/// </summary>
public static class WorkerRuntimeManifestCodec
{
    public const string SchemaVersion = "dps.worker-runtime-manifest/v1";
    public const int MaximumFileCount = 256;
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    public const long MaximumClosureBytes = 1024L * 1024 * 1024;
    public const int MaximumManifestBytes = 256 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex RelativePathPattern = new(
        "^[A-Za-z0-9_+.-]+(?:/[A-Za-z0-9_+.-]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static byte[] Create(string versionDirectory, IEnumerable<string> filePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);
        ArgumentNullException.ThrowIfNull(filePaths);
        var root = NormalizeDirectory(versionDirectory);
        var entries = filePaths.Select(path =>
        {
            var full = Path.GetFullPath(path);
            var relative = ToManifestRelativePath(root, full);
            var digest = HashFileBounded(full, MaximumFileBytes);
            return new WorkerRuntimeManifestEntryV1(relative, digest);
        }).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
        ValidateEntries(entries);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new WorkerRuntimeManifestV1(SchemaVersion, entries),
            JsonOptions);
        if (bytes.Length > MaximumManifestBytes)
            throw new InvalidDataException("Worker runtime manifest exceeds its fixed size limit");
        return bytes;
    }

    internal static WorkerRuntimeManifestV1 Decode(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumManifestBytes)
            throw new InvalidDataException("Worker runtime manifest size is outside the allowed range");
        WorkerRuntimeManifestV1 manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WorkerRuntimeManifestV1>(utf8Json, JsonOptions) ??
                throw new InvalidDataException("Worker runtime manifest is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Worker runtime manifest JSON is invalid", exception);
        }
        if (manifest.SchemaVersion != SchemaVersion || manifest.Files is null)
            throw new InvalidDataException("unknown Worker runtime manifest identity");
        ValidateEntries(manifest.Files);
        return manifest;
    }

    internal static string ToManifestRelativePath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Worker runtime file is outside its version directory");
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    internal static string ResolveManifestPath(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var full = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        _ = ToManifestRelativePath(root, full);
        return full;
    }

    internal static string HashStreamBounded(Stream stream, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || stream.Length < 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Worker runtime file size is outside the allowed range");
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException("Worker runtime file exceeded its allowed size while hashing");
            hash.AppendData(buffer, 0, read);
        }
        stream.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string HashFileBounded(string path, long maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return HashStreamBounded(stream, maximumBytes);
    }

    internal static void RequireSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 ||
            !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException(field + " is not canonical lowercase SHA-256");
    }

    private static void ValidateEntries(WorkerRuntimeManifestEntryV1[] entries)
    {
        if (entries.Length is < 1 or > MaximumFileCount || entries.Any(entry => entry is null))
            throw new InvalidDataException("Worker runtime manifest file set is empty, oversized, or contains null");
        var prior = string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            ValidateRelativePath(entry.RelativePath);
            RequireSha256(entry.Sha256, "Worker runtime file digest");
            if (!seen.Add(entry.RelativePath) ||
                prior.Length != 0 && StringComparer.Ordinal.Compare(prior, entry.RelativePath) >= 0)
                throw new InvalidDataException("Worker runtime manifest paths are duplicated, case-colliding, or unsorted");
            prior = entry.RelativePath;
            total = checked(total + MaximumFileBytes);
            if (total / MaximumFileBytes > MaximumFileCount)
                throw new InvalidDataException("Worker runtime manifest total bound overflowed");
        }
    }

    private static void ValidateRelativePath(string? value)
    {
        if (value is null || value.Length is < 1 or > 512 ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            !RelativePathPattern.IsMatch(value) ||
            value.Split('/').Any(segment => segment is "." or ".." || segment.Length > 128))
            throw new InvalidDataException("Worker runtime manifest path is not canonical and relative");
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>
/// A complete proof of the signed runtime closure. Every regular file and
/// every subdirectory is inventoried; extra/missing/case-colliding content is
/// rejected. Windows additionally requires a fixed immutable directory DACL
/// whose owner/writers are provisioning identities, never the runtime token.
/// </summary>
public sealed class WorkerRuntimeClosureProof
{
    private readonly string _versionDirectory;
    private readonly string _manifestPath;
    private readonly string _manifestSha256;
    private readonly string _directorySecuritySha256;
    private readonly SecurePathProof[] _pathProofs;
    private readonly RuntimeFile[] _files;
    private readonly string[] _expectedDirectories;

    private WorkerRuntimeClosureProof(
        string versionDirectory,
        string manifestPath,
        string manifestSha256,
        string directorySecuritySha256,
        SecurePathProof[] pathProofs,
        RuntimeFile[] files,
        string[] expectedDirectories)
    {
        _versionDirectory = versionDirectory;
        _manifestPath = manifestPath;
        _manifestSha256 = manifestSha256;
        _directorySecuritySha256 = directorySecuritySha256;
        _pathProofs = pathProofs;
        _files = files;
        _expectedDirectories = expectedDirectories;
        IdentitySha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            manifestSha256,
            directorySecuritySha256,
            string.Join("\n", pathProofs.Select(proof => proof.IdentitySha256))))));
    }

    public string IdentitySha256 { get; }

    public static string CaptureDirectorySecuritySha256(string versionDirectory) =>
        ImmutableWorkerDirectorySecurity.CaptureTreeAndValidate(versionDirectory);

    public static WorkerRuntimeClosureProof Capture(string approvedRoot, WorkerArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!Enum.IsDefined(artifact.Slot))
            throw new InvalidDataException("Worker slot must be A or B");
        WorkerRuntimeManifestCodec.RequireSha256(artifact.RuntimeManifestSha256, "runtime manifest digest");
        WorkerRuntimeManifestCodec.RequireSha256(
            artifact.VersionDirectorySecuritySha256,
            "version directory security digest");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        var version = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifact.VersionDirectory));
        var versionProof = SecurePathProof.CaptureDirectory(root, version);
        var manifestPath = Path.GetFullPath(artifact.RuntimeManifestPath);
        var manifestRelative = WorkerRuntimeManifestCodec.ToManifestRelativePath(version, manifestPath);
        var manifestProof = SecurePathProof.CaptureFile(version, manifestPath);
        byte[] manifestBytes;
        using (var stream = new FileStream(
                   manifestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > WorkerRuntimeManifestCodec.MaximumManifestBytes)
                throw new InvalidDataException("Worker runtime manifest size is outside the allowed range");
            manifestBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(manifestBytes);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != artifact.RuntimeManifestSha256)
            throw new InvalidDataException("Worker runtime manifest digest does not match the signed artifact");
        var manifest = WorkerRuntimeManifestCodec.Decode(manifestBytes);
        if (manifest.Files.Any(entry =>
                string.Equals(entry.RelativePath, manifestRelative, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Worker runtime manifest cannot recursively list itself");

        var expectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtimeFiles = new List<RuntimeFile>(manifest.Files.Length + 1);
        var proofs = new List<SecurePathProof> { versionProof, manifestProof };
        long totalBytes = manifestBytes.Length;
        foreach (var entry in manifest.Files)
        {
            var full = WorkerRuntimeManifestCodec.ResolveManifestPath(version, entry.RelativePath);
            if (!expectedFiles.TryAdd(full, entry.Sha256))
                throw new InvalidDataException("Worker runtime manifest contains a case-colliding file");
            var proof = SecurePathProof.CaptureFile(version, full);
            var info = new FileInfo(full);
            info.Refresh();
            if (info.Length < 0 || info.Length > WorkerRuntimeManifestCodec.MaximumFileBytes)
                throw new InvalidDataException("Worker runtime file exceeds its per-file size limit");
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > WorkerRuntimeManifestCodec.MaximumClosureBytes)
                throw new InvalidDataException("Worker runtime closure exceeds its total size limit");
            if (WorkerRuntimeManifestCodec.HashFileBounded(
                    full,
                    WorkerRuntimeManifestCodec.MaximumFileBytes) != entry.Sha256)
                throw new InvalidDataException("Worker runtime file digest does not match its signed manifest");
            runtimeFiles.Add(new RuntimeFile(full, entry.Sha256));
            proofs.Add(proof);
        }

        RequireArtifactMember(artifact.BinaryPath, artifact.Sha256, expectedFiles, "binary");
        RequireArtifactMember(
            artifact.HealthEvidencePath,
            artifact.HealthEvidenceSha256,
            expectedFiles,
            "health evidence");
        RequireArtifactMember(
            artifact.ShadowEvidencePath,
            artifact.ShadowEvidenceSha256,
            expectedFiles,
            "shadow evidence");
        var expectedDirectories = DeriveExpectedDirectories(version, manifestPath, expectedFiles.Keys);
        ValidateExactInventory(version, manifestPath, expectedFiles.Keys, expectedDirectories);
        var directorySecuritySha256 = ImmutableWorkerDirectorySecurity.CaptureTreeAndValidate(version);
        if (directorySecuritySha256 != artifact.VersionDirectorySecuritySha256)
            throw new InvalidDataException("Worker version directory security descriptor changed");
        foreach (var proof in proofs) proof.Revalidate();
        return new WorkerRuntimeClosureProof(
            version,
            manifestPath,
            artifact.RuntimeManifestSha256,
            directorySecuritySha256,
            proofs.ToArray(),
            runtimeFiles.ToArray(),
            expectedDirectories);
    }

    public void Revalidate()
    {
        foreach (var proof in _pathProofs) proof.Revalidate();
        ValidateExactInventory(
            _versionDirectory,
            _manifestPath,
            _files.Select(file => file.Path),
            _expectedDirectories);
        if (ImmutableWorkerDirectorySecurity.CaptureTreeAndValidate(_versionDirectory) !=
            _directorySecuritySha256)
            throw new InvalidOperationException("Worker version directory security descriptor changed");
    }

    internal LockedWorkerRuntimeClosure LockForLaunch()
    {
        Revalidate();
        var streams = new List<FileStream>(_files.Length + 1);
        try
        {
            streams.Add(OpenAndVerify(
                _manifestPath,
                _manifestSha256,
                WorkerRuntimeManifestCodec.MaximumManifestBytes));
            foreach (var file in _files)
                streams.Add(OpenAndVerify(file.Path, file.Sha256, WorkerRuntimeManifestCodec.MaximumFileBytes));
            Revalidate();
            return new LockedWorkerRuntimeClosure(this, streams.ToArray());
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenAndVerify(string path, string expectedSha256, long maximumBytes)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        try
        {
            if (WorkerRuntimeManifestCodec.HashStreamBounded(stream, maximumBytes) != expectedSha256)
                throw new InvalidDataException("Worker runtime file changed before launch");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void RequireArtifactMember(
        string path,
        string digest,
        IReadOnlyDictionary<string, string> files,
        string role)
    {
        var full = Path.GetFullPath(path);
        if (!files.TryGetValue(full, out var listed) || listed != digest)
            throw new InvalidDataException("Worker " + role + " is absent from or mismatched with the signed closure");
    }

    private static string[] DeriveExpectedDirectories(
        string root,
        string manifestPath,
        IEnumerable<string> files)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Append(manifestPath))
        {
            var current = Path.GetDirectoryName(file);
            while (current is not null && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                if (!directories.Add(current)) break;
                current = Path.GetDirectoryName(current);
            }
        }
        return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateExactInventory(
        string root,
        string manifestPath,
        IEnumerable<string> expectedFiles,
        IEnumerable<string> expectedDirectories)
    {
        var expectedFileSet = new HashSet<string>(expectedFiles.Append(manifestPath), StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(expectedFileSet))
            throw new InvalidOperationException("Worker runtime directory contains a missing or unsigned extra file");
        var expectedDirectorySet = expectedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualDirectories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualDirectories.SetEquals(expectedDirectorySet))
            throw new InvalidOperationException("Worker runtime directory contains a missing or unsigned extra directory");
        foreach (var directory in actualDirectories)
            _ = SecurePathProof.CaptureDirectory(root, directory);
    }

    private sealed record RuntimeFile(string Path, string Sha256);
}

internal sealed class LockedWorkerRuntimeClosure : IDisposable
{
    private readonly WorkerRuntimeClosureProof _proof;
    private readonly FileStream[] _streams;
    private bool _disposed;

    public LockedWorkerRuntimeClosure(WorkerRuntimeClosureProof proof, FileStream[] streams)
    {
        _proof = proof;
        _streams = streams;
    }

    public string IdentitySha256 => _proof.IdentitySha256;

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _proof.Revalidate();
        if (_streams.Any(stream => !stream.CanRead))
            throw new InvalidOperationException("Worker runtime closure lock was lost");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var stream in _streams.Reverse()) stream.Dispose();
    }
}

internal static class ImmutableWorkerDirectorySecurity
{
    private static readonly string SystemSid = "S-1-5-18";
    private static readonly string AdministratorsSid = "S-1-5-32-544";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string CaptureTreeAndValidate(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Prepend(root)
            .OrderBy(directory => Path.GetRelativePath(root, directory), StringComparer.Ordinal)
            .ToArray();
        var statement = new StringBuilder("dps.worker-runtime-directory-security-tree/v1\n");
        foreach (var directory in directories)
        {
            var relative = string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
                ? "."
                : Path.GetRelativePath(root, directory)
                    .Replace(Path.DirectorySeparatorChar, '/');
            statement.Append(relative)
                .Append(':')
                .Append(CaptureSingleAndValidate(directory))
                .Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(statement.ToString())));
    }

    private static string CaptureSingleAndValidate(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(
                "portable-synthetic-unverified-directory-security/v1\n" + full)));
        return CaptureWindows(full);
    }

    [SupportedOSPlatform("windows")]
    private static string CaptureWindows(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        var binary = security.GetSecurityDescriptorBinaryForm();
        var raw = new RawSecurityDescriptor(binary, 0);
        if (raw.DiscretionaryAcl is null)
            throw new InvalidOperationException("Worker version directory cannot have a null DACL");
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsProvisioningIdentity(owner.Value))
            throw new InvalidOperationException("Worker version directory owner is not a provisioning identity");

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var runtimePrincipals = new HashSet<string>(StringComparer.Ordinal)
        {
            identity.User?.Value ?? string.Empty
        };
        if (identity.Groups is not null)
        {
            foreach (var group in identity.Groups) runtimePrincipals.Add(group.Value);
        }
        if (runtimePrincipals.Contains(SystemSid) || runtimePrincipals.Contains(AdministratorsSid))
            throw new InvalidOperationException(
                "Supervisor/Worker runtime token cannot be an artifact provisioning identity");

        const FileSystemRights mutationRights =
            FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (var rule in rules.OfType<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & mutationRights) == 0)
                continue;
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!IsProvisioningIdentity(sid))
                throw new InvalidOperationException(
                    "Worker version directory grants mutation to a runtime/untrusted identity");
        }
        return Convert.ToHexStringLower(SHA256.HashData(binary));
    }

    private static bool IsProvisioningIdentity(string sid) =>
        sid == SystemSid || sid == AdministratorsSid;
}
