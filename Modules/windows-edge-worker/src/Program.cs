using System.Text.Json;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeWorker;

if (args.Length != 3 ||
    !string.Equals(args[0], "--production-reconcile", StringComparison.Ordinal) ||
    !string.Equals(args[1], "--state-dir", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "Usage: Dps.WindowsEdgeWorker --production-reconcile --state-dir <absolute-private-directory>");
    return 64;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    var result = await WorkerProductionHost.ReconcileAsync(args[2], shutdown.Token)
        .ConfigureAwait(false);
    Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    }));
    return result.ReconciliationComplete ? 0 : 78;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Worker startup reconciliation was cancelled; action intake remains disabled.");
    return 75;
}
catch (Exception exception) when (
    exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
    PlatformNotSupportedException or JournalConflictException or JournalQuarantinedException or
    JournalAttestationUnavailableException or JournalAttestationStateChangedException)
{
    Console.Error.WriteLine(
        "Worker startup reconciliation failed closed; action intake remains disabled: " +
        exception.GetType().Name + ": " + exception.Message);
    return 74;
}
