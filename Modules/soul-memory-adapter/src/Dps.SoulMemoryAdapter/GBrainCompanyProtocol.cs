using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dps.GBrainProjector.Contracts;
using Dps.SoulMemoryAdapter.Contracts;

namespace Dps.SoulMemoryAdapter;

/// <summary>
/// Fixed protocol profile for the first GBrain Company HTTP/MCP adapter.
/// The deployment version is an operator pin, never a floating "latest" value.
/// </summary>
public static class GBrainCompanyProtocolV1
{
    public const string AdapterContractId = "dps.gbrain.company-mcp-adapter/v1";
    public const string TestedServerName = "gbrain";
    public const string TestedServerVersion = "0.42.42.0";
    public const string PreferredMcpProtocolVersion = "2025-11-25";
    public const string TestedMcpProtocolVersion = "2025-03-26";
    public static readonly IReadOnlySet<string> AllowedMcpProtocolVersions =
        new HashSet<string>(
            [PreferredMcpProtocolVersion, "2025-06-18", TestedMcpProtocolVersion],
            StringComparer.Ordinal);
    public const string ExpectedSchemaPack = "gbrain-base-v2";
    public const string SourceBindingSlug = "projection/source-binding";
    public const string ProjectionCurrentSlug = "projection/current";
    public const string PersonaCurrentSlug = "profile/persona/current";
    public const string SourceBindingContractId = "dps.gbrain.source-binding/v1";
    public const string ProjectionPageContractId = "dps.gbrain.projection-page/v1";
    public const string PersonaCurrentContractId = "dps.gbrain.persona-current-page/v1";
    public const string WireSchemaVersion = "1.0.0";
    public const string DpsProvenance = "dps-control-plane";
    public const string GBrainRemoteProvenance = "mcp:put_page";
}

public sealed record GBrainCompanyOptions
{
    private GBrainCompanyOptions(
        Uri issuerUri,
        Uri mcpEndpoint,
        string expectedServerVersion,
        string expectedSchemaPack,
        TimeSpan operationTimeout,
        int maximumRequestBytes,
        int maximumResponseBytes,
        int maximumConcurrency,
        TimeSpan maximumClockSkew,
        bool allowPlaintextLoopback)
    {
        IssuerUri = issuerUri;
        McpEndpoint = mcpEndpoint;
        ExpectedServerVersion = expectedServerVersion;
        ExpectedSchemaPack = expectedSchemaPack;
        OperationTimeout = operationTimeout;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumConcurrency = maximumConcurrency;
        MaximumClockSkew = maximumClockSkew;
        AllowPlaintextLoopback = allowPlaintextLoopback;
        Validate();
    }

    public Uri IssuerUri { get; }
    public Uri McpEndpoint { get; }
    public string ExpectedServerName => GBrainCompanyProtocolV1.TestedServerName;
    public string ExpectedServerVersion { get; }
    public string PreferredMcpProtocolVersion => GBrainCompanyProtocolV1.PreferredMcpProtocolVersion;
    public string ExpectedSchemaPack { get; }
    public Uri ExpectedResourceAudience => IssuerUri;
    public TimeSpan OperationTimeout { get; }
    public int MaximumRequestBytes { get; }
    public int MaximumResponseBytes { get; }
    public int MaximumConcurrency { get; }
    public TimeSpan MaximumClockSkew { get; }
    internal bool AllowPlaintextLoopback { get; }

    internal bool IsAllowedMcpProtocolVersion(string value) =>
        GBrainCompanyProtocolV1.AllowedMcpProtocolVersions.Contains(value);

