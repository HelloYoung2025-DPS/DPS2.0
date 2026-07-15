using Dps.CommandOrchestrator.Contracts;

namespace Dps.CommandOrchestrator;

public sealed record ExecutionAuthorizationActivationV1(
    string ReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomTokenSha256,
    bool ShadowMode);

public interface IPolicyExecutionAuthorizationSignerV1
{
    public const string CurrentProtocolId = "dps.policy-approval.execution-authorization-signer/v1";
    public const string CurrentSignerModule = "policy-approval";

    string ProtocolId { get; }
    string SignerModule { get; }
    string KeyId { get; }

    ValueTask<ExecutionAuthorizationV1> SignAsync(
        ExecutionAuthorizationV1 unsignedAuthorization,
        CancellationToken cancellationToken = default);
}
