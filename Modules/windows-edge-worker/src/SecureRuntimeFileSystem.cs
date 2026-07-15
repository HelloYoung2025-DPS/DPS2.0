using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dps.WindowsEdgeWorker;

internal static class SecureRuntimeFileSystem
{
    private const int MaximumPathCharacters = 1024;
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int NativeStatBufferBytes = 512;
    private const uint UnixPermissionBitsMask = 0x0FFF;
    private const uint PrivateDirectoryPermissionBits = 0x01C0;
    private const uint PrivateFilePermissionBits = 0x0180;
    private const int UnixErrorNoEntry = 2;
    private const int UnixErrorNotDirectory = 20;

    public static string PrepareDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("worker runtime directory must be an absolute path");
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > MaximumPathCharacters)
            throw new PathTooLongException("worker runtime directory exceeds the hard path limit");

        if (!Directory.Exists(fullPath))
        {
            var parent = Directory.GetParent(fullPath) ??
                throw new InvalidDataException("worker runtime directory must have an existing parent");
            if (!parent.Exists)
                throw new DirectoryNotFoundException("worker runtime directory parent does not exist");
            RejectReparsePoints(parent.FullName, includeLeaf: true);
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(fullPath);
            else
                Directory.CreateDirectory(fullPath, PrivateDirectoryMode);
        }

        RejectReparsePoints(fullPath, includeLeaf: true);
        if (!OperatingSystem.IsWindows())
        {
            var identity = ReadRequiredUnixPathIdentity(fullPath);
            if (!identity.IsDirectory)
                throw new IOException("worker runtime directory must name a directory without following links");
            VerifyEffectiveUserOwns(identity, "worker runtime directory");
            if ((identity.Mode & UnixPermissionBitsMask) != PrivateDirectoryPermissionBits)
                throw new UnauthorizedAccessException(
                    "worker runtime directory must have exact owner-only 0700 permissions");
        }

        return fullPath;
    }

    public static void VerifyExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            var identity = TryReadUnixPathIdentity(fullPath);
            if (identity is null)
                return;
            if (!identity.Value.IsRegularFile)
                throw new IOException(
                    "worker runtime file must be a regular file without following links");
            if (identity.Value.LinkCount != 1)
                throw new IOException("worker runtime files cannot have hard-link aliases");
            VerifyEffectiveUserOwns(identity.Value, "worker runtime file");
            if ((identity.Value.Mode & UnixPermissionBitsMask) != PrivateFilePermissionBits)
                throw new UnauthorizedAccessException(
                    "worker runtime file must have exact owner-only 0600 permissions");
            return;
        }

        if (!File.Exists(fullPath))
        {
            var missing = new FileInfo(fullPath);
            if (missing.LinkTarget is not null)
                throw new IOException("worker runtime file cannot be a symbolic link");
            return;
        }

        RejectFileReparsePoint(fullPath);
    }

    public static FileStream OpenOrCreatePrivateFile(
        string path,
        FileAccess access,
        FileShare share,
        FileOptions options = FileOptions.None,
        int bufferSize = 4096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > MaximumPathCharacters)
            throw new PathTooLongException("worker runtime file exceeds the hard path limit");
        VerifyExistingFile(fullPath);
        var existed = File.Exists(fullPath);

        var streamOptions = CreatePrivateStreamOptions(
            existed ? FileMode.Open : FileMode.CreateNew,
            access,
            share,
            options,
            bufferSize);

        FileStream? stream = null;
        try
        {
            try
            {
                stream = new FileStream(fullPath, streamOptions);
            }
            catch (IOException) when (!existed && File.Exists(fullPath))
            {
                VerifyExistingFile(fullPath);
                streamOptions = CreatePrivateStreamOptions(
                    FileMode.Open,
                    access,
                    share,
                    options,
                    bufferSize);
                stream = new FileStream(fullPath, streamOptions);
            }
            VerifyExistingFile(fullPath);
            _ = VerifyOpenFileIdentity(stream, fullPath);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public static void WritePrivateFileCreateNew(string path, ReadOnlySpan<byte> bytes)
    {
        VerifyExistingFile(path);
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.WriteThrough,
            BufferSize = 4096
        };
        if (!OperatingSystem.IsWindows())
            streamOptions.UnixCreateMode = PrivateFileMode;
        using var stream = new FileStream(path, streamOptions);
        _ = VerifyOpenFileIdentity(stream, Path.GetFullPath(path));
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        _ = VerifyOpenFileIdentity(stream, Path.GetFullPath(path));
    }

    public static void WritePrivateFileCreateNewOrVerify(
        string path,
        ReadOnlySpan<byte> bytes,
        int maximumBytes)
    {
        if (bytes.Length > maximumBytes)
            throw new InvalidDataException("worker crash-isolation artifact exceeds its hard byte limit");
        try
        {
            WritePrivateFileCreateNew(path, bytes);
            return;
        }
        catch (IOException) when (File.Exists(path))
        {
            VerifyExistingFile(path);
            using var stream = OpenOrCreatePrivateFile(
                path,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            if (stream.Length != bytes.Length || stream.Length > maximumBytes)
                throw new InvalidDataException(
                    "existing worker crash-isolation artifact does not match the interrupted write");
            var existing = GC.AllocateUninitializedArray<byte>(bytes.Length);
            stream.ReadExactly(existing);
            if (stream.ReadByte() != -1)
                throw new InvalidDataException(
                    "existing worker crash-isolation artifact grew beyond the interrupted write");
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException(
                    "existing worker crash-isolation artifact does not match the interrupted write");
        }
    }

    public static RuntimeFileIdentity? CaptureOpenFileIdentity(FileStream stream, string path) =>
        VerifyOpenFileIdentity(stream, Path.GetFullPath(path));

    public static void VerifyOpenFileIdentity(
        FileStream stream,
        string path,
        RuntimeFileIdentity? expectedIdentity)
    {
        var actual = VerifyOpenFileIdentity(stream, Path.GetFullPath(path));
        if (expectedIdentity is not null && actual != expectedIdentity)
            throw new IOException("worker runtime file handle identity changed");
    }

    private static RuntimeFileIdentity? VerifyOpenFileIdentity(FileStream stream, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsWindows())
            return null;
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "worker runtime file identity verification supports only probed Unix hosts");
        if (stream.SafeFileHandle.IsInvalid || stream.SafeFileHandle.IsClosed)
            throw new IOException("worker runtime file handle is unavailable for identity validation");

        var handleBuffer = Marshal.AllocHGlobal(NativeStatBufferBytes);
        var pathBuffer = Marshal.AllocHGlobal(NativeStatBufferBytes);
        try
        {
            ZeroNativeBuffer(handleBuffer);
            ZeroNativeBuffer(pathBuffer);
            var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (NativeFStat(descriptor, handleBuffer) != 0)
                throw NativeIdentityException("fstat failed for an open worker runtime file");
            if (NativeLStat(fullPath, pathBuffer) != 0)
                throw NativeIdentityException("lstat failed for the current worker runtime path");

            var handleIdentity = ReadNativeIdentity(handleBuffer);
            var pathIdentity = ReadNativeIdentity(pathBuffer);
            if (!handleIdentity.IsRegularFile || !pathIdentity.IsRegularFile)
                throw new IOException("worker runtime paths must name regular files without following links");
            VerifyEffectiveUserOwns(handleIdentity, "open worker runtime file");
            if (handleIdentity != pathIdentity)
                throw new IOException(
                    "worker runtime path no longer names the file held by the open handle");
            if (handleIdentity.LinkCount != 1)
                throw new IOException("worker runtime files cannot have hard-link aliases");
            return handleIdentity;
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuffer);
            Marshal.FreeHGlobal(handleBuffer);
        }
    }

    private static RuntimeFileIdentity ReadNativeIdentity(IntPtr buffer)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new RuntimeFileIdentity(
                unchecked((uint)Marshal.ReadInt32(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((ushort)Marshal.ReadInt16(buffer, 6)),
                unchecked((ushort)Marshal.ReadInt16(buffer, 4)),
                unchecked((uint)Marshal.ReadInt32(buffer, 16)));
        }

        var (linkCount, mode, ownerUid) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => (
                unchecked((ulong)(uint)Marshal.ReadInt32(buffer, 20)),
                unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                unchecked((uint)Marshal.ReadInt32(buffer, 24))),
            Architecture.X64 => (
                unchecked((ulong)Marshal.ReadInt64(buffer, 16)),
                unchecked((uint)Marshal.ReadInt32(buffer, 24)),
                unchecked((uint)Marshal.ReadInt32(buffer, 28))),
            _ => throw new PlatformNotSupportedException(
                "worker runtime Linux file identity requires a probed x64 or arm64 ABI")
        };
        return new RuntimeFileIdentity(
            unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
            unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
            linkCount,
            mode,
            ownerUid);
    }

    private static RuntimeFileIdentity ReadRequiredUnixPathIdentity(string fullPath) =>
        TryReadUnixPathIdentity(fullPath) ??
        throw new FileNotFoundException(
            "worker runtime path disappeared during security validation",
            fullPath);

    private static RuntimeFileIdentity? TryReadUnixPathIdentity(string fullPath)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "worker runtime path identity verification supports only probed Unix hosts");

        var buffer = Marshal.AllocHGlobal(NativeStatBufferBytes);
        try
        {
            ZeroNativeBuffer(buffer);
            if (NativeLStat(fullPath, buffer) == 0)
                return ReadNativeIdentity(buffer);
            var error = Marshal.GetLastPInvokeError();
            if (error is UnixErrorNoEntry or UnixErrorNotDirectory)
                return null;
            throw NativeIdentityException(
                "lstat failed for a worker runtime path",
                error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void VerifyEffectiveUserOwns(
        RuntimeFileIdentity identity,
        string description)
    {
        var effectiveUserId = NativeGetEffectiveUserId();
        if (identity.OwnerUid != effectiveUserId)
            throw new UnauthorizedAccessException(
                $"{description} must be owned by the effective Unix user");
    }

    private static void ZeroNativeBuffer(IntPtr buffer)
    {
        for (var offset = 0; offset < NativeStatBufferBytes; offset += sizeof(long))
            Marshal.WriteInt64(buffer, offset, 0);
    }

    private static IOException NativeIdentityException(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return NativeIdentityException(message, error);
    }

    private static IOException NativeIdentityException(string message, int error) =>
        new(message, new Win32Exception(error));

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int NativeFStat(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int NativeLStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint NativeGetEffectiveUserId();

    private static void RejectReparsePoints(string path, bool includeLeaf)
    {
        var current = includeLeaf ? new DirectoryInfo(path) : Directory.GetParent(path);
        while (current is not null)
        {
            current.Refresh();
            if (!current.Exists)
                throw new DirectoryNotFoundException("worker runtime path component does not exist");
            if (current.LinkTarget is not null ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("worker runtime directory cannot traverse a symbolic link or reparse point");
            current = current.Parent;
        }
    }

    private static FileStreamOptions CreatePrivateStreamOptions(
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        int bufferSize)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = share,
            Options = options,
            BufferSize = bufferSize
        };
        if (!OperatingSystem.IsWindows() &&
            mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append)
            streamOptions.UnixCreateMode = PrivateFileMode;
        return streamOptions;
    }

    private static void RejectFileReparsePoint(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("worker runtime file disappeared during security validation", path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("worker runtime file cannot be a symbolic link or reparse point");
    }
}

internal readonly record struct RuntimeFileIdentity(
    ulong Device,
    ulong Inode,
    ulong LinkCount,
    uint Mode,
    uint OwnerUid)
{
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryFileType = 0x4000;

    public bool IsRegularFile => (Mode & FileTypeMask) == RegularFileType;

    public bool IsDirectory => (Mode & FileTypeMask) == DirectoryFileType;
}