    public static GBrainCompanyOptions CreateProduction(
        Uri issuerUri,
        Uri mcpEndpoint,
        string expectedServerVersion,
        string expectedSchemaPack = GBrainCompanyProtocolV1.ExpectedSchemaPack,
        TimeSpan? operationTimeout = null,
        int maximumRequestBytes = 524_288,
        int maximumResponseBytes = 1_048_576,
        int maximumConcurrency = 4,
        TimeSpan? maximumClockSkew = null)
    {
        return new GBrainCompanyOptions(
            issuerUri,
            mcpEndpoint,
            expectedServerVersion,
            expectedSchemaPack,
            operationTimeout ?? TimeSpan.FromSeconds(15),
            maximumRequestBytes,
            maximumResponseBytes,
            maximumConcurrency,
            maximumClockSkew ?? TimeSpan.FromMinutes(2),
            false);
    }

    internal static GBrainCompanyOptions CreateLoopbackSimulation(
        Uri issuerUri,
        Uri mcpEndpoint,
        TimeSpan? operationTimeout = null,
        int maximumResponseBytes = 1_048_576)
    {
        return new GBrainCompanyOptions(
            issuerUri,
            mcpEndpoint,
            GBrainCompanyProtocolV1.TestedServerVersion,
            GBrainCompanyProtocolV1.ExpectedSchemaPack,
            operationTimeout ?? TimeSpan.FromSeconds(3),
            524_288,
            maximumResponseBytes,
            4,
            TimeSpan.FromMinutes(2),
            true);
    }

    internal Uri OAuthDiscoveryEndpoint => new(
        IssuerUri.AbsoluteUri.TrimEnd('/') + "/.well-known/oauth-authorization-server",
        UriKind.Absolute);

    internal Uri OidcDiscoveryEndpoint => new(
        IssuerUri.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration",
        UriKind.Absolute);

    internal Uri OAuthProtectedResourceEndpoint => new(
        IssuerUri.GetLeftPart(UriPartial.Authority) +
        "/.well-known/oauth-protected-resource" + McpEndpoint.AbsolutePath,
        UriKind.Absolute);

    internal Uri OAuthProtectedResourceRootEndpoint => new(
        IssuerUri.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource",
        UriKind.Absolute);

    internal bool AllowPinnedChallengeMetadataFallback =>
        string.Equals(ExpectedServerVersion, TestedVersionForChallengeFallback, StringComparison.Ordinal);

    private const string TestedVersionForChallengeFallback = GBrainCompanyProtocolV1.TestedServerVersion;

    internal void ValidateDiscoveredTokenEndpoint(Uri endpoint)
    {
        ValidateEndpointSecurity(endpoint, nameof(endpoint));
        if (!SameAuthority(IssuerUri, endpoint))
        {
            throw new GBrainConfigurationException(
                "OAuth token endpoint authority must exactly match the configured issuer authority.");
        }
    }

    internal void ValidateMetadataEndpoint(Uri endpoint, string expectedPath)
    {
        ValidateEndpointSecurity(endpoint, nameof(endpoint));
        if (!SameAuthority(IssuerUri, endpoint) ||
            !string.Equals(endpoint.AbsolutePath, expectedPath, StringComparison.Ordinal))
            throw new GBrainConfigurationException("OAuth metadata endpoint is outside the exact trusted authority and path.");
    }

