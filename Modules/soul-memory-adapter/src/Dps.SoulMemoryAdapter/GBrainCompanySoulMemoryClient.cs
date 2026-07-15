using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dps.GBrainProjector.Contracts;
using Dps.SoulMemoryAdapter.Contracts;

namespace Dps.SoulMemoryAdapter;

public sealed record GBrainProjectionSearchResult(
    GBrainProjectionV1 Projection,
    DateTimeOffset PageUpdatedAt,
    DateTimeOffset VerifiedAt);

public sealed record GBrainPersonaCurrentDocument(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("payload_json")] string PayloadJson,
    [property: JsonPropertyName("payload_checksum")] string PayloadChecksum,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("provenance")] string Provenance)
{
    public void Validate()
    {
        SoulMemoryContractValidation.RequireMajor(SchemaVersion, 1);
        SoulMemoryContractValidation.RequireExact(
            ContractId,
            GBrainCompanyProtocolV1.PersonaCurrentContractId,
            nameof(ContractId));
        var scope = new GBrainSoulScope(SourceId, SoulId, DeviceBindingId, PlatformAccountId);
        scope.Validate();
        SoulMemoryContractValidation.RequireSha256(Revision, nameof(Revision));
        SoulMemoryContractValidation.RequireSha256(PayloadChecksum, nameof(PayloadChecksum));
        SoulMemoryContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        SoulMemoryContractValidation.RequireExact(
            Provenance,
            GBrainCompanyProtocolV1.DpsProvenance,
            nameof(Provenance));
        if (string.IsNullOrWhiteSpace(PayloadJson) || Encoding.UTF8.GetByteCount(PayloadJson) > 262_144)
            throw new ArgumentException("Persona payload JSON is outside policy.", nameof(PayloadJson));
        using var payload = JsonDocument.Parse(PayloadJson, new JsonDocumentOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        GBrainHttpJson.EnsureNoDuplicateProperties(payload.RootElement);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PayloadJson)));
        SoulMemoryContractValidation.RequireExact(PayloadChecksum, checksum, nameof(PayloadChecksum));
    }
}

public static class GBrainCompanyProvisioning
{
    /// <summary>
    /// Produces the only accepted immutable Source/Soul binding page. Provisioning
    /// is an operator action; this helper never performs a network call.
    /// </summary>
    public static string CreateSourceBindingMarkdown(GBrainSoulScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        return GBrainCompanyPageCodec.RenderSourceBindingMarkdown(scope);
    }
}

public sealed record GBrainProjectionDeleteOutcome(
    string Status,
    string SourceId,
    string SoulId,
    DateTimeOffset? DeletedAt,
    bool ExactSoftDeleteReadback,
    bool HardErasureConfirmed,
    bool ChunksAndEmbeddingsErased,
    bool CachesErased,
    bool BackupsErased)
{
    public const string SoftDeletedStatus = "soft-deleted-exact-readback";
    public const string AlreadySoftDeletedStatus = "already-soft-deleted-exact-readback";
    public const string NotFoundStatus = "not-found-no-mutation";
}

public sealed record GBrainProjectionRebuildOutcome(
    string Status,
    bool RestoredFromSoftDelete,
    bool ReplacedPriorProjection,
    SoulMemoryReadbackV1 VerifiedReadback)
{
    public const string AlreadyCurrentStatus = "already-current-exact-readback";
    public const string RebuiltStatus = "rebuilt-exact-readback";
}

/// <summary>
/// Fixed, version-pinned GBrain Company adapter. Public methods expose domain
/// operations only; URLs, MCP method names, and tool names cannot be supplied by
/// model output or callers.
/// </summary>
public sealed class GBrainCompanySoulMemoryClient : IDisposable
{
    private readonly GBrainCompanyOptions _options;
    private readonly IGBrainOAuthClientCredentialSource _credentialSource;
    private readonly IGBrainProjectionMutationJournal _mutationJournal;
    private readonly IGBrainSoulMutationLeaseProvider _mutationLeaseProvider;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _operationGate;

    public GBrainCompanySoulMemoryClient(
        GBrainCompanyOptions options,
        IGBrainOAuthClientCredentialSource credentialSource,
        IGBrainProjectionMutationJournal mutationJournal,
        IGBrainSoulMutationLeaseProvider mutationLeaseProvider)
        : this(
            options,
            credentialSource,
            mutationJournal,
            mutationLeaseProvider,
            GBrainHttpClientFactory.Create(
                options ?? throw new ArgumentNullException(nameof(options))),
            TimeProvider.System,
            ownsHttpClient: true)
    {
    }

