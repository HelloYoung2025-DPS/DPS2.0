using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Dps.EdgeLocalJournal;

public sealed partial class JournalStore
{
    private const string FileIdentityDomain = "dps.edge-local-journal.file-identity-sha256/v1";
    private const string AppendIntentDomain = "dps.edge-local-journal.append-intent-sha256/v1";

    private async Task<JournalWriterLease> AcquireWriterLeaseAsync(CancellationToken cancellationToken)
        => await AcquireExclusiveLeaseAsync(_path + ".writer.lock", cancellationToken).ConfigureAwait(false);

    private async Task<JournalWriterLease> AcquireAppendIntentGateAsync(CancellationToken cancellationToken)
        => await AcquireExclusiveLeaseAsync(_path + ".append-intent.lock", cancellationToken).ConfigureAwait(false);

    private static async Task<JournalWriterLease> AcquireExclusiveLeaseAsync(
        string leasePath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsurePathIsNotLinkOrDirectory(leasePath, allowMissing: true);
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                    BufferSize = 1
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                var stream = new FileStream(leasePath, options);
                try
                {
                    var identity = ReadOpenFileIdentity(stream.SafeFileHandle);
                    EnsurePathStillNamesOpenFile(leasePath, stream.SafeFileHandle, identity);
                    return new JournalWriterLease(leasePath, stream, identity);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<AppendIntentLease> CreateAppendIntentAsync(
        JournalAppendRequest request,
        CancellationToken cancellationToken)
    {
        await using var intentGate = await AcquireAppendIntentGateAsync(cancellationToken).ConfigureAwait(false);
        intentGate.AssertStillBound();
        var nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var intentPath = _path + ".append-intent." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + nonce + ".json";
        var requestDigest = JournalChecksumEncoding.ComputeSha256(
            AppendIntentDomain,
            request.ProducerModule,
            request.CommandId,
            request.EntryId,
            request.TraceId,
            request.IdempotencyKey);
        var serialized = "{\"schema_version\":\"1.0\",\"kind\":\"append-intent\",\"process_id\":" +
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
            ",\"nonce\":\"" + nonce + "\",\"request_sha256\":\"" + requestDigest + "\"}\n";
        var bytes = Utf8NoBom.GetBytes(serialized);
        FileStream? stream = null;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.WriteThrough,
                BufferSize = 4096
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            stream = new FileStream(intentPath, options);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            var identity = ReadOpenFileIdentity(stream.SafeFileHandle);
            EnsurePathStillNamesOpenFile(intentPath, stream.SafeFileHandle, identity);
            await stream.DisposeAsync().ConfigureAwait(false);
            stream = null;
            intentGate.AssertStillBound();
            return new AppendIntentLease(intentPath, identity);
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            TryDeleteOwnedPath(intentPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void EnsureNoAppendIntentArtifacts()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var pattern = Path.GetFileName(_path) + ".append-intent.*.json";
        if (Directory.EnumerateFiles(directory, pattern).Any())
        {
            throw new JournalAttestationStateChangedException(
                "A durable append intent exists; drain attestation issuance fails closed until append completion or reviewed stale-intent recovery.");
        }
    }

    private static void EnsurePathIsNotLinkOrDirectory(string path, bool allowMissing)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            if (allowMissing)
            {
                return;
            }
            throw new FileNotFoundException("Required Journal durable file is missing.", path);
        }

        var info = new FileInfo(path);
        info.Refresh();
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            info.LinkTarget is not null)
        {
            throw new JournalCorruptionException("Journal durable paths must be regular files and must not be links or reparse points.");
        }
    }