    private void Validate()
    {
        ValidateEndpointSecurity(IssuerUri, nameof(IssuerUri));
        ValidateEndpointSecurity(McpEndpoint, nameof(McpEndpoint));
        if (!string.Equals(IssuerUri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new GBrainConfigurationException("The OAuth issuer URL must not contain a path.");
        }

        if (!string.Equals(McpEndpoint.AbsolutePath, "/mcp", StringComparison.Ordinal))
        {
            throw new GBrainConfigurationException("The GBrain MCP endpoint path must be exactly /mcp.");
        }

        if (!SameAuthority(IssuerUri, McpEndpoint))
        {
            throw new GBrainConfigurationException(
                "OAuth issuer and MCP endpoint must use the same exact authority.");
        }

        RequireToken(ExpectedServerVersion, 64, nameof(ExpectedServerVersion));
        RequireToken(ExpectedSchemaPack, 64, nameof(ExpectedSchemaPack));
        if (OperationTimeout < TimeSpan.FromMilliseconds(100) ||
            OperationTimeout > TimeSpan.FromMinutes(2))
        {
            throw new GBrainConfigurationException(
                "GBrain operation timeout must be between 100 milliseconds and two minutes.");
        }

        if (MaximumRequestBytes is < 4_096 or > 1_048_576 ||
            MaximumResponseBytes is < 4_096 or > 4_194_304)
        {
            throw new GBrainConfigurationException("GBrain request or response limit is outside policy.");
        }

        if (MaximumConcurrency is < 1 or > 32)
        {
            throw new GBrainConfigurationException("GBrain maximum concurrency is outside policy.");
        }

        if (MaximumClockSkew < TimeSpan.Zero || MaximumClockSkew > TimeSpan.FromMinutes(5))
        {
            throw new GBrainConfigurationException("GBrain clock skew allowance is outside policy.");
        }
    }

    private void ValidateEndpointSecurity(Uri endpoint, string name)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new GBrainConfigurationException($"{name} must be an absolute URL without user info, query, or fragment.");
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return;
        }

        if (!AllowPlaintextLoopback ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !IsLoopbackHost(endpoint.Host))
        {
            throw new GBrainConfigurationException(
                "Production GBrain endpoints require HTTPS; plaintext is reserved for the internal loopback simulator.");
        }
    }

    internal static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    internal static void RequireToken(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(static character => character is < '!' or > '~'))
        {
            throw new GBrainConfigurationException($"{name} is not a bounded visible-ASCII token.");
        }
    }
}

public sealed record GBrainSoulScope(
    string SourceId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    long SourceBindingNonce)
{
    public void Validate()
    {
        SoulMemoryContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        SoulMemoryContractValidation.RequireDeviceBindingId(DeviceBindingId, nameof(DeviceBindingId));
        SoulMemoryContractValidation.RequirePlatformAccountId(PlatformAccountId, nameof(PlatformAccountId));
        // The scope carries its own binding witness: what was the single-candidate v1
        // derivation becomes, under v2, a nonce-witnessed candidate derivation whose
        // pairing proof is recomputed locally (Compute also enforces the fixed nonce
        // window and the canonical source_id format). To bind a Soul to a Source
        // identifier that is not its own, an attacker would need a nonce in
        // [0, MaximumNonce] whose domain-separated SHA-256 candidate collides with the
        // foreign identifier, which is computationally infeasible. This restores the
        // v1 property that the (soul, source) pair is locally provable; allocation
        // freshness (which nonce is durably allocated to the Soul) remains the durable
        // authority's concern via GBrainProjectionV2.Validate() and the provisioned
        // binding page.
        var expectedSourceId = GBrainSourceIdCandidates.Compute(SoulId, SourceBindingNonce);
        if (!string.Equals(SourceId, expectedSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "SourceId is not the Soul's nonce-witnessed source binding candidate.",
                nameof(SourceId));
        }
    }
}

public sealed record GBrainOAuthCredentialRequest(
    string SourceId,
    string SoulId,
    IReadOnlyList<string> RequiredScopes);

/// <summary>
/// Runtime secret source implemented by the composition root. It must use a real
/// secret manager; this module never reads files or environment variables itself.
/// </summary>
public interface IGBrainOAuthClientCredentialSource
{
    ValueTask<GBrainOAuthClientCredentialLease> AcquireAsync(
        GBrainOAuthCredentialRequest request,
        CancellationToken cancellationToken);
}

public static class GBrainProjectionMutationOperation
{
    public const string Upsert = "projection-upsert";
    public const string SoftDelete = "projection-soft-delete";

    internal static bool IsSupported(string value) =>
        string.Equals(value, Upsert, StringComparison.Ordinal) ||
        string.Equals(value, SoftDelete, StringComparison.Ordinal);
}

