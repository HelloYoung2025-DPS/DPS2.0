using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dps.SoulMemoryAdapter.Tests;

/// <summary>
/// In-process loopback protocol simulator. It exercises complete HTTP messages
/// without opening a socket, so it is deterministic and cannot reach a real host.
/// It is simulation evidence only.
/// </summary>
internal sealed class GBrainLoopbackSimulator : HttpMessageHandler, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SimulatedPage> _pages = new(StringComparer.Ordinal);
    private readonly GBrainSoulScope _scope;
    private readonly DateTimeOffset _now;

    internal GBrainLoopbackSimulator(GBrainSoulScope scope, DateTimeOffset now)
    {
        _scope = scope;
        _now = now;
        Issuer = new Uri("http://127.0.0.1:48123/", UriKind.Absolute);
        McpEndpoint = new Uri(Issuer, "mcp");
        SeedMarkdown(
            GBrainCompanyProtocolV1.SourceBindingSlug,
            GBrainCompanyProvisioning.CreateSourceBindingMarkdown(scope));
    }

    internal Uri Issuer { get; }
    internal Uri McpEndpoint { get; }
    internal bool DriftGetPageCapability { get; set; }
    internal bool OmitInitializeToolsCapability { get; set; }
    internal bool RedirectDiscovery { get; set; }
    internal bool OversizeInitialize { get; set; }
    internal bool MalformedWhoAmIErrorFlag { get; set; }
    internal bool IgnoreDeletedFilter { get; set; }
    internal bool OmitSearchSource { get; set; }
    internal string? SearchSourceOverride { get; set; }
    internal bool MalformedNextPutResult { get; set; }
    internal bool DropNextProjectionReadAfterPut { get; set; }
    internal CancellationTokenSource? CancelAfterNextPutCommit { get; set; }
    internal Action? AfterNextPutCommit { get; set; }
    internal bool DropAfterNextPutCommit { get; set; }
    internal string? ProjectionReadSourceOverride { get; set; }
    internal bool TamperProjectionBodyOnRead { get; set; }
    internal int PutCalls { get; private set; }
    internal int DeleteCalls { get; private set; }
    internal int RestoreCalls { get; private set; }
    internal int SearchCalls { get; private set; }
    internal int PersonaGetCalls { get; private set; }
    internal int TokenCalls { get; private set; }
    internal int RequestCount { get; private set; }
    internal int ToolsListCalls { get; private set; }
    internal int RequestsWithPreferredHeader { get; private set; }
    internal int RequestsWithNegotiatedHeader { get; private set; }
    internal int RequestsWithSessionHeader { get; private set; }
    internal int ChallengeCalls { get; private set; }
    internal int ProtectedMetadataCalls { get; private set; }
    internal int InitializedNotificationCalls { get; private set; }
    internal int SessionCloseCalls { get; private set; }
    internal bool TokenAudienceAccepted { get; private set; }

    internal HttpClient CreateHttpClient() => new(this, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal void SeedPersona(GBrainPersonaCurrentDocument persona)
    {
        SeedMarkdown(
            GBrainCompanyProtocolV1.PersonaCurrentSlug,
            GBrainCompanyPageCodec.RenderPersonaMarkdown(persona));
    }

    internal void MarkPageSoftDeleted(string slug)
    {
        lock (_gate)
        {
            if (!_pages.TryGetValue(slug, out var page))
                throw new InvalidOperationException("Simulator page does not exist.");
            _pages[slug] = page with { DeletedAt = _now };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(",", header.Value);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                headers[header.Key] = string.Join(",", header.Value);
        }
        var simulated = new SimulatedRequest(
            request.Method.Method,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            headers,
            body);
        lock (_gate)
            RequestCount++;
        var response = Handle(simulated);
        if (response.DropConnection)
            throw new HttpRequestException("Simulated connection loss after mutation commit.");

        var result = new HttpResponseMessage((HttpStatusCode)response.StatusCode)
        {
            Content = new ByteArrayContent(response.Body),
            RequestMessage = request
        };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "UTF-8"
        };
        foreach (var header in response.Headers)
            result.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return result;
    }

    private SimulatedResponse Handle(SimulatedRequest request)
    {
        if (request.Headers.TryGetValue("MCP-Protocol-Version", out var anyProtocolHeader) &&
            string.Equals(anyProtocolHeader, GBrainCompanyProtocolV1.PreferredMcpProtocolVersion, StringComparison.Ordinal))
            RequestsWithPreferredHeader++;
        if (request.Method == "GET" &&
            request.Path == "/.well-known/oauth-authorization-server")
        {
            if (RedirectDiscovery)
                return SimulatedResponse.Redirect(new Uri(Issuer, "redirected-discovery").AbsoluteUri);
            return SimulatedResponse.Json(200, new
            {
                issuer = Issuer.AbsoluteUri,
                token_endpoint = new Uri(Issuer, "token").AbsoluteUri,
                grant_types_supported = new[] { "client_credentials" },
                scopes_supported = new[] { "read", "write", "admin" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post" }
            });
        }

        if (request.Method == "GET" &&
            request.Path is "/.well-known/oauth-protected-resource/mcp" or
                "/.well-known/oauth-protected-resource")
        {
            ProtectedMetadataCalls++;
            return SimulatedResponse.Json(200, new
            {
                resource = Issuer.AbsoluteUri,
                authorization_servers = new[] { Issuer.AbsoluteUri },
                scopes_supported = new[] { "read", "write" },
                resource_name = "DPS GBrain loopback"
            });
        }

        if (request.Method == "POST" && request.Path == "/token")
        {
            lock (_gate)
                TokenCalls++;
            var form = Encoding.UTF8.GetString(request.Body);
            var audienceAccepted = form.Contains(
                "resource=" + Uri.EscapeDataString(Issuer.AbsoluteUri),
                StringComparison.Ordinal);
            if (!form.Contains("grant_type=client_credentials", StringComparison.Ordinal) ||
                !form.Contains("client_id=gbrain_cl_loopback", StringComparison.Ordinal) ||
                !form.Contains("client_secret=loopback-secret", StringComparison.Ordinal) ||
                !form.Contains("scope=read%20write", StringComparison.Ordinal) ||
                !audienceAccepted)
                return SimulatedResponse.Json(401, new { error = "invalid_client" });
            TokenAudienceAccepted = true;
            return SimulatedResponse.Json(200, new
            {
                access_token = "gbrain_at_loopback",
                token_type = "bearer",
                expires_in = 3600,
                scope = "read write"
            });
        }

        if (request.Method == "DELETE" && request.Path == "/mcp")
        {
            if (request.Headers.TryGetValue("Authorization", out var deleteAuthorization) &&
                string.Equals(deleteAuthorization, "Bearer gbrain_at_loopback", StringComparison.Ordinal) &&
                request.Headers.TryGetValue("Mcp-Session-Id", out var deleteSession) &&
                string.Equals(deleteSession, "loopback-session", StringComparison.Ordinal))
            {
                SessionCloseCalls++;
                return SimulatedResponse.Empty(204);
            }
            return SimulatedResponse.Json(401, new { error = "unauthorized" });
        }

        if (request.Method != "POST" || request.Path != "/mcp")
            return SimulatedResponse.Json(404, new { error = "not_found" });
        if (!request.Headers.TryGetValue("Authorization", out var authorization) ||
            !string.Equals(authorization, "Bearer gbrain_at_loopback", StringComparison.Ordinal))
        {
            ChallengeCalls++;
            return SimulatedResponse.Json(
                401,
                new { error = "invalid_token" },
                new Dictionary<string, string>
                {
                    ["WWW-Authenticate"] =
                        $"Bearer error=\"invalid_token\", resource_metadata=\"{new Uri(Issuer, ".well-known/oauth-protected-resource/mcp").AbsoluteUri}\""
                });
        }

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString();
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 0;
        if (request.Headers.TryGetValue("MCP-Protocol-Version", out var protocolHeader) &&
            string.Equals(protocolHeader, GBrainCompanyProtocolV1.TestedMcpProtocolVersion, StringComparison.Ordinal))
            RequestsWithNegotiatedHeader++;
        if (method != "initialize")
        {
            if (!request.Headers.TryGetValue("Mcp-Session-Id", out var sessionHeader) ||
                !string.Equals(sessionHeader, "loopback-session", StringComparison.Ordinal))
                return RpcError(id, -32000, "missing session");
            RequestsWithSessionHeader++;
        }
        if (method == "notifications/initialized")
        {
            InitializedNotificationCalls++;
            return SimulatedResponse.Empty(202);
        }
        if (id == 0)
            return RpcError(0, -32600, "missing id");
        if (method == "initialize")
        {
            if (OversizeInitialize)
                return SimulatedResponse.RawJson(200, "{\"padding\":\"" + new string('x', 16_384) + "\"}");
            var capabilities = OmitInitializeToolsCapability
                ? (object)new { }
                : new { tools = new { } };
            return Rpc(id, new
            {
                protocolVersion = GBrainCompanyProtocolV1.TestedMcpProtocolVersion,
                capabilities,
                serverInfo = new
                {
                    name = GBrainCompanyProtocolV1.TestedServerName,
                    version = GBrainCompanyProtocolV1.TestedServerVersion
                }
            });
        }
        if (method == "tools/list")
        {
            ToolsListCalls++;
            var cursor = root.GetProperty("params").TryGetProperty("cursor", out var cursorValue)
                ? cursorValue.GetString()
                : null;
            var definitions = ToolDefinitions();
            return cursor switch
            {
                null => Rpc(id, new { tools = definitions.Take(3).ToArray(), nextCursor = "tools-page-2" }),
                "tools-page-2" => Rpc(id, new { tools = definitions.Skip(3).ToArray() }),
                _ => RpcError(id, -32602, "invalid cursor")
            };
        }
        if (method != "tools/call")
            return RpcError(id, -32601, "unknown method");

        var parameters = root.GetProperty("params");
        var name = parameters.GetProperty("name").GetString();
        var arguments = parameters.GetProperty("arguments");
        return name switch
        {
            "whoami" when MalformedWhoAmIErrorFlag => ToolResultWithErrorFlag(id, new
            {
                transport = "oauth",
                client_id = "gbrain_cl_loopback",
                client_name = "DPS loopback",
                scopes = new[] { "read", "write" },
                expires_at = _now.AddHours(1).ToUnixTimeSeconds()
            }, "false"),
            "whoami" => ToolResult(id, new
            {
                transport = "oauth",
                client_id = "gbrain_cl_loopback",
                client_name = "DPS loopback",
                scopes = new[] { "read", "write" },
                expires_at = _now.AddHours(1).ToUnixTimeSeconds()
            }),
            "get_page" => GetPage(id, arguments),
            "put_page" => PutPage(id, arguments),
            "delete_page" => DeletePage(id, arguments),
            "restore_page" => RestorePage(id, arguments),
            "search" => Search(id),
            _ => ToolError(id, "unknown_tool")
        };
    }

    private SimulatedResponse GetPage(long id, JsonElement arguments)
    {
        var slug = arguments.GetProperty("slug").GetString()!;
        var includeDeleted = arguments.TryGetProperty("include_deleted", out var include) && include.GetBoolean();
        if (slug == GBrainCompanyProtocolV1.ProjectionCurrentSlug &&
            DropNextProjectionReadAfterPut && PutCalls > 0)
        {
            DropNextProjectionReadAfterPut = false;
            return SimulatedResponse.Drop();
        }
        SimulatedPage? page;
        lock (_gate)
        {
            _pages.TryGetValue(slug, out page);
            if (slug == GBrainCompanyProtocolV1.PersonaCurrentSlug)
                PersonaGetCalls++;
        }
        if (page is null || page.DeletedAt is not null && !includeDeleted && !IgnoreDeletedFilter)
            return ToolError(id, "page_not_found");
        var source = slug == GBrainCompanyProtocolV1.ProjectionCurrentSlug &&
                     ProjectionReadSourceOverride is not null
            ? ProjectionReadSourceOverride
            : page.SourceId;
        var body = slug == GBrainCompanyProtocolV1.ProjectionCurrentSlug && TamperProjectionBodyOnRead
            ? page.CompiledTruth + " "
            : page.CompiledTruth;
        return ToolResult(id, new
        {
            slug = page.Slug,
            source_id = source,
            compiled_truth = body,
            frontmatter = page.Frontmatter,
            source_kind = GBrainCompanyProtocolV1.GBrainRemoteProvenance,
            ingested_via = GBrainCompanyProtocolV1.GBrainRemoteProvenance,
            updated_at = page.UpdatedAt.ToString("O"),
            deleted_at = page.DeletedAt?.ToString("O")
        });
    }

    private SimulatedResponse PutPage(long id, JsonElement arguments)
    {
        var slug = arguments.GetProperty("slug").GetString()!;
        var content = arguments.GetProperty("content").GetString()!;
        lock (_gate)
        {
            PutCalls++;
            SeedMarkdown(slug, content);
            var afterCommit = AfterNextPutCommit;
            AfterNextPutCommit = null;
            afterCommit?.Invoke();
            var cancellation = CancelAfterNextPutCommit;
            CancelAfterNextPutCommit = null;
            cancellation?.Cancel();
            if (MalformedNextPutResult)
            {
                MalformedNextPutResult = false;
                return Rpc(id, new { unexpected = "missing-content" });
            }
            if (DropAfterNextPutCommit)
            {
                DropAfterNextPutCommit = false;
                return SimulatedResponse.Drop();
            }
        }
        return ToolResult(id, new { status = "written", slug });
    }

    private SimulatedResponse DeletePage(long id, JsonElement arguments)
    {
        var slug = arguments.GetProperty("slug").GetString()!;
        lock (_gate)
        {
            DeleteCalls++;
            if (!_pages.TryGetValue(slug, out var page))
                return ToolError(id, "page_not_found");
            if (page.DeletedAt is not null)
                return ToolResult(id, new { status = "already_soft_deleted", slug, deleted_at = page.DeletedAt.Value.ToString("O") });
            _pages[slug] = page with { DeletedAt = _now };
        }
        return ToolResult(id, new { status = "soft_deleted", slug });
    }

    private SimulatedResponse RestorePage(long id, JsonElement arguments)
    {
        var slug = arguments.GetProperty("slug").GetString()!;
        lock (_gate)
        {
            RestoreCalls++;
            if (!_pages.TryGetValue(slug, out var page))
                return ToolError(id, "page_not_found");
            if (page.DeletedAt is null)
                return ToolResult(id, new { status = "already_active", slug });
            _pages[slug] = page with { DeletedAt = null, UpdatedAt = _now };
        }
        return ToolResult(id, new { status = "restored", slug });
    }

    private SimulatedResponse Search(long id)
    {
        lock (_gate)
        {
            SearchCalls++;
            if (_pages.TryGetValue(GBrainCompanyProtocolV1.ProjectionCurrentSlug, out var page) &&
                page.DeletedAt is null)
            {
                var candidate = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["slug"] = page.Slug,
                    ["updated_at"] = page.UpdatedAt.ToString("O")
                };
                if (!OmitSearchSource)
                    candidate["source_id"] = SearchSourceOverride ?? page.SourceId;
                return ToolResult(id, new[]
                {
                    candidate
                });
            }
        }
        return ToolResult(id, Array.Empty<object>());
    }

    private object[] ToolDefinitions()
    {
        return GBrainCapabilityProfile.Required.Select(profile =>
        {
            var properties = profile.Properties.ToDictionary(
                pair => pair.Key,
                pair => (object)new { type = pair.Value },
                StringComparer.Ordinal);
            if (DriftGetPageCapability && profile.Name == "get_page")
                properties.Remove("include_deleted");
            return (object)new
            {
                name = profile.Name,
                description = "loopback pinned capability",
                inputSchema = new
                {
                    type = "object",
                    properties,
                    required = profile.Required.OrderBy(value => value, StringComparer.Ordinal).ToArray()
                }
            };
        }).ToArray();
    }

    private void SeedMarkdown(string slug, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 4 || lines[0] != "---")
            throw new InvalidOperationException("Simulator received invalid fixed markdown.");
        var frontmatter = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 1;
        for (; index < lines.Length && lines[index] != "---"; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0)
                throw new InvalidOperationException("Simulator received invalid frontmatter.");
            frontmatter.Add(lines[index][..separator], lines[index][(separator + 1)..].TrimStart());
        }
        if (index >= lines.Length)
            throw new InvalidOperationException("Simulator received unterminated frontmatter.");
        var body = string.Join('\n', lines[(index + 1)..]);
        if (body.EndsWith('\n'))
            body = body[..^1];
        _pages[slug] = new SimulatedPage(
            slug,
            _scope.SourceId,
            body,
            frontmatter,
            _now,
            null);
    }

    private static SimulatedResponse Rpc(long id, object result) =>
        SimulatedResponse.Json(
            200,
            new { jsonrpc = "2.0", id, result },
            new Dictionary<string, string> { ["Mcp-Session-Id"] = "loopback-session" });

    private static SimulatedResponse RpcError(long id, int code, string message) =>
        SimulatedResponse.Json(200, new { jsonrpc = "2.0", id, error = new { code, message } });

    private static SimulatedResponse ToolResult(long id, object value) =>
        Rpc(id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } },
            isError = false
        });

    private static SimulatedResponse ToolResultWithErrorFlag(long id, object value, object isError) =>
        Rpc(id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } },
            isError
        });

    private static SimulatedResponse ToolError(long id, string error) =>
        Rpc(id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { error }) } },
            isError = true
        });

    private sealed record SimulatedPage(
        string Slug,
        string SourceId,
        string CompiledTruth,
        IReadOnlyDictionary<string, string> Frontmatter,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DeletedAt);

    private sealed record SimulatedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed record SimulatedResponse(
        int StatusCode,
        byte[] Body,
        IReadOnlyDictionary<string, string> Headers,
        bool DropConnection)
    {
        internal static SimulatedResponse Json(
            int statusCode,
            object value,
            IReadOnlyDictionary<string, string>? headers = null) =>
            RawJson(statusCode, JsonSerializer.Serialize(value), headers);

        internal static SimulatedResponse RawJson(
            int statusCode,
            string json,
            IReadOnlyDictionary<string, string>? headers = null) =>
            new(statusCode, Encoding.UTF8.GetBytes(json), headers ?? new Dictionary<string, string>(), false);

        internal static SimulatedResponse Redirect(string location) =>
            new(302, Array.Empty<byte>(), new Dictionary<string, string> { ["Location"] = location }, false);

        internal static SimulatedResponse Drop() =>
            new(0, Array.Empty<byte>(), new Dictionary<string, string>(), true);

        internal static SimulatedResponse Empty(int statusCode) =>
            new(statusCode, Array.Empty<byte>(), new Dictionary<string, string>(), false);
    }
}

