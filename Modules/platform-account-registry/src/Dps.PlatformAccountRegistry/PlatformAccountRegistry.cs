using Dps.PlatformAccountRegistry.Contracts;
using Dps.PlatformAuthorizationAuthority.Contracts;

namespace Dps.PlatformAccountRegistry;

public sealed record AuthorizePlatformAccountCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string Platform,
    string AliasDigest,
    string AliasKeyId,
    long AliasKeyEpoch,
    SignedPlatformAuthorizationEvidenceV1 AuthorizationEvidence,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record ChangePlatformAccountStatusCommand(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long ExpectedRevision,
    string Status,
    SignedPlatformAuthorizationEvidenceV1 AuthorizationEvidence,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

public interface IPlatformAccountRegistry
{
    PlatformAccountAuthorizedV1 Authorize(AuthorizePlatformAccountCommand command);
    PlatformAccountAuthorizedV1 ChangeStatus(ChangePlatformAccountStatusCommand command);
    PlatformAccountAuthorizedV1 Get(string platformAccountId, string soulId, string deviceBindingId);
    bool IsAuthorized(string platformAccountId, string soulId, string deviceBindingId);
}

public sealed partial class InMemoryPlatformAccountRegistry : IPlatformAccountRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PlatformAccountAuthorizedV1> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PayloadKey, PlatformAccountAuthorizedV1 Result)> _idempotency = new(StringComparer.Ordinal);
    private readonly PlatformAuthorizationEvidenceVerifier _evidenceVerifier;

    public InMemoryPlatformAccountRegistry(string activeReleaseBomSha256, long activeReleaseGeneration)
        : this(PlatformAuthorizationEvidenceVerifier.CreatePinned(activeReleaseBomSha256, activeReleaseGeneration))
    {
    }

    internal InMemoryPlatformAccountRegistry(PlatformAuthorizationEvidenceVerifier evidenceVerifier)
        => _evidenceVerifier = evidenceVerifier ?? throw new ArgumentNullException(nameof(evidenceVerifier));

    public PlatformAccountAuthorizedV1 Authorize(AuthorizePlatformAccountCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(command.PlatformAccountId);
        AccountContractValidation.RequireIdentifier(command.Platform, nameof(command.Platform));
        AccountContractValidation.RequireSha256(command.AliasDigest, nameof(command.AliasDigest));
        AccountContractValidation.RequireKeyId(command.AliasKeyId, nameof(command.AliasKeyId));
        if (command.AliasKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(command.AliasKeyEpoch));
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
        _evidenceVerifier.VerifyAuthorizeScope(command.AuthorizationEvidence, command);

        var platform = command.Platform.ToLowerInvariant();
        var alias = command.AliasDigest.ToLowerInvariant();
        var aliasKey = string.Join(':', platform, command.AliasKeyId, command.AliasKeyEpoch, alias);
        var payloadKey = string.Join(':', "authorize", command.SoulId, command.DeviceBindingId,
            command.PlatformAccountId, aliasKey,
            PlatformAuthorizationEvidenceVerifier.ComputeEvidenceSha256(command.AuthorizationEvidence));

        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                EnsureSamePayload(prior.PayloadKey, payloadKey);
                return prior.Result;
            }
            _evidenceVerifier.EnsureFresh(command.AuthorizationEvidence);
            if (_byAlias.ContainsKey(aliasKey)) throw new InvalidOperationException("The verified platform alias is already registered.");
            if (_byId.ContainsKey(command.PlatformAccountId)) throw new InvalidOperationException("The platform account identifier is already registered.");
            var value = Create(command.SoulId, command.DeviceBindingId, command.PlatformAccountId, platform, alias,
                command.AliasKeyId, command.AliasKeyEpoch, command.AuthorizationEvidence.AuthorizationEvidenceId,
                1, "authorized", command.TraceId, command.IdempotencyKey, command.OccurredAt);
            _byId.Add(command.PlatformAccountId, value);
            _byAlias.Add(aliasKey, command.PlatformAccountId);
            _idempotency.Add(command.IdempotencyKey, (payloadKey, value));
            return value;
        }
    }

    public PlatformAccountAuthorizedV1 ChangeStatus(ChangePlatformAccountStatusCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateScope(command.SoulId, command.DeviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(command.PlatformAccountId);
        if (command.Status is not ("authorized" or "revoked" or "suspended")) throw new ArgumentOutOfRangeException(nameof(command.Status));
        ValidateEnvelope(command.TraceId, command.IdempotencyKey, command.OccurredAt);
        _evidenceVerifier.VerifySignatureAndIssuer(command.AuthorizationEvidence);
        var payloadKey = string.Join(':', "status", command.SoulId, command.DeviceBindingId, command.PlatformAccountId,
            command.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), command.Status,
            PlatformAuthorizationEvidenceVerifier.ComputeEvidenceSha256(command.AuthorizationEvidence));

        lock (_gate)
        {
            if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
            {
                EnsureSamePayload(prior.PayloadKey, payloadKey);
                return prior.Result;
            }
            var current = GetUnderLock(command.PlatformAccountId, command.SoulId, command.DeviceBindingId);
            EnsureNoEffectiveBindingReservationUnderLock(current.PlatformAccountId);
            if (current.AuthorizationRevision != command.ExpectedRevision) throw new InvalidOperationException("Stale authorization revision.");
            _evidenceVerifier.VerifyStatusScope(command.AuthorizationEvidence, command, current);
            _evidenceVerifier.EnsureFresh(command.AuthorizationEvidence);
            var value = Create(current.SoulId, current.DeviceBindingId, current.PlatformAccountId, current.Platform,
                current.AliasDigest, current.AliasKeyId, current.AliasKeyEpoch,
                command.AuthorizationEvidence.AuthorizationEvidenceId, current.AuthorizationRevision + 1,
                command.Status, command.TraceId, command.IdempotencyKey, command.OccurredAt);
            _byId[current.PlatformAccountId] = value;
            _idempotency.Add(command.IdempotencyKey, (payloadKey, value));
            return value;
        }
    }

    public PlatformAccountAuthorizedV1 Get(string platformAccountId, string soulId, string deviceBindingId)
    {
        ValidateScope(soulId, deviceBindingId);
        AccountContractValidation.RequirePlatformAccountId(platformAccountId);
        lock (_gate) return GetUnderLock(platformAccountId, soulId, deviceBindingId);
    }

    public bool IsAuthorized(string platformAccountId, string soulId, string deviceBindingId) =>
        Get(platformAccountId, soulId, deviceBindingId).Status == "authorized";

    private PlatformAccountAuthorizedV1 GetUnderLock(string accountId, string soulId, string bindingId)
    {
        if (!_byId.TryGetValue(accountId, out var value)) throw new KeyNotFoundException("Unknown platform account.");
        if (value.SoulId != soulId || value.DeviceBindingId != bindingId)
            throw new UnauthorizedAccessException("Platform account scope mismatch.");
        return value;
    }

    private static PlatformAccountAuthorizedV1 Create(string soulId, string bindingId, string accountId, string platform,
        string alias, string keyId, long keyEpoch, string evidenceId, long revision, string status, string traceId,
        string idempotencyKey, DateTimeOffset occurredAt)
    {
        var value = new PlatformAccountAuthorizedV1(PlatformAccountAuthorizedV1.CurrentSchemaVersion,
            PlatformAccountAuthorizedV1.CurrentContractId, PlatformAccountAuthorizedV1.CurrentProducerModule,
            soulId, bindingId, accountId, traceId, idempotencyKey, occurredAt, "sensitive", platform, alias, keyId,
            evidenceId, revision, status, keyEpoch);
        value.Validate();
        return value;
    }

    private static void ValidateScope(string soulId, string bindingId)
    {
        AccountContractValidation.RequireSoulId(soulId);
        AccountContractValidation.RequireDeviceBindingId(bindingId);
    }

    private static void ValidateEnvelope(string traceId, string idempotencyKey, DateTimeOffset occurredAt)
    {
        AccountContractValidation.RequireTraceId(traceId);
        AccountContractValidation.RequireIdempotencyKey(idempotencyKey);
        AccountContractValidation.RequireUtc(occurredAt, nameof(occurredAt));
    }

    private static void EnsureSamePayload(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key is bound to a different authorization mutation.");
    }
}