/// <summary>
/// Versioned delete request supplied by the Control Plane. A caller must bind a
/// delete to the exact projection revision/checksum it observed; a scope alone
/// is intentionally insufficient to authorize a mutation.
/// </summary>
public sealed record GBrainProjectionDeleteIntent(
    string SchemaVersion,
    string ContractId,
    string SourceId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string ExpectedProjectionRevision,
    string ExpectedProjectionChecksum,
    long SourceBindingNonce)
{
    // 1.1 (additive minor): the intent now carries the source-binding nonce witness so
    // its (soul, source, nonce) triple is locally provable; RequireMajor(…, 1) keeps
    // major-1 compatibility.
    public const string CurrentSchemaVersion = "1.1";
    public const string CurrentContractId = "gbrain.projection.delete-intent/v1";

    public void Validate()
    {
        SoulMemoryContractValidation.RequireMajor(SchemaVersion, 1);
        SoulMemoryContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        new GBrainSoulScope(SourceId, SoulId, DeviceBindingId, PlatformAccountId, SourceBindingNonce)
            .Validate();
        SoulMemoryContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        SoulMemoryContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        SoulMemoryContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        SoulMemoryContractValidation.RequireSha256(
            ExpectedProjectionRevision,
            nameof(ExpectedProjectionRevision));
        SoulMemoryContractValidation.RequireSha256(
            ExpectedProjectionChecksum,
            nameof(ExpectedProjectionChecksum));
    }

    internal GBrainProjectionMutationIntent ToMutationIntent()
    {
        Validate();
        return new GBrainProjectionMutationIntent(
            GBrainProjectionMutationIntent.CurrentSchemaVersion,
            GBrainProjectionMutationIntent.CurrentContractId,
            GBrainProjectionMutationOperation.SoftDelete,
            SourceId,
            SoulId,
            DeviceBindingId,
            PlatformAccountId,
            TraceId,
            IdempotencyKey,
            OccurredAt,
            ExpectedProjectionRevision,
            ExpectedProjectionChecksum,
            SourceBindingNonce);
    }
}

/// <summary>
/// Explicit operation envelope used by the durable mutation journal. The
/// projection timestamp describes projection content; this timestamp orders the
/// remote mutation itself, including delete/rebuild operations.
/// </summary>
public sealed record GBrainProjectionRebuildIntent(
    string SchemaVersion,
    string ContractId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string CurrentContractId = "gbrain.projection.rebuild-intent/v1";

    public void Validate()
    {
        SoulMemoryContractValidation.RequireMajor(SchemaVersion, 1);
        SoulMemoryContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        SoulMemoryContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        SoulMemoryContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        SoulMemoryContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
    }
}