internal sealed class FixedGBrainCredentialSource : IGBrainOAuthClientCredentialSource
{
    private readonly GBrainSoulScope _boundScope;
    private readonly string[] _grantedScopes;

    internal FixedGBrainCredentialSource(
        GBrainSoulScope boundScope,
        params string[] grantedScopes)
    {
        _boundScope = boundScope;
        _grantedScopes = grantedScopes.Length == 0 ? ["read", "write"] : grantedScopes;
    }

    public ValueTask<GBrainOAuthClientCredentialLease> AcquireAsync(
        GBrainOAuthCredentialRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GBrainOAuthClientCredentialLease(
            "gbrain_cl_loopback",
            "loopback-secret"u8,
            _boundScope.SourceId,
            _boundScope.SoulId,
            [_boundScope.SourceId],
            _grantedScopes));
    }
}

internal sealed class FixedGBrainProjectionMutationJournal : IGBrainProjectionMutationJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<(string SoulId, string Key), StoredMutation> _mutations = new();
    private readonly Dictionary<string, StoredMutation> _unresolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredMutation> _latestVerified = new(StringComparer.Ordinal);
    private long _nextReservation;

    public ValueTask<GBrainProjectionMutationReservation> ReserveOrReadAsync(
        GBrainProjectionMutationIntent requested,
        string leaseFenceToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        requested.Validate();
        GBrainCompanyOptions.RequireToken(leaseFenceToken, 128, nameof(leaseFenceToken));
        lock (_gate)
        {
            var key = (requested.SoulId, requested.IdempotencyKey);
            if (_mutations.TryGetValue(key, out var existing))
            {
                if (!Equals(existing.Intent, requested))
                    throw new InvalidOperationException(
                        "The mutation idempotency key is already bound to different content.");
                if (!string.Equals(
                        existing.Status,
                        GBrainProjectionMutationReservation.VerifiedStatus,
                        StringComparison.Ordinal))
                {
                    existing = existing with { LeaseFenceToken = leaseFenceToken };
                    _mutations[key] = existing;
                    _unresolved[requested.SoulId] = existing;
                }
                return ValueTask.FromResult(existing.ToReservation(created: false, leaseFenceToken));
            }

            if (_unresolved.ContainsKey(requested.SoulId))
                throw new GBrainMutationQuarantinedException(
                    "The Soul has an unresolved dispatched mutation.");
            if (_latestVerified.TryGetValue(requested.SoulId, out var latest) &&
                requested.OccurredAt <= latest.Intent.OccurredAt)
            {
                throw new InvalidOperationException(
                    "A stale or ambiguously ordered mutation cannot follow the latest verified mutation.");
            }

            var reservationId = "gbrain_mut_" +
                Interlocked.Increment(ref _nextReservation).ToString("D12", CultureInfo.InvariantCulture);
            var stored = new StoredMutation(
                requested,
                reservationId,
                leaseFenceToken,
                GBrainProjectionMutationReservation.ReservedStatus);
            _mutations.Add(key, stored);
            _unresolved.Add(requested.SoulId, stored);
            return ValueTask.FromResult(stored.ToReservation(created: true, leaseFenceToken));
        }
    }

    public ValueTask<GBrainProjectionMutationReservation> MarkDispatchedAsync(
        GBrainProjectionMutationReservation reservation,
        string leaseFenceToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reservation.Validate();
        lock (_gate)
        {
            var stored = RequireCurrent(reservation, leaseFenceToken);
            if (!string.Equals(
                    stored.Status,
                    GBrainProjectionMutationReservation.ReservedStatus,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Only a reserved mutation can be dispatched.");
            stored = stored with
            {
                Status = GBrainProjectionMutationReservation.DispatchedStatus
            };
            Store(stored);
            return ValueTask.FromResult(stored.ToReservation(created: false, leaseFenceToken));
        }
    }

    public ValueTask<GBrainProjectionMutationReservation> MarkVerifiedAsync(
        GBrainProjectionMutationReservation reservation,
        string leaseFenceToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reservation.Validate();
        lock (_gate)
        {
            var stored = RequireCurrent(reservation, leaseFenceToken);
            if (string.Equals(
                    stored.Status,
                    GBrainProjectionMutationReservation.VerifiedStatus,
                    StringComparison.Ordinal))
                return ValueTask.FromResult(stored.ToReservation(created: false, leaseFenceToken));
            stored = stored with
            {
                Status = GBrainProjectionMutationReservation.VerifiedStatus
            };
            Store(stored);
            _unresolved.Remove(stored.Intent.SoulId);
            if (!_latestVerified.TryGetValue(stored.Intent.SoulId, out var latest) ||
                stored.Intent.OccurredAt > latest.Intent.OccurredAt)
                _latestVerified[stored.Intent.SoulId] = stored;
            return ValueTask.FromResult(stored.ToReservation(created: false, leaseFenceToken));
        }
    }

    private StoredMutation RequireCurrent(
        GBrainProjectionMutationReservation reservation,
        string leaseFenceToken)
    {
        var key = (reservation.Intent.SoulId, reservation.Intent.IdempotencyKey);
        if (!_mutations.TryGetValue(key, out var stored) ||
            !Equals(stored.Intent, reservation.Intent) ||
            !string.Equals(stored.ReservationId, reservation.ReservationId, StringComparison.Ordinal) ||
            !string.Equals(stored.LeaseFenceToken, leaseFenceToken, StringComparison.Ordinal) ||
            !string.Equals(reservation.LeaseFenceToken, leaseFenceToken, StringComparison.Ordinal))
        {
            throw new GBrainMutationLeaseLostException(
                "Mutation journal reservation or fence is no longer current.");
        }
        return stored;
    }

    private void Store(StoredMutation stored)
    {
        _mutations[(stored.Intent.SoulId, stored.Intent.IdempotencyKey)] = stored;
        if (!string.Equals(
                stored.Status,
                GBrainProjectionMutationReservation.VerifiedStatus,
                StringComparison.Ordinal))
            _unresolved[stored.Intent.SoulId] = stored;
    }

    private sealed record StoredMutation(
        GBrainProjectionMutationIntent Intent,
        string ReservationId,
        string LeaseFenceToken,
        string Status)
    {
        internal GBrainProjectionMutationReservation ToReservation(
            bool created,
            string leaseFenceToken) =>
            new(Intent, ReservationId, leaseFenceToken, Status, created);
    }
}

internal sealed class FixedGBrainSoulMutationLeaseProvider : IGBrainSoulMutationLeaseProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _activeLoss = new(StringComparer.Ordinal);
    private long _nextFence;

    public async ValueTask<GBrainSoulMutationLease> AcquireAsync(
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        SemaphoreSlim semaphore;
        lock (_gate)
        {
            if (!_leases.TryGetValue(scope.SourceId, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _leases.Add(scope.SourceId, semaphore);
            }
        }
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        var loss = new CancellationTokenSource();
        var fence = "gbrain_fence_" +
            Interlocked.Increment(ref _nextFence).ToString("D12", CultureInfo.InvariantCulture);
        lock (_gate)
            _activeLoss[scope.SourceId] = loss;
        return new FixedGBrainSoulMutationLease(this, scope, fence, loss, semaphore);
    }

    internal void LoseActiveLease(string sourceId)
    {
        lock (_gate)
        {
            if (_activeLoss.TryGetValue(sourceId, out var loss))
                loss.Cancel();
        }
    }

    private void Release(
        string sourceId,
        CancellationTokenSource loss,
        SemaphoreSlim semaphore)
    {
        lock (_gate)
        {
            if (_activeLoss.TryGetValue(sourceId, out var active) && ReferenceEquals(active, loss))
                _activeLoss.Remove(sourceId);
        }
        loss.Cancel();
        loss.Dispose();
        semaphore.Release();
    }

    private sealed class FixedGBrainSoulMutationLease : GBrainSoulMutationLease
    {
        private FixedGBrainSoulMutationLeaseProvider? _owner;
        private CancellationTokenSource? _loss;
        private SemaphoreSlim? _semaphore;

        internal FixedGBrainSoulMutationLease(
            FixedGBrainSoulMutationLeaseProvider owner,
            GBrainSoulScope scope,
            string fenceToken,
            CancellationTokenSource loss,
            SemaphoreSlim semaphore)
            : base(scope, fenceToken, loss.Token)
        {
            _owner = owner;
            _loss = loss;
            _semaphore = semaphore;
        }

        public override ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var loss = Interlocked.Exchange(ref _loss, null);
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            if (owner is not null && loss is not null && semaphore is not null)
                owner.Release(Scope.SourceId, loss, semaphore);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;
    internal FixedTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}
