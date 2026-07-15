using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dps.WindowsEdgeSupervisor;

/// <summary>
/// Captures every existing component between an approved root and a Worker
/// artifact. Production Windows captures volume/file identifiers from open
/// handles; all platforms reject links/reparse points before and after the
/// capture. Revalidation detects parent replacement as well as file replacement.
/// </summary>
public sealed class SecurePathProof
{
    private readonly PathIdentity[] _identities;

    private SecurePathProof(string approvedRoot, string targetPath, PathIdentity[] identities)
    {
        ApprovedRoot = approvedRoot;
        TargetPath = targetPath;
        _identities = identities;
        IdentitySha256 = ComputeIdentitySha256(identities);
    }

    public string ApprovedRoot { get; }
    public string TargetPath { get; }
    public string IdentitySha256 { get; }

    public static SecurePathProof CaptureDirectory(string approvedRoot, string directoryPath) =>
        Capture(approvedRoot, directoryPath, expectDirectory: true);

    public static SecurePathProof CaptureFile(string approvedRoot, string filePath) =>
        Capture(approvedRoot, filePath, expectDirectory: false);

    public void Revalidate()
    {
        var current = Capture(
            ApprovedRoot,
            TargetPath,
            expectDirectory: _identities[^1].IsDirectory);
        if (!string.Equals(IdentitySha256, current.IdentitySha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "a protected Worker path or one of its parents was replaced after validation");
    }

    private static SecurePathProof Capture(
        string approvedRoot,
        string targetPath,
        bool expectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!Path.IsPathFullyQualified(approvedRoot) || !Path.IsPathFullyQualified(targetPath))
            throw new ArgumentException("protected Worker paths must be absolute");

        var root = Normalize(approvedRoot);
        var target = Normalize(targetPath);
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("protected Worker path is outside the approved root");

        var paths = OperatingSystem.IsWindows()
            ? BuildWindowsAncestorChain(root)
            : new List<string> { root };
        if (relative != ".")
        {
            var current = root;
            foreach (var component in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (component is "." or "..")
                    throw new InvalidOperationException("protected Worker path is not canonical");
                current = Path.Combine(current, component);
                paths.Add(current);
            }
        }

        var identities = new PathIdentity[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            var shouldBeDirectory = index != paths.Count - 1 || expectDirectory;
            identities[index] = CaptureIdentity(paths[index], shouldBeDirectory);
        }
        return new SecurePathProof(root, target, identities);
    }

    private static PathIdentity CaptureIdentity(string path, bool expectDirectory)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
            throw new InvalidOperationException("protected Worker path kind changed during validation");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("links and reparse points are forbidden in protected Worker paths");

        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!string.IsNullOrEmpty(info.LinkTarget) ||
            info.ResolveLinkTarget(returnFinalTarget: false) is not null)
            throw new InvalidOperationException("symbolic links are forbidden in protected Worker paths");

        var identity = OperatingSystem.IsWindows()
            ? WindowsFileIdentity.Capture(path, isDirectory)
            : PortableIdentity(path, info, isDirectory);

        var attributesAfter = File.GetAttributes(path);
        if ((attributesAfter & FileAttributes.ReparsePoint) != 0 ||
            ((attributesAfter & FileAttributes.Directory) != 0) != isDirectory)
            throw new InvalidOperationException("protected Worker path changed during handle capture");
        return identity;
    }

    private static PathIdentity PortableIdentity(
        string path,
        FileSystemInfo info,
        bool isDirectory)
    {
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("protected Worker path disappeared during validation", path);
        var length = info is FileInfo file ? file.Length : 0L;
        return new PathIdentity(
            Normalize(path),
            isDirectory,
            "portable-synthetic",
            info.CreationTimeUtc.Ticks,
            info.LastWriteTimeUtc.Ticks,
            length);
    }

    private static string ComputeIdentitySha256(IEnumerable<PathIdentity> identities)
    {
        var text = string.Join("\n", identities.Select(identity => string.Join(
            "|",
            identity.Path,
            identity.IsDirectory ? "D" : "F",
            identity.NativeIdentity,
            identity.CreationTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (identity.IsDirectory ? 0L : identity.LastWriteTicks)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            (identity.IsDirectory ? 0L : identity.Length)
                .ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static string Normalize(string path) =>
        PreserveVolumeRoot(Path.GetFullPath(path));

    private static string PreserveVolumeRoot(string path)
    {
        var volumeRoot = Path.GetPathRoot(path);
        return string.Equals(path, volumeRoot, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.TrimEndingDirectorySeparator(path);
    }

    private static List<string> BuildWindowsAncestorChain(string approvedRoot)
    {
        var volumeRoot = Path.GetPathRoot(approvedRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot))
            throw new InvalidOperationException("protected Worker path has no Windows volume root");
        var paths = new List<string> { PreserveVolumeRoot(volumeRoot) };
        var relative = Path.GetRelativePath(volumeRoot, approvedRoot);
        if (relative == ".") return paths;
        var current = volumeRoot;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidOperationException("approved Worker root is not canonical");
            current = Path.Combine(current, component);
            paths.Add(PreserveVolumeRoot(current));
        }
        return paths;
    }

    private sealed record PathIdentity(
        string Path,
        bool IsDirectory,
        string NativeIdentity,
        long CreationTicks,
        long LastWriteTicks,
        long Length);

    private static class WindowsFileIdentity
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x80;
        private const uint ShareRead = 0x1;
        private const uint ShareWrite = 0x2;
        private const uint ShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;

        public static PathIdentity Capture(string path, bool isDirectory)
        {
            var access = isDirectory ? FileReadAttributes : GenericRead | FileReadAttributes;
            using var handle = CreateFileW(
                path,
                access,
                ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero,
                OpenExisting,
                (isDirectory ? BackupSemantics : 0) | OpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "unable to open protected Worker path for identity validation");
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "unable to read protected Worker path identity");
            if (!isDirectory && information.NumberOfLinks != 1)
                throw new InvalidOperationException(
                    "hard-linked Worker files are forbidden in protected paths");

            var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            var native = information.VolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) +
                ":" + fileIndex.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            var creationTicks = ToLong(information.CreationTime);
            var lastWriteTicks = ToLong(information.LastWriteTime);
            var length = isDirectory
                ? 0L
                : unchecked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow));
            return new PathIdentity(Normalize(path), isDirectory, native, creationTicks, lastWriteTicks, length);
        }

        private static long ToLong(NativeFileTime value) =>
            unchecked((long)(((ulong)value.High << 32) | value.Low));

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public NativeFileTime CreationTime;
            public NativeFileTime LastAccessTime;
            public NativeFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