/// <summary>
/// Immutable, versioned mutation tuple stored by the Control Plane before any
/// GBrain side effect. The journal owns idempotency, forward ordering, and the
/// durable unresolved/verified state; an in-memory implementation is test-only.
/// </summary>
public sealed record GBrainProjectionMutationIntent(
    string SchemaVersion,
    string ContractId,
    string OperationKind,
    string SourceId,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string ExpectedProjectionRevision,
    string ExpectedProjectionChecksum,
    long SourceBindingNonce)
{
    // 1.1 (additive minor): the journaled tuple now pins the source-binding nonce
    // witness alongside the Source identifier; RequireMajor(…, 1) keeps major-1
    // compatibility.
    public const string CurrentSchemaVersion = "1.1";
    public const string CurrentContractId = "gbrain.projection.mutation-intent/v1";

    public static GBrainProjectionMutationIntent FromProjection(GBrainProjectionV2 projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        return new GBrainProjectionMutationIntent(
            CurrentSchemaVersion,
            CurrentContractId,
            GBrainProjectionMutationOperation.Upsert,
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            projection.TraceId,
            projection.IdempotencyKey,
            projection.OccurredAt,
            projection.ProjectionRevision,
            projection.ProjectionChecksum,
            projection.SourceBindingNonce);
    }

    public static GBrainProjectionMutationIntent FromRebuild(
        GBrainProjectionV2 projection,
        GBrainProjectionRebuildIntent rebuild)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(rebuild);
        projection.Validate();
        rebuild.Validate();
        return new GBrainProjectionMutationIntent(
            CurrentSchemaVersion,
            CurrentContractId,
            GBrainProjectionMutationOperation.Upsert,
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            rebuild.TraceId,
            rebuild.IdempotencyKey,
            rebuild.OccurredAt,
            projection.ProjectionRevision,
            projection.ProjectionChecksum,
            projection.SourceBindingNonce);
    }

    public void Validate()
    {
        SoulMemoryContractValidation.RequireMajor(SchemaVersion, 1);
        SoulMemoryContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        if (!GBrainProjectionMutationOperation.IsSupported(OperationKind))
            throw new NotSupportedException($"Unsupported GBrain projection mutation '{OperationKind}'.");
        new GBrainSoulScope(SourceId, SoulId, DeviceBindingId, PlatformAccountId, SourceBindingNonce)
            .Validate();
        SoulMemoryContractValidation.RequireTraceId(TraceId, nameof(TraceId));
        SoulMemoryContractValidation.RequireIdempotencyKey(IdempotencyKey, nameof(IdempotencyKey));
        SoulMemoryContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        SoulMemoryContractValidation.RequireSha256(
            ExpectedProjectionRevision,
            nameof(ExpectedProjectionRevision));
        SoulMemoryContractValidation.RequireSha256(
            ExpectedProjectionChecksum,
            nameof(ExpectedProjectionChecksum));
    }
}

public sealed record GBrainProjectionMutationReservation(
    GBrainProjectionMutationIntent Intent,
    string ReservationId,
    string LeaseFenceToken,
    string Status,
    bool Created)
{
    public const string ReservedStatus = "reserved";
    public const string DispatchedStatus = "dispatched-unresolved";
    public const string VerifiedStatus = "verified";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Intent);
        Intent.Validate();
        GBrainCompanyOptions.RequireToken(ReservationId, 128, nameof(ReservationId));
        GBrainCompanyOptions.RequireToken(LeaseFenceToken, 128, nameof(LeaseFenceToken));
        if (Status is not ReservedStatus and not DispatchedStatus and not VerifiedStatus)
            throw new GBrainProtocolException("Mutation journal returned an unknown state.");
    }
}

/// <summary>
/// Durable per-Soul mutation journal. ReserveOrRead must atomically bind a global
/// per-Soul idempotency key, reject stale mutation time, reject a different
/// unresolved mutation, and attach the current monotonic lease fence. A
/// dispatched record remains quarantined until exact reconciliation or explicit
/// operator repair marks it verified.
/// </summary>
public interface IGBrainProjectionMutationJournal
{
    ValueTask<GBrainProjectionMutationReservation> ReserveOrReadAsync(
        GBrainProjectionMutationIntent requested,
        string leaseFenceToken,
        CancellationToken cancellationToken);

    ValueTask<GBrainProjectionMutationReservation> MarkDispatchedAsync(
        GBrainProjectionMutationReservation reservation,
        string leaseFenceToken,
        CancellationToken cancellationToken);