    private static NativeFileIdentity ReadOpenFileIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                throw new IOException("Cannot read the durable file identity.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            return new NativeFileIdentity(
                "windows",
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            var descriptor = checked((int)handle.DangerousGetHandle().ToInt64());
            if (FStat(descriptor, buffer) != 0)
            {
                throw new IOException("Cannot read the durable file descriptor identity.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            return ReadUnixIdentity(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static NativeFileIdentity ReadPathFileIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadOpenFileIdentity(stream.SafeFileHandle);
        }

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            if (Stat(path, buffer) != 0)
            {
                throw new IOException("Cannot read the durable path identity.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            return ReadUnixIdentity(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static NativeFileIdentity ReadUnixIdentity(nint buffer)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new NativeFileIdentity(
                "darwin",
                unchecked((uint)Marshal.ReadInt32(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
        }
        return new NativeFileIdentity(
            "unix",
            unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
            unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
    }

    private static void EnsurePathStillNamesOpenFile(
        string path,
        SafeFileHandle handle,
        NativeFileIdentity expected)
    {
        EnsurePathIsNotLinkOrDirectory(path, allowMissing: false);
        var openIdentity = ReadOpenFileIdentity(handle);
        // Windows FileShare.None (leases) or a handle that omits FileShare.Delete
        // prevents path rebinding while the handle is live. Opening the same path
        // again would itself violate that share contract, so the held-handle
        // identity is the authoritative check there. Unix rename is independent
        // of sharing flags and therefore requires a separate stat(path) check.
        var pathIdentity = OperatingSystem.IsWindows() ? openIdentity : ReadPathFileIdentity(path);
        if (openIdentity != expected || pathIdentity != expected)
        {
            throw new JournalAttestationStateChangedException(
                "A durable Journal path was replaced or rebound while its file descriptor was in use.");
        }
    }

    private static string HashFileIdentity(NativeFileIdentity identity) =>
        JournalChecksumEncoding.ComputeSha256(
            FileIdentityDomain,
            identity.Platform,
            identity.Device.ToString(CultureInfo.InvariantCulture),
            identity.FileId.ToString(CultureInfo.InvariantCulture));

    private static JournalReadSnapshot OpenStableJournalSnapshot(string path)
    {
        EnsurePathIsNotLinkOrDirectory(path, allowMissing: false);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        try
        {
            var identity = ReadOpenFileIdentity(stream.SafeFileHandle);
            EnsurePathStillNamesOpenFile(path, stream.SafeFileHandle, identity);
            return new JournalReadSnapshot(path, stream, identity);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void TryDeleteOwnedPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct NativeFileIdentity(string Platform, ulong Device, ulong FileId);

    private sealed class JournalWriterLease(
        string path,
        FileStream stream,
        NativeFileIdentity identity) : IAsyncDisposable
    {
        private bool _disposed;

        public void AssertStillBound()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsurePathStillNamesOpenFile(path, stream.SafeFileHandle, identity);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class AppendIntentLease(
        string path,
        NativeFileIdentity identity) : IAsyncDisposable
    {
        private bool _disposed;

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
            _disposed = true;
            if (!File.Exists(path))
            {
                throw new JournalAttestationStateChangedException("The active append-intent artifact disappeared unexpectedly.");
            }
            EnsurePathIsNotLinkOrDirectory(path, allowMissing: false);
            if (ReadPathFileIdentity(path) != identity)
            {
                throw new JournalAttestationStateChangedException("The active append-intent artifact was replaced unexpectedly.");
            }
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JournalReadSnapshot(
        string path,
        FileStream stream,
        NativeFileIdentity identity) : IAsyncDisposable
    {
        private bool _disposed;

        public async Task<FileHash> HashAsync(long maximumBytes, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsurePathStillNamesOpenFile(path, stream.SafeFileHandle, identity);
            var length = stream.Length;
            if (length < 0 || length > maximumBytes)
            {
                throw new JournalCorruptionException("Journal file exceeds the attestation byte limit.");
            }
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
                if (stream.Length != length || ReadOpenFileIdentity(stream.SafeFileHandle) != identity)
                {
                    throw new JournalAttestationStateChangedException(
                        "Journal length or file identity changed while hashing the held descriptor.");
                }
                EnsurePathStillNamesOpenFile(path, stream.SafeFileHandle, identity);
                return new FileHash(
                    length,
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    HashFileIdentity(identity));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, nint buffer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint buffer);
}
