using System.Security.Cryptography;
using System.Text;
using Dps.EdgeLocalJournal;

if (args.Length != 2)
{
    return 64;
}

var mode = args[0];
var path = Path.GetFullPath(args[1]);
if (mode is "append" or "rebind-gate-append")
{
    await using var store = await JournalStore.OpenAsync(path);
    Console.WriteLine("READY");
    if (await Console.In.ReadLineAsync() != "GO")
    {
        return 65;
    }
    if (mode == "rebind-gate-append")
    {
        var gate = path + ".append-intent.lock";
        var backup = gate + ".probe-original";
        try
        {
            File.Move(gate, backup);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                Options = FileOptions.WriteThrough,
                BufferSize = 1
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var replacement = new FileStream(gate, options))
            {
                replacement.Flush(flushToDisk: true);
            }
            Console.WriteLine("GATE_REBOUND");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            Console.WriteLine(File.Exists(gate) ? "GATE_REBIND_BLOCKED" : "GATE_REBIND_PARTIAL");
            return 0;
        }
    }
    const string payload = "{\"probe\":true}";
    var payloadSha256 = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    await store.AppendAsync(new JournalAppendRequest(
        "1.0",
        "edge.journal.append/v1",
        "windows-edge-supervisor",
        "probe-command",
        "probe-entry-" + Guid.NewGuid().ToString("N"),
        "COMMAND_STATE",
        "trace_" + new string('9', 32),
        "idem_" + new string('8', 64),
        "internal",
        "soul_" + new string('7', 64),
        "db_" + new string('6', 32),
        "pa_" + new string('5', 32),
        payload,
        payloadSha256,
        DateTimeOffset.Parse("2026-07-15T00:00:00.0000000+00:00")));
    Console.WriteLine("APPENDED");
    return 0;
}

Console.WriteLine("READY");
if (await Console.In.ReadLineAsync() != "GO")
{
    return 65;
}

if (mode == "replace")
{
    try
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var backup = path + ".probe-original";
        File.Move(path, backup);
        await File.WriteAllBytesAsync(path, bytes);
        Console.WriteLine("REPLACED");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
    {
        Console.WriteLine(File.Exists(path) ? "REBIND_BLOCKED" : "REBIND_PARTIAL");
    }
    return 0;
}
if (mode == "symlink")
{
    try
    {
        var backup = path + ".probe-original";
        File.Move(path, backup);
        File.CreateSymbolicLink(path, backup);
        Console.WriteLine("SYMLINKED");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
    {
        Console.WriteLine(File.Exists(path) ? "REBIND_BLOCKED" : "REBIND_PARTIAL");
    }
    return 0;
}

return 64;