    ValueTask<GBrainProjectionMutationReservation> MarkVerifiedAsync(
        GBrainProjectionMutationReservation reservation,
        string leaseFenceToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Cross-process exclusive mutation lease for one native GBrain Source/Soul.
/// Production providers must be durable and fenced; local semaphores are tests only.
/// </summary>
public abstract class GBrainSoulMutationLease : IAsyncDisposable
{
    protected GBrainSoulMutationLease(
        GBrainSoulScope scope,
        string fenceToken,
        CancellationToken lostToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        GBrainCompanyOptions.RequireToken(fenceToken, 128, nameof(fenceToken));
        Scope = scope;
        FenceToken = fenceToken;
        LostToken = lostToken;
    }

    public GBrainSoulScope Scope { get; }
    public string FenceToken { get; }
    public CancellationToken LostToken { get; }

    internal void ValidateFor(GBrainSoulScope expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        if (!Equals(Scope, expected))
            throw new GBrainAuthorizationException(
                "GBrain mutation lease is not bound to the requested Soul scope.");
        ThrowIfLost();
    }

    internal void ThrowIfLost()
    {
        if (LostToken.IsCancellationRequested)
            throw new GBrainMutationLeaseLostException(
                "The durable GBrain mutation lease was lost or expired.");
    }

    public abstract ValueTask DisposeAsync();
}

public sealed class GBrainMutationLeaseLostException : InvalidOperationException
{
    public GBrainMutationLeaseLostException(string message) : base(message) { }
}

public sealed class GBrainMutationQuarantinedException : InvalidOperationException
{
    public GBrainMutationQuarantinedException(string message) : base(message) { }
}

public interface IGBrainSoulMutationLeaseProvider
{
    ValueTask<GBrainSoulMutationLease> AcquireAsync(
        GBrainSoulScope scope,
        CancellationToken cancellationToken);
}

public sealed class GBrainOAuthClientCredentialLease : IDisposable
{
    private byte[]? _clientSecret;

    public GBrainOAuthClientCredentialLease(
        string clientId,
        ReadOnlySpan<byte> clientSecretUtf8,
        string boundSourceId,
        string boundSoulId,
        IReadOnlyList<string> readableSourceIds,
        IReadOnlyList<string> grantedScopes)
    {
        GBrainCompanyOptions.RequireToken(clientId, 256, nameof(clientId));
        if (clientSecretUtf8.IsEmpty || clientSecretUtf8.Length > 8_192 ||
            !IsVisibleAscii(clientSecretUtf8))
        {
            throw new GBrainConfigurationException(
                "OAuth client secret must be a bounded visible-ASCII value.");
        }

        ClientId = clientId;
        ValidateSourceAndSoul(boundSourceId, boundSoulId);
        ArgumentNullException.ThrowIfNull(readableSourceIds);
        ArgumentNullException.ThrowIfNull(grantedScopes);
        BoundSourceId = boundSourceId;
        BoundSoulId = boundSoulId;
        ReadableSourceIds = readableSourceIds.ToArray();
        GrantedScopes = grantedScopes.ToArray();
        _clientSecret = clientSecretUtf8.ToArray();
    }

    public string ClientId { get; }
    public string BoundSourceId { get; }
    public string BoundSoulId { get; }
    public IReadOnlyList<string> ReadableSourceIds { get; }
    public IReadOnlyList<string> GrantedScopes { get; }
    internal ReadOnlySpan<byte> ClientSecretUtf8 =>
        _clientSecret ?? throw new ObjectDisposedException(nameof(GBrainOAuthClientCredentialLease));

    internal void ValidateFor(GBrainOAuthCredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSourceAndSoul(request.SourceId, request.SoulId);
        ArgumentNullException.ThrowIfNull(request.RequiredScopes);
        if (!string.Equals(BoundSourceId, request.SourceId, StringComparison.Ordinal) ||
            !string.Equals(BoundSoulId, request.SoulId, StringComparison.Ordinal) ||
            ReadableSourceIds.Count != 1 ||
            !string.Equals(ReadableSourceIds[0], request.SourceId, StringComparison.Ordinal))
        {
            throw new GBrainAuthorizationException(
                "OAuth credential is not exclusively bound to the requested Soul and Source.");
        }

        var grants = new HashSet<string>(GrantedScopes, StringComparer.Ordinal);
        if (GrantedScopes.Count != 2 || grants.Count != 2 ||
            !grants.SetEquals(["read", "write"]) ||
            request.RequiredScopes.Any(scope => !grants.Contains(scope)))
        {
            throw new GBrainAuthorizationException(
                "OAuth credential must have exactly read and write scopes and no administrative scope.");
        }
    }

    public void Dispose()
    {
        if (_clientSecret is null)
            return;
        CryptographicOperations.ZeroMemory(_clientSecret);
        _clientSecret = null;
    }

    private static bool IsVisibleAscii(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is < (byte)'!' or > (byte)'~')
                return false;
        }

        return true;
    }