    internal GBrainCompanySoulMemoryClient(
        GBrainCompanyOptions options,
        IGBrainOAuthClientCredentialSource credentialSource,
        IGBrainProjectionMutationJournal mutationJournal,
        IGBrainSoulMutationLeaseProvider mutationLeaseProvider,
        HttpClient httpClient,
        TimeProvider timeProvider,
        bool ownsHttpClient = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialSource = credentialSource ?? throw new ArgumentNullException(nameof(credentialSource));
        _mutationJournal = mutationJournal ?? throw new ArgumentNullException(nameof(mutationJournal));
        _mutationLeaseProvider = mutationLeaseProvider ?? throw new ArgumentNullException(nameof(mutationLeaseProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownsHttpClient = ownsHttpClient;
        _operationGate = new SemaphoreSlim(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    public async Task<SoulMemoryReadbackV1> WriteProjectionAsync(
        GBrainProjectionV1 projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var operationToken = operation.Token;
        var scope = ScopeFor(projection);
        var intent = GBrainProjectionMutationIntent.FromProjection(projection);
        await using var mutationLease = await AcquireMutationLeaseAsync(scope, operationToken)
            .ConfigureAwait(false);
        await using var session = await OpenAsync(scope, operationToken).ConfigureAwait(false);
        await VerifySourceBindingAsync(session, scope, operationToken).ConfigureAwait(false);

        var existing = await TryGetPageAsync(
            session,
            GBrainCompanyProtocolV1.ProjectionCurrentSlug,
            includeDeleted: true,
            operationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var current = GBrainCompanyPageCodec.ReadProjection(existing, scope, _options, _timeProvider);
            if (existing.DeletedAt is not null)
                throw new GBrainProtocolException(
                    "Projection is soft-deleted; use the explicit rebuild operation.");
            if (string.Equals(
                    current.ProjectionChecksum,
                    projection.ProjectionChecksum,
                    StringComparison.Ordinal) &&
                string.Equals(
                    current.ProjectionRevision,
                    projection.ProjectionRevision,
                    StringComparison.Ordinal))
            {
                var exactReservation = await ReserveMutationAsync(
                    intent,
                    mutationLease,
                    operationToken).ConfigureAwait(false);
                await MarkVerifiedAsync(exactReservation, mutationLease, operationToken)
                    .ConfigureAwait(false);
                return VerifyReceipt(projection);
            }
            EnsureForwardOnlyReplacement(current, projection);
        }

        var reservation = await ReserveMutationAsync(intent, mutationLease, operationToken)
            .ConfigureAwait(false);
        if (!reservation.Created)
            throw new GBrainMutationQuarantinedException(
                "A prior upsert is not exactly reflected by the current page; mutation remains quarantined.");
        return await PutAndVerifyAsync(
                session,
                scope,
                projection,
                reservation,
                mutationLease,
                alreadyDispatched: false,
                operationToken)
            .ConfigureAwait(false);
    }

    public async Task<SoulMemoryReadbackV1> ReadProjectionExactAsync(
        GBrainProjectionV1 expectedProjection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedProjection);
        expectedProjection.Validate();
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var operationToken = operation.Token;
        var scope = ScopeFor(expectedProjection);
        await using var session = await OpenAsync(scope, operationToken).ConfigureAwait(false);
        await VerifySourceBindingAsync(session, scope, operationToken).ConfigureAwait(false);
        var page = await GetPageAsync(
            session,
            GBrainCompanyProtocolV1.ProjectionCurrentSlug,
            includeDeleted: false,
            operationToken).ConfigureAwait(false);
        var readback = GBrainCompanyPageCodec.ReadProjection(page, scope, _options, _timeProvider);
        EnsureProjectionExact(expectedProjection, readback);
        return VerifyReceipt(expectedProjection);
    }

    public async Task<IReadOnlyList<GBrainProjectionSearchResult>> SearchProjectionAsync(
        GBrainSoulScope scope,
        string query,
        DateTimeOffset notOlderThan,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        ValidateSearchQuery(query);
        if (limit is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be between 1 and 20.");
        SoulMemoryContractValidation.RequireUtc(notOlderThan, nameof(notOlderThan));
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var operationToken = operation.Token;

        await using var session = await OpenAsync(scope, operationToken).ConfigureAwait(false);
        await VerifySourceBindingAsync(session, scope, operationToken).ConfigureAwait(false);
        using var search = await session.CallToolAsync(
            GBrainMcpTool.Search,
            new { query, limit, offset = 0 },
            mutating: false,
            operationToken).ConfigureAwait(false);
        if (search.RootElement.ValueKind != JsonValueKind.Array || search.RootElement.GetArrayLength() > limit)
            throw new GBrainProtocolException("GBrain search returned an invalid result count.");

        var verified = new List<GBrainProjectionSearchResult>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in search.RootElement.EnumerateArray())
        {
            var item = GBrainHttpJson.RequireObject(candidate, "GBrain search result");
            var slug = GBrainHttpJson.RequireString(item, "slug", 256);
            if (!string.Equals(slug, GBrainCompanyProtocolV1.ProjectionCurrentSlug, StringComparison.Ordinal) ||
                !slugs.Add(slug))
            {
                throw new GBrainProtocolException(
                    "Search returned an unregistered or duplicate page; no result is trusted.");
            }
            if (!item.TryGetProperty("source_id", out var candidateSource) ||
                candidateSource.ValueKind != JsonValueKind.String ||
                !string.Equals(candidateSource.GetString(), scope.SourceId, StringComparison.Ordinal))
                throw new GBrainAuthorizationException("Search result advertised a different Source.");

            var exactPage = await GetPageAsync(session, slug, includeDeleted: false, operationToken)
                .ConfigureAwait(false);
            var projection = GBrainCompanyPageCodec.ReadProjection(
                exactPage,
                scope,
                _options,
                _timeProvider,
                notOlderThan);
            verified.Add(new GBrainProjectionSearchResult(
                projection,
                exactPage.UpdatedAt,
                _timeProvider.GetUtcNow()));
        }
        return verified;
    }

    public async Task<GBrainPersonaCurrentDocument> ReadPersonaCurrentExactAsync(
        GBrainSoulScope scope,
        DateTimeOffset notOlderThan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        SoulMemoryContractValidation.RequireUtc(notOlderThan, nameof(notOlderThan));
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var operationToken = operation.Token;
        await using var session = await OpenAsync(scope, operationToken).ConfigureAwait(false);
        await VerifySourceBindingAsync(session, scope, operationToken).ConfigureAwait(false);
        var page = await GetPageAsync(
            session,
            GBrainCompanyProtocolV1.PersonaCurrentSlug,
            includeDeleted: false,
            operationToken).ConfigureAwait(false);
        return GBrainCompanyPageCodec.ReadPersona(
            page,
            scope,
            _options,
            _timeProvider,
            notOlderThan);
    }

    public async Task<GBrainProjectionDeleteOutcome> SoftDeleteProjectionAsync(
        GBrainProjectionDeleteIntent deleteIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deleteIntent);
        deleteIntent.Validate();
        var scope = new GBrainSoulScope(
            deleteIntent.SourceId,
            deleteIntent.SoulId,
            deleteIntent.DeviceBindingId,
            deleteIntent.PlatformAccountId);
        var mutationIntent = deleteIntent.ToMutationIntent();
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var operationToken = operation.Token;
        await using var mutationLease = await AcquireMutationLeaseAsync(scope, operationToken)
            .ConfigureAwait(false);
        await using var session = await OpenAsync(scope, operationToken).ConfigureAwait(false);
        await VerifySourceBindingAsync(session, scope, operationToken).ConfigureAwait(false);
        var existing = await TryGetPageAsync(
            session,
            GBrainCompanyProtocolV1.ProjectionCurrentSlug,
            includeDeleted: true,
            operationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var notFoundReservation = await ReserveMutationAsync(
                mutationIntent,
                mutationLease,
                operationToken).ConfigureAwait(false);
            if (!notFoundReservation.Created &&
                !string.Equals(
                    notFoundReservation.Status,
                    GBrainProjectionMutationReservation.VerifiedStatus,
                    StringComparison.Ordinal))
            {
                throw new GBrainMutationQuarantinedException(
                    "A prior delete is unresolved; an absent page cannot prove a delayed delete will not execute.");
            }
            await MarkVerifiedAsync(notFoundReservation, mutationLease, operationToken)
                .ConfigureAwait(false);
            return new GBrainProjectionDeleteOutcome(
                GBrainProjectionDeleteOutcome.NotFoundStatus,
                scope.SourceId,
                scope.SoulId,
                null,
                false,
                false,
                false,
                false,
                false);
        }

        var current = GBrainCompanyPageCodec.ReadProjection(
            existing,
            scope,
            _options,
            _timeProvider);
        EnsureDeleteTargetExact(deleteIntent, current);
        var reservation = await ReserveMutationAsync(
            mutationIntent,
            mutationLease,
            operationToken).ConfigureAwait(false);
        if (existing.DeletedAt is not null)
        {
            await MarkVerifiedAsync(reservation, mutationLease, operationToken)
                .ConfigureAwait(false);
            return CreateDeleteOutcome(
                GBrainProjectionDeleteOutcome.AlreadySoftDeletedStatus,
                scope,
                existing.DeletedAt);
        }
        if (!reservation.Created)
            throw new GBrainMutationQuarantinedException(
                "A prior delete is verified or unresolved but projection/current is active; replay is refused.");

        reservation = await MarkDispatchedAsync(reservation, mutationLease, operationToken)
            .ConfigureAwait(false);
        try
        {
            using var mutationCancellation = CreateMutationCancellation(
                operationToken,
                mutationLease);
            using var ignored = await session.CallToolAsync(
                GBrainMcpTool.DeletePage,
                new { slug = GBrainCompanyProtocolV1.ProjectionCurrentSlug },
                mutating: true,
                mutationCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            // Remain dispatched until the exact read-back below succeeds.
        }

        GBrainPage deleted;
        try
        {
            mutationLease.ThrowIfLost();
            deleted = await GetPageAsync(
                session,
                GBrainCompanyProtocolV1.ProjectionCurrentSlug,
                includeDeleted: true,
                operationToken).ConfigureAwait(false);
            var deletedProjection = GBrainCompanyPageCodec.ReadProjection(
                deleted,
                scope,
                _options,
                _timeProvider);
            EnsureDeleteTargetExact(deleteIntent, deletedProjection);
            if (deleted.DeletedAt is null)
                throw new GBrainReadbackMismatchException(
                    "Projection delete was not confirmed by exact include_deleted read-back.");
            await MarkVerifiedAsync(reservation, mutationLease, operationToken)
                .ConfigureAwait(false);
        }
        catch (GBrainUnknownOutcomeException)
        {
            throw;
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            throw new GBrainUnknownOutcomeException(
                "Projection delete could not be reconciled by exact include_deleted read-back; the Soul remains quarantined.",
                exception);
        }

        return CreateDeleteOutcome(
            GBrainProjectionDeleteOutcome.SoftDeletedStatus,
            scope,
            deleted.DeletedAt);
    }

    public async Task<GBrainProjectionRebuildOutcome> RebuildProjectionAsync(
        GBrainProjectionV1 projection,
        GBrainProjectionRebuildIntent rebuildIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(rebuildIntent);
        projection.Validate();
        rebuildIntent.Validate();
        if (rebuildIntent.OccurredAt <= projection.OccurredAt)
            throw new InvalidOperationException(
                "A rebuild operation must occur strictly after its target projection.");
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var operationToken = operation.Token;
        var scope = ScopeFor(projection);
        var intent = GBrainProjectionMutationIntent.FromRebuild(projection, rebuildIntent);
        await using var mutationLease = await AcquireMutationLeaseAsync(scope, operationToken)
            .ConfigureAwait(false);
        await using var session = await OpenAsync(scope, operationToken).ConfigureAwait(false);
        await VerifySourceBindingAsync(session, scope, operationToken).ConfigureAwait(false);

        var existing = await TryGetPageAsync(
            session,
            GBrainCompanyProtocolV1.ProjectionCurrentSlug,
            includeDeleted: true,
            operationToken).ConfigureAwait(false);
        var restored = false;
        var replaced = false;
        var exactCurrent = false;
        if (existing is not null)
        {
            var current = GBrainCompanyPageCodec.ReadProjection(existing, scope, _options, _timeProvider);
            replaced = !string.Equals(
                current.ProjectionChecksum,
                projection.ProjectionChecksum,
                StringComparison.Ordinal);
            exactCurrent = !replaced && string.Equals(
                current.ProjectionRevision,
                projection.ProjectionRevision,
                StringComparison.Ordinal);
            if (!exactCurrent)
                EnsureForwardOnlyReplacement(current, projection);
        }

        var reservation = await ReserveMutationAsync(intent, mutationLease, operationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.DeletedAt is null && exactCurrent)
        {
            await MarkVerifiedAsync(reservation, mutationLease, operationToken)
                .ConfigureAwait(false);
            return new GBrainProjectionRebuildOutcome(
                GBrainProjectionRebuildOutcome.AlreadyCurrentStatus,
                false,
                false,
                VerifyReceipt(projection));
        }
        if (!reservation.Created)
            throw new GBrainMutationQuarantinedException(
                "A prior rebuild is not exactly reflected by an active current page; mutation remains quarantined.");

        var alreadyDispatched = false;
        if (existing is not null && existing.DeletedAt is not null)
        {
            reservation = await MarkDispatchedAsync(reservation, mutationLease, operationToken)
                .ConfigureAwait(false);
            alreadyDispatched = true;
            try
            {
                using var mutationCancellation = CreateMutationCancellation(
                    operationToken,
                    mutationLease);
                using var ignored = await session.CallToolAsync(
                    GBrainMcpTool.RestorePage,
                    new { slug = GBrainCompanyProtocolV1.ProjectionCurrentSlug },
                    mutating: true,
                    mutationCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsReconciliationFailure(exception))
            {
                // The durable journal remains dispatched until exact read-back.
            }

            try
            {
                mutationLease.ThrowIfLost();
                var active = await GetPageAsync(
                    session,
                    GBrainCompanyProtocolV1.ProjectionCurrentSlug,
                    includeDeleted: true,
                    operationToken).ConfigureAwait(false);
                if (active.DeletedAt is not null)
                    throw new GBrainReadbackMismatchException(
                        "Projection restore was not confirmed by exact read-back.");
                var restoredProjection = GBrainCompanyPageCodec.ReadProjection(
                    active,
                    scope,
                    _options,
                    _timeProvider);
                restored = true;
                if (exactCurrent)
                {
                    EnsureProjectionExact(projection, restoredProjection);
                    await MarkVerifiedAsync(reservation, mutationLease, operationToken)
                        .ConfigureAwait(false);
                    return new GBrainProjectionRebuildOutcome(
                        GBrainProjectionRebuildOutcome.RebuiltStatus,
                        true,
                        false,
                        VerifyReceipt(projection));
                }
            }
            catch (GBrainUnknownOutcomeException)
            {
                throw;
            }
            catch (Exception exception) when (IsReconciliationFailure(exception))
            {
                throw new GBrainUnknownOutcomeException(
                    "Projection restore could not be reconciled by exact read-back; the Soul remains quarantined.",
                    exception);
            }
        }

        SoulMemoryReadbackV1 receipt;
        try
        {
            receipt = await PutAndVerifyAsync(
                    session,
                    scope,
                    projection,
                    reservation,
                    mutationLease,
                    alreadyDispatched,
                    operationToken)
                .ConfigureAwait(false);
        }
        catch (GBrainUnknownOutcomeException)
        {
            throw;
        }
        catch (Exception exception) when (restored && IsReconciliationFailure(exception))
        {
            throw new GBrainUnknownOutcomeException(
                "Projection was restored but the replacement write did not complete; exact reconciliation is required.",
                exception);
        }
        return new GBrainProjectionRebuildOutcome(
            GBrainProjectionRebuildOutcome.RebuiltStatus,
            restored,
            replaced,
            receipt);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private ValueTask<GBrainMcpSession> OpenAsync(
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        return GBrainMcpSession.OpenAsync(
            _options,
            _credentialSource,
            _httpClient,
            _timeProvider,
            scope,
            cancellationToken);
    }

    private async ValueTask<GBrainOperationContext> EnterOperationAsync(
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        try
        {
            await _operationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new GBrainOperationContext(_operationGate, timeout);
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    private async ValueTask<GBrainSoulMutationLease> AcquireMutationLeaseAsync(
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        var lease = await _mutationLeaseProvider.AcquireAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
            throw new GBrainAuthorizationException("GBrain mutation lease provider returned no lease.");
        try
        {
            lease.ValidateFor(scope);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<GBrainProjectionMutationReservation> ReserveMutationAsync(
        GBrainProjectionMutationIntent intent,
        GBrainSoulMutationLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();
        lease.ValidateFor(new GBrainSoulScope(
            intent.SourceId,
            intent.SoulId,
            intent.DeviceBindingId,
            intent.PlatformAccountId));
        var reservation = await _mutationJournal.ReserveOrReadAsync(
            intent,
            lease.FenceToken,
            cancellationToken).ConfigureAwait(false);
        EnsureReservation(reservation, intent, lease.FenceToken, expectedStatus: null);
        if (reservation.Created &&
            !string.Equals(
                reservation.Status,
                GBrainProjectionMutationReservation.ReservedStatus,
                StringComparison.Ordinal))
        {
            throw new GBrainProtocolException(
                "A newly-created mutation reservation was not returned as reserved.");
        }
        return reservation;
    }

    private async ValueTask<GBrainProjectionMutationReservation> MarkDispatchedAsync(
        GBrainProjectionMutationReservation reservation,
        GBrainSoulMutationLease lease,
        CancellationToken cancellationToken)
    {
        EnsureReservation(
            reservation,
            reservation.Intent,
            lease.FenceToken,
            GBrainProjectionMutationReservation.ReservedStatus);
        lease.ThrowIfLost();
        var dispatched = await _mutationJournal.MarkDispatchedAsync(
            reservation,
            lease.FenceToken,
            cancellationToken).ConfigureAwait(false);
        EnsureReservation(
            dispatched,
            reservation.Intent,
            lease.FenceToken,
            GBrainProjectionMutationReservation.DispatchedStatus,
            reservation.ReservationId);
        return dispatched;
    }

    private async ValueTask<GBrainProjectionMutationReservation> MarkVerifiedAsync(
        GBrainProjectionMutationReservation reservation,
        GBrainSoulMutationLease lease,
        CancellationToken cancellationToken)
    {
        EnsureReservation(reservation, reservation.Intent, lease.FenceToken, expectedStatus: null);
        lease.ThrowIfLost();
        if (string.Equals(
                reservation.Status,
                GBrainProjectionMutationReservation.VerifiedStatus,
                StringComparison.Ordinal))
            return reservation;
        var verified = await _mutationJournal.MarkVerifiedAsync(
            reservation,
            lease.FenceToken,
            cancellationToken).ConfigureAwait(false);
        EnsureReservation(
            verified,
            reservation.Intent,
            lease.FenceToken,
            GBrainProjectionMutationReservation.VerifiedStatus,
            reservation.ReservationId);
        lease.ThrowIfLost();
        return verified;
    }

    private static void EnsureReservation(
        GBrainProjectionMutationReservation? reservation,
        GBrainProjectionMutationIntent expectedIntent,
        string expectedFenceToken,
        string? expectedStatus,
        string? expectedReservationId = null)
    {
        if (reservation is null)
            throw new GBrainProtocolException("GBrain mutation journal returned no reservation.");
        reservation.Validate();
        if (!Equals(reservation.Intent, expectedIntent) ||
            !string.Equals(
                reservation.LeaseFenceToken,
                expectedFenceToken,
                StringComparison.Ordinal) ||
            expectedReservationId is not null &&
            !string.Equals(
                reservation.ReservationId,
                expectedReservationId,
                StringComparison.Ordinal) ||
            expectedStatus is not null &&
            !string.Equals(reservation.Status, expectedStatus, StringComparison.Ordinal))
        {
            throw new GBrainProtocolException(
                "GBrain mutation journal returned a mismatched intent, fence, reservation, or state.");
        }
    }

    private async Task VerifySourceBindingAsync(
        GBrainMcpSession session,
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        var binding = await GetPageAsync(
            session,
            GBrainCompanyProtocolV1.SourceBindingSlug,
            includeDeleted: false,
            cancellationToken).ConfigureAwait(false);
        GBrainCompanyPageCodec.VerifySourceBinding(binding, scope, _options, _timeProvider);
    }

    private async Task<SoulMemoryReadbackV1> PutAndVerifyAsync(
        GBrainMcpSession session,
        GBrainSoulScope scope,
        GBrainProjectionV1 projection,
        GBrainProjectionMutationReservation reservation,
        GBrainSoulMutationLease lease,
        bool alreadyDispatched,
        CancellationToken cancellationToken)
    {
        if (!alreadyDispatched)
            reservation = await MarkDispatchedAsync(reservation, lease, cancellationToken)
                .ConfigureAwait(false);
        else
            EnsureReservation(
                reservation,
                reservation.Intent,
                lease.FenceToken,
                GBrainProjectionMutationReservation.DispatchedStatus);
        var content = GBrainCompanyPageCodec.RenderProjectionMarkdown(projection);
        try
        {
            using var mutationCancellation = CreateMutationCancellation(cancellationToken, lease);
            using var ignored = await session.CallToolAsync(
                GBrainMcpTool.PutPage,
                new { slug = GBrainCompanyProtocolV1.ProjectionCurrentSlug, content },
                mutating: true,
                mutationCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            // The record remains dispatched. Exact read-back below is the only
            // automatic transition out of quarantine.
        }

        try
        {
            lease.ThrowIfLost();
            var page = await GetPageAsync(
                session,
                GBrainCompanyProtocolV1.ProjectionCurrentSlug,
                includeDeleted: false,
                cancellationToken).ConfigureAwait(false);
            var readback = GBrainCompanyPageCodec.ReadProjection(page, scope, _options, _timeProvider);
            EnsureProjectionExact(projection, readback);
            await MarkVerifiedAsync(reservation, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (GBrainUnknownOutcomeException)
        {
            throw;
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            throw new GBrainUnknownOutcomeException(
                "Projection write could not be reconciled by exact read-back; the Soul remains quarantined.",
                exception);
        }
        return VerifyReceipt(projection);
    }

    private SoulMemoryReadbackV1 VerifyReceipt(GBrainProjectionV1 projection)
    {
        var adapter = new DeterministicSoulMemoryAdapter();
        var prepared = adapter.Prepare(projection);
        var observation = new GBrainReadbackObservation(
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId,
            projection.SchemaVersion,
            projection.ContractId,
            projection.ProjectionRevision,
            projection.ProjectionChecksum);
        return adapter.Verify(new VerifyReadbackCommand(
            prepared,
            observation,
            projection.TraceId,
            projection.IdempotencyKey,
            _timeProvider.GetUtcNow()));
    }

    private static async Task<GBrainPage> GetPageAsync(
        GBrainMcpSession session,
        string slug,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        using var result = await session.CallToolAsync(
            GBrainMcpTool.GetPage,
            new { slug, fuzzy = false, include_deleted = includeDeleted },
            mutating: false,
            cancellationToken).ConfigureAwait(false);
        var page = GBrainPage.Parse(result.RootElement);
        if (!includeDeleted && page.DeletedAt is not null)
            throw new GBrainReadbackMismatchException(
                "GBrain returned a soft-deleted page to an active-only exact read.");
        return page;
    }

    private static async Task<GBrainPage?> TryGetPageAsync(
        GBrainMcpSession session,
        string slug,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetPageAsync(session, slug, includeDeleted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GBrainToolCallException exception) when (exception.ErrorCode == "page_not_found")
        {
            return null;
        }
    }

    private static GBrainSoulScope ScopeFor(GBrainProjectionV1 projection) =>
        new(
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId);

    private static CancellationTokenSource CreateMutationCancellation(
        CancellationToken operationToken,
        GBrainSoulMutationLease lease)
    {
        lease.ThrowIfLost();
        return CancellationTokenSource.CreateLinkedTokenSource(operationToken, lease.LostToken);
    }

    private static void EnsureDeleteTargetExact(
        GBrainProjectionDeleteIntent deleteIntent,
        GBrainProjectionV1 current)
    {
        if (!string.Equals(
                deleteIntent.ExpectedProjectionRevision,
                current.ProjectionRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                deleteIntent.ExpectedProjectionChecksum,
                current.ProjectionChecksum,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Delete intent does not target the exact current projection revision and checksum.");
        }
        if (deleteIntent.OccurredAt <= current.OccurredAt)
            throw new InvalidOperationException(
                "Delete intent is stale or ambiguously ordered relative to its target projection.");
    }

    private static GBrainProjectionDeleteOutcome CreateDeleteOutcome(
        string status,
        GBrainSoulScope scope,
        DateTimeOffset? deletedAt) =>
        new(
            status,
            scope.SourceId,
            scope.SoulId,
            deletedAt,
            true,
            false,
            false,
            false,
            false);

    private static void EnsureProjectionExact(
        GBrainProjectionV1 expected,
        GBrainProjectionV1 actual)
    {
        if (!string.Equals(
                GBrainProjectionCanonicalizer.Serialize(expected),
                GBrainProjectionCanonicalizer.Serialize(actual),
                StringComparison.Ordinal))
        {
            throw new GBrainReadbackMismatchException(
                "GBrain projection read-back does not exactly match the requested projection.");
        }
    }

    private static void EnsureForwardOnlyReplacement(
        GBrainProjectionV1 current,
        GBrainProjectionV1 candidate)
    {
        if (string.Equals(current.IdempotencyKey, candidate.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "GBrain projection idempotency key cannot be rebound to different content.");
        if (string.Equals(current.ProjectionRevision, candidate.ProjectionRevision, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "GBrain projection revision cannot be rebound to a different checksum.");
        if (candidate.OccurredAt <= current.OccurredAt)
            throw new InvalidOperationException(
                "A stale or ambiguously ordered GBrain projection cannot replace the current revision.");
    }

    private static bool IsReconciliationFailure(Exception exception) =>
        exception is GBrainProtocolException or
            GBrainReadbackMismatchException or
            OperationCanceledException or
            IOException or
            JsonException or
            ArgumentException or
            InvalidOperationException;

    private static void ValidateSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || Encoding.UTF8.GetByteCount(query) > 1_024 ||
            query.Any(static character => char.IsControl(character)))
            throw new ArgumentException("Search query is outside policy.", nameof(query));
    }

    private sealed class GBrainOperationContext : IDisposable
    {
        private SemaphoreSlim? _gate;
        private CancellationTokenSource? _timeout;

        internal GBrainOperationContext(
            SemaphoreSlim gate,
            CancellationTokenSource timeout)
        {
            _gate = gate;
            _timeout = timeout;
        }

        internal CancellationToken Token =>
            _timeout?.Token ?? throw new ObjectDisposedException(nameof(GBrainOperationContext));

        public void Dispose()
        {
            var timeout = Interlocked.Exchange(ref _timeout, null);
            var gate = Interlocked.Exchange(ref _gate, null);
            timeout?.Dispose();
            gate?.Release();
        }
    }
}

internal sealed record GBrainPage(
    string Slug,
    string SourceId,
    string CompiledTruth,
    JsonElement Frontmatter,
    string SourceKind,
    string IngestedVia,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    internal static GBrainPage Parse(JsonElement value)
    {
        var root = GBrainHttpJson.RequireObject(value, "GBrain page");
        var slug = GBrainHttpJson.RequireString(root, "slug", 256);
        var sourceId = GBrainHttpJson.RequireString(root, "source_id", 64);
        var compiledTruth = GBrainHttpJson.RequireString(root, "compiled_truth", 524_288);
        var frontmatter = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(root, "frontmatter"),
            "GBrain page frontmatter").Clone();
        var sourceKind = GBrainHttpJson.RequireString(root, "source_kind", 64);
        var ingestedVia = GBrainHttpJson.RequireString(root, "ingested_via", 64);
        var updatedAt = ParseUtc(root, "updated_at", required: true)!.Value;
        var deletedAt = ParseUtc(root, "deleted_at", required: false);
        return new GBrainPage(
            slug,
            sourceId,
            compiledTruth,
            frontmatter,
            sourceKind,
            ingestedVia,
            updatedAt,
            deletedAt);
    }

    private static DateTimeOffset? ParseUtc(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
                throw new GBrainProtocolException($"GBrain page {name} is missing.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new GBrainProtocolException($"GBrain page {name} is not UTC.");
        return parsed;
    }
}

internal static class GBrainCompanyPageCodec
{
    private const string BindingProvenance = "dps-authorized-source-provisioning";

    internal static string RenderSourceBindingMarkdown(GBrainSoulScope scope)
    {
        var body = RenderSourceBindingBody(scope);
        var checksum = ComputeBindingChecksum(scope);
        return Frontmatter(
            "DPS Source Binding",
            GBrainCompanyProtocolV1.SourceBindingContractId,
            scope,
            checksum,
            BindingProvenance) + body;
    }

    internal static string RenderProjectionMarkdown(GBrainProjectionV1 projection)
    {
        projection.Validate();
        var scope = new GBrainSoulScope(
            projection.SourceId,
            projection.SoulId,
            projection.DeviceBindingId,
            projection.PlatformAccountId);
        return Frontmatter(
            "DPS Soul Projection",
            GBrainCompanyProtocolV1.ProjectionPageContractId,
            scope,
            projection.ProjectionChecksum,
            GBrainCompanyProtocolV1.DpsProvenance,
            projection.ProjectionRevision) +
            GBrainProjectionCanonicalizer.Serialize(projection);
    }

    internal static string RenderPersonaMarkdown(GBrainPersonaCurrentDocument persona)
    {
        persona.Validate();
        var scope = new GBrainSoulScope(
            persona.SourceId,
            persona.SoulId,
            persona.DeviceBindingId,
            persona.PlatformAccountId);
        return Frontmatter(
            "DPS Persona Current",
            GBrainCompanyProtocolV1.PersonaCurrentContractId,
            scope,
            persona.PayloadChecksum,
            GBrainCompanyProtocolV1.DpsProvenance,
            persona.Revision) +
            RenderPersonaBody(persona);
    }

    internal static void VerifySourceBinding(
        GBrainPage page,
        GBrainSoulScope scope,
        GBrainCompanyOptions options,
        TimeProvider timeProvider)
    {
        RequirePageBase(page, GBrainCompanyProtocolV1.SourceBindingSlug, scope, options, timeProvider);
        RequireFrontmatter(
            page,
            GBrainCompanyProtocolV1.SourceBindingContractId,
            scope,
            ComputeBindingChecksum(scope),
            BindingProvenance,
            null);
        var expected = RenderSourceBindingBody(scope);
        if (!string.Equals(page.CompiledTruth, expected, StringComparison.Ordinal))
            throw new GBrainAuthorizationException("GBrain Source binding page is not the exact provisioned binding.");
    }

    internal static GBrainProjectionV1 ReadProjection(
        GBrainPage page,
        GBrainSoulScope scope,
        GBrainCompanyOptions options,
        TimeProvider timeProvider,
        DateTimeOffset? notOlderThan = null)
    {
        RequirePageBase(
            page,
            GBrainCompanyProtocolV1.ProjectionCurrentSlug,
            scope,
            options,
            timeProvider,
            notOlderThan);
        GBrainProjectionV1 projection;
        try
        {
            projection = JsonSerializer.Deserialize<GBrainProjectionV1>(page.CompiledTruth)
                ?? throw new JsonException("Projection was null.");
            projection.Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new GBrainReadbackMismatchException("GBrain projection page body is invalid.", exception);
        }
        if (!string.Equals(
                GBrainProjectionCanonicalizer.Serialize(projection),
                page.CompiledTruth,
                StringComparison.Ordinal))
            throw new GBrainReadbackMismatchException("GBrain projection page is not canonical JSON.");
        EnsureScope(scope, projection.SourceId, projection.SoulId, projection.DeviceBindingId, projection.PlatformAccountId);
        RequireFrontmatter(
            page,
            GBrainCompanyProtocolV1.ProjectionPageContractId,
            scope,
            projection.ProjectionChecksum,
            GBrainCompanyProtocolV1.DpsProvenance,
            projection.ProjectionRevision);
        return projection;
    }

    internal static GBrainPersonaCurrentDocument ReadPersona(
        GBrainPage page,
        GBrainSoulScope scope,
        GBrainCompanyOptions options,
        TimeProvider timeProvider,
        DateTimeOffset notOlderThan)
    {
        RequirePageBase(
            page,
            GBrainCompanyProtocolV1.PersonaCurrentSlug,
            scope,
            options,
            timeProvider,
            notOlderThan);
        GBrainPersonaCurrentDocument persona;
        try
        {
            persona = JsonSerializer.Deserialize<GBrainPersonaCurrentDocument>(page.CompiledTruth)
                ?? throw new JsonException("Persona was null.");
            persona.Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new GBrainReadbackMismatchException("GBrain Persona current page is invalid.", exception);
        }
        if (!string.Equals(RenderPersonaBody(persona), page.CompiledTruth, StringComparison.Ordinal))
            throw new GBrainReadbackMismatchException("GBrain Persona current page is not canonical JSON.");
        EnsureScope(scope, persona.SourceId, persona.SoulId, persona.DeviceBindingId, persona.PlatformAccountId);
        RequireFrontmatter(
            page,
            GBrainCompanyProtocolV1.PersonaCurrentContractId,
            scope,
            persona.PayloadChecksum,
            GBrainCompanyProtocolV1.DpsProvenance,
            persona.Revision);
        return persona;
    }

    private static string RenderSourceBindingBody(GBrainSoulScope scope)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", GBrainCompanyProtocolV1.WireSchemaVersion);
            writer.WriteString("contract_id", GBrainCompanyProtocolV1.SourceBindingContractId);
            writer.WriteString("source_id", scope.SourceId);
            writer.WriteString("soul_id", scope.SoulId);
            writer.WriteString("device_binding_id", scope.DeviceBindingId);
            writer.WriteString("platform_account_id", scope.PlatformAccountId);
            writer.WriteString("schema_pack", GBrainCompanyProtocolV1.ExpectedSchemaPack);
            writer.WriteString("provenance", BindingProvenance);
            writer.WriteString("binding_checksum", ComputeBindingChecksum(scope));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string RenderPersonaBody(GBrainPersonaCurrentDocument persona)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", persona.SchemaVersion);
            writer.WriteString("contract_id", persona.ContractId);
            writer.WriteString("source_id", persona.SourceId);
            writer.WriteString("soul_id", persona.SoulId);
            writer.WriteString("device_binding_id", persona.DeviceBindingId);
            writer.WriteString("platform_account_id", persona.PlatformAccountId);
            writer.WriteString("revision", persona.Revision);
            writer.WriteString("payload_json", persona.PayloadJson);
            writer.WriteString("payload_checksum", persona.PayloadChecksum);
            writer.WriteString("occurred_at", persona.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("provenance", persona.Provenance);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ComputeBindingChecksum(GBrainSoulScope scope)
    {
        var canonical = string.Join('\n',
            GBrainCompanyProtocolV1.WireSchemaVersion,
            GBrainCompanyProtocolV1.SourceBindingContractId,
            scope.SourceId,
            scope.SoulId,
            scope.DeviceBindingId,
            scope.PlatformAccountId,
            GBrainCompanyProtocolV1.ExpectedSchemaPack,
            BindingProvenance);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Frontmatter(
        string title,
        string contractId,
        GBrainSoulScope scope,
        string checksum,
        string provenance,
        string? revision = null)
    {
        var builder = new StringBuilder(512);
        builder.AppendLine("---");
        builder.AppendLine("type: note");
        builder.Append("title: ").AppendLine(title);
        builder.Append("dps_schema_version: ").AppendLine(GBrainCompanyProtocolV1.WireSchemaVersion);
        builder.Append("dps_schema_pack: ").AppendLine(GBrainCompanyProtocolV1.ExpectedSchemaPack);
        builder.Append("dps_contract_id: ").AppendLine(contractId);
        builder.Append("dps_source_id: ").AppendLine(scope.SourceId);
        builder.Append("dps_soul_id: ").AppendLine(scope.SoulId);
        builder.Append("dps_device_binding_id: ").AppendLine(scope.DeviceBindingId);
        builder.Append("dps_platform_account_id: ").AppendLine(scope.PlatformAccountId);
        builder.Append("dps_checksum: ").AppendLine(checksum);
        if (revision is not null)
            builder.Append("dps_revision: ").AppendLine(revision);
        builder.Append("dps_provenance: ").AppendLine(provenance);
        builder.AppendLine("---");
        return builder.ToString();
    }

    private static void RequirePageBase(
        GBrainPage page,
        string slug,
        GBrainSoulScope scope,
        GBrainCompanyOptions options,
        TimeProvider timeProvider,
        DateTimeOffset? notOlderThan = null)
    {
        if (!string.Equals(page.Slug, slug, StringComparison.Ordinal) ||
            !string.Equals(page.SourceId, scope.SourceId, StringComparison.Ordinal))
            throw new GBrainAuthorizationException("GBrain page slug or native Source is outside the requested Soul scope.");
        if (!string.Equals(page.SourceKind, GBrainCompanyProtocolV1.GBrainRemoteProvenance, StringComparison.Ordinal) ||
            !string.Equals(page.IngestedVia, GBrainCompanyProtocolV1.GBrainRemoteProvenance, StringComparison.Ordinal))
            throw new GBrainReadbackMismatchException("GBrain page provenance is not the pinned remote put_page provenance.");

        var now = timeProvider.GetUtcNow();
        if (page.UpdatedAt > now.Add(options.MaximumClockSkew))
            throw new GBrainReadbackMismatchException("GBrain page updated_at is in the future.");
        if (page.DeletedAt is not null &&
            (page.DeletedAt.Value > now.Add(options.MaximumClockSkew) ||
             page.DeletedAt.Value < page.UpdatedAt))
            throw new GBrainReadbackMismatchException(
                "GBrain page deleted_at is outside its temporal integrity bounds.");
        if (notOlderThan.HasValue && page.UpdatedAt < notOlderThan.Value.Subtract(options.MaximumClockSkew))
            throw new GBrainReadbackMismatchException("GBrain page is older than the caller's freshness floor.");
    }

    private static void RequireFrontmatter(
        GBrainPage page,
        string contractId,
        GBrainSoulScope scope,
        string checksum,
        string provenance,
        string? revision)
    {
        RequireFrontmatterValue(page.Frontmatter, "type", "note");
        RequireFrontmatterValue(page.Frontmatter, "dps_schema_version", GBrainCompanyProtocolV1.WireSchemaVersion);
        RequireFrontmatterValue(page.Frontmatter, "dps_schema_pack", GBrainCompanyProtocolV1.ExpectedSchemaPack);
        RequireFrontmatterValue(page.Frontmatter, "dps_contract_id", contractId);
        RequireFrontmatterValue(page.Frontmatter, "dps_source_id", scope.SourceId);
        RequireFrontmatterValue(page.Frontmatter, "dps_soul_id", scope.SoulId);
        RequireFrontmatterValue(page.Frontmatter, "dps_device_binding_id", scope.DeviceBindingId);
        RequireFrontmatterValue(page.Frontmatter, "dps_platform_account_id", scope.PlatformAccountId);
        RequireFrontmatterValue(page.Frontmatter, "dps_checksum", checksum);
        RequireFrontmatterValue(page.Frontmatter, "dps_provenance", provenance);
        if (revision is not null)
            RequireFrontmatterValue(page.Frontmatter, "dps_revision", revision);
    }

    private static void RequireFrontmatterValue(JsonElement frontmatter, string name, string expected)
    {
        if (!frontmatter.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
            throw new GBrainReadbackMismatchException($"GBrain page frontmatter field {name} does not match.");
    }

    private static void EnsureScope(
        GBrainSoulScope expected,
        string sourceId,
        string soulId,
        string deviceBindingId,
        string platformAccountId)
    {
        if (!string.Equals(expected.SourceId, sourceId, StringComparison.Ordinal) ||
            !string.Equals(expected.SoulId, soulId, StringComparison.Ordinal) ||
            !string.Equals(expected.DeviceBindingId, deviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(expected.PlatformAccountId, platformAccountId, StringComparison.Ordinal))
            throw new GBrainAuthorizationException("GBrain page crosses Soul, device, or account scope.");
    }
}
