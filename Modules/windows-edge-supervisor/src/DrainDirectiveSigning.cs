namespace Dps.WindowsEdgeSupervisor;

/// <summary>
/// Least-privilege authority for the single Supervisor-owned drain-directive
/// signature domain. Implementations must keep private key material outside the
/// Supervisor process and reject every other statement domain.
/// </summary>
public interface IDrainDirectiveSigningBroker
{
    string KeyId { get; }

    ValueTask<string> SignDrainDirectiveStatementAsync(
        ReadOnlyMemory<byte> canonicalStatement,
        CancellationToken cancellationToken = default);
}