    private static void ValidateSourceAndSoul(string sourceId, string soulId)
    {
        SoulMemoryContractValidation.RequireSoulId(soulId, nameof(soulId));
        // Format check only here: the credential pair does not carry the nonce, so the
        // pairing proof arrives by equality transduction. ValidateFor requires exact
        // SourceId/SoulId equality against a requested pair taken from a
        // GBrainSoulScope that has already proven its (soul, nonce) -> source_id
        // derivation in Validate(); byte equality with a proven pair carries that
        // proof onto the lease.
        SoulMemoryContractValidation.RequireSourceId(sourceId, nameof(sourceId));
    }
}

internal sealed class GBrainOAuthAccessTokenLease : IDisposable
{
    private byte[]? _token;

    internal GBrainOAuthAccessTokenLease(
        ReadOnlySpan<byte> tokenUtf8,
        string clientId,
        string sourceId,
        string soulId,
        DateTimeOffset expiresAt)
    {
        if (tokenUtf8.IsEmpty || tokenUtf8.Length > 8_192)
            throw new GBrainAuthorizationException("OAuth access token is outside policy.");
        foreach (var character in tokenUtf8)
        {
            if (character is < (byte)'!' or > (byte)'~')
                throw new GBrainAuthorizationException("OAuth access token is outside policy.");
        }
        _token = tokenUtf8.ToArray();
        ClientId = clientId;
        SourceId = sourceId;
        SoulId = soulId;
        ExpiresAt = expiresAt;
    }

    internal string ClientId { get; }
    internal string SourceId { get; }
    internal string SoulId { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal ReadOnlySpan<byte> TokenUtf8 =>
        _token ?? throw new ObjectDisposedException(nameof(GBrainOAuthAccessTokenLease));

    public void Dispose()
    {
        if (_token is null)
            return;
        CryptographicOperations.ZeroMemory(_token);
        _token = null;
    }
}

internal static class GBrainHttpClientFactory
{
    internal static HttpClient Create(GBrainCompanyOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = options.MaximumConcurrency,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            UseCookies = false,
            UseProxy = false
        };
        if (!options.AllowPlaintextLoopback)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            };
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

public class GBrainProtocolException : InvalidOperationException
{
    public GBrainProtocolException(string message) : base(message) { }
    public GBrainProtocolException(string message, Exception? innerException)
        : base(message, innerException) { }
}

public sealed class GBrainConfigurationException : GBrainProtocolException
{
    public GBrainConfigurationException(string message) : base(message) { }
}

public sealed class GBrainAuthorizationException : GBrainProtocolException
{
    public GBrainAuthorizationException(string message) : base(message) { }
    public GBrainAuthorizationException(string message, Exception? innerException)
        : base(message, innerException) { }
}

public sealed class GBrainCapabilityException : GBrainProtocolException
{
    public GBrainCapabilityException(string message) : base(message) { }
}

public sealed class GBrainToolCallException : GBrainProtocolException
{
    public GBrainToolCallException(string operation, string errorCode)
        : base($"GBrain operation {operation} failed with {errorCode}.")
    {
        Operation = operation;
        ErrorCode = errorCode;
    }

    public string Operation { get; }
    public string ErrorCode { get; }
}

public sealed class GBrainUnknownOutcomeException : GBrainProtocolException
{
    public GBrainUnknownOutcomeException(string message) : base(message) { }
    public GBrainUnknownOutcomeException(string message, Exception? innerException)
        : base(message, innerException) { }
}
