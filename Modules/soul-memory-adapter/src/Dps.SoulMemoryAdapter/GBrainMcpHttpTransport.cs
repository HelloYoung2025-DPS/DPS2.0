using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dps.SoulMemoryAdapter;

internal enum GBrainMcpTool
{
    GetPage,
    PutPage,
    DeletePage,
    RestorePage,
    Search,
    WhoAmI
}

internal static class GBrainMcpToolNames
{
    internal static string Get(GBrainMcpTool tool) => tool switch
    {
        GBrainMcpTool.GetPage => "get_page",
        GBrainMcpTool.PutPage => "put_page",
        GBrainMcpTool.DeletePage => "delete_page",
        GBrainMcpTool.RestorePage => "restore_page",
        GBrainMcpTool.Search => "search",
        GBrainMcpTool.WhoAmI => "whoami",
        _ => throw new GBrainCapabilityException("Unknown fixed GBrain operation.")
    };
}

internal sealed class GBrainOAuthClientCredentialsTokenProvider
{
    private static readonly string[] RequiredScopes = ["read", "write"];
    private readonly GBrainCompanyOptions _options;
    private readonly IGBrainOAuthClientCredentialSource _credentialSource;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    internal GBrainOAuthClientCredentialsTokenProvider(
        GBrainCompanyOptions options,
        IGBrainOAuthClientCredentialSource credentialSource,
        HttpClient httpClient,
        TimeProvider timeProvider)
    {
        _options = options;
        _credentialSource = credentialSource;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    internal async ValueTask<GBrainOAuthAccessTokenLease> AcquireAsync(
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        var request = new GBrainOAuthCredentialRequest(scope.SourceId, scope.SoulId, RequiredScopes);
        using var credentials = await _credentialSource.AcquireAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(credentials);
        credentials.ValidateFor(request);

        var resource = await DiscoverProtectedResourceAsync(cancellationToken).ConfigureAwait(false);
        var tokenEndpoint = await DiscoverTokenEndpointAsync(resource.AuthorizationServer, cancellationToken)
            .ConfigureAwait(false);
        return await ExchangeAsync(
            tokenEndpoint,
            resource.ResourceAudience,
            credentials,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GBrainProtectedResource> DiscoverProtectedResourceAsync(
        CancellationToken cancellationToken)
    {
        var challengedMetadata = await DiscoverChallengeMetadataUriAsync(cancellationToken)
            .ConfigureAwait(false);
        var metadataUri = challengedMetadata ?? _options.OAuthProtectedResourceEndpoint;
        var metadata = await TryReadMetadataAsync(metadataUri, cancellationToken).ConfigureAwait(false);
        if (metadata is null && challengedMetadata is null)
        {
            metadataUri = _options.OAuthProtectedResourceRootEndpoint;
            metadata = await TryReadMetadataAsync(metadataUri, cancellationToken).ConfigureAwait(false);
        }
        if (metadata is null)
            throw new GBrainAuthorizationException("GBrain does not publish RFC 9728 protected resource metadata.");

        using (metadata)
        {
            var root = GBrainHttpJson.RequireObject(metadata.RootElement, "OAuth protected resource metadata");
            var resourceText = GBrainHttpJson.RequireString(root, "resource", 2_048);
            if (!Uri.TryCreate(resourceText, UriKind.Absolute, out var resourceAudience) ||
                !UriEquals(_options.ExpectedResourceAudience, resourceAudience))
                throw new GBrainAuthorizationException("OAuth protected resource audience does not match the deployment pin.");

            var authorizationServers = GBrainHttpJson.RequireProperty(root, "authorization_servers");
            if (authorizationServers.ValueKind != JsonValueKind.Array ||
                authorizationServers.GetArrayLength() != 1 ||
                authorizationServers[0].ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(authorizationServers[0].GetString(), UriKind.Absolute, out var authorizationServer) ||
                !UriEquals(_options.IssuerUri, authorizationServer))
                throw new GBrainAuthorizationException("OAuth protected resource metadata has an untrusted authorization server.");
            RequireArrayContains(root, "scopes_supported", "read");
            RequireArrayContains(root, "scopes_supported", "write");
            return new GBrainProtectedResource(resourceAudience, authorizationServer);
        }
    }

    private async Task<Uri?> DiscoverChallengeMetadataUriAsync(CancellationToken cancellationToken)
    {
        var id = 1L;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new
            {
                protocolVersion = _options.PreferredMcpProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "dps-oauth-discovery", version = "0.2.0" }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.McpEndpoint);
        request.Content = new GBrainZeroingByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _options.PreferredMcpProtocolVersion);
        using var response = await SendAsync(request, mutating: false, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            throw new GBrainAuthorizationException("Unauthenticated MCP discovery probe must return HTTP 401.");

        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(value =>
            string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        if (challenge is null)
            throw new GBrainAuthorizationException("MCP 401 response is missing a Bearer challenge.");
        var metadataText = ExtractQuotedChallengeParameter(challenge.Parameter, "resource_metadata");
        if (metadataText is null)
        {
            if (_options.AllowPinnedChallengeMetadataFallback)
                return null;
            throw new GBrainAuthorizationException("MCP 401 response is missing resource_metadata.");
        }
        if (!Uri.TryCreate(metadataText, UriKind.Absolute, out var metadataUri))
            throw new GBrainAuthorizationException("MCP resource_metadata challenge is not an absolute URL.");
        var pathAware = _options.OAuthProtectedResourceEndpoint.AbsolutePath;
        var rootPath = _options.OAuthProtectedResourceRootEndpoint.AbsolutePath;
        if (!GBrainCompanyOptions.SameAuthority(_options.IssuerUri, metadataUri) ||
            (metadataUri.AbsolutePath != pathAware && metadataUri.AbsolutePath != rootPath) ||
            !string.IsNullOrEmpty(metadataUri.Query) || !string.IsNullOrEmpty(metadataUri.Fragment) ||
            !string.IsNullOrEmpty(metadataUri.UserInfo))
            throw new GBrainAuthorizationException("MCP resource_metadata challenge is outside the trusted endpoints.");
        return metadataUri;
    }

    private async Task<JsonDocument?> TryReadMetadataAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _options.PreferredMcpProtocolVersion);
        using var response = await SendAsync(request, mutating: false, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        RequireSuccess(response, "OAuth metadata discovery", mutating: false);
        return await GBrainHttpJson.ReadDocumentAsync(
            response,
            _options.MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> DiscoverTokenEndpointAsync(
        Uri authorizationServer,
        CancellationToken cancellationToken)
    {
        if (!UriEquals(_options.IssuerUri, authorizationServer))
            throw new GBrainAuthorizationException("Authorization server does not match the deployment pin.");
        JsonDocument? document = null;
        foreach (var endpoint in new[] { _options.OAuthDiscoveryEndpoint, _options.OidcDiscoveryEndpoint })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _options.PreferredMcpProtocolVersion);
            using var response = await SendAsync(request, mutating: false, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                continue;
            RequireSuccess(response, "OAuth authorization-server discovery", mutating: false);
            document = await GBrainHttpJson.ReadDocumentAsync(
                response,
                _options.MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            break;
        }
        if (document is null)
            throw new GBrainAuthorizationException("OAuth RFC 8414 and OIDC discovery both failed.");
        using (document)
        {
        var root = GBrainHttpJson.RequireObject(document.RootElement, "OAuth discovery");

        var issuer = GBrainHttpJson.RequireString(root, "issuer", 2_048);
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var discoveredIssuer) ||
            !UriEquals(_options.IssuerUri, discoveredIssuer))
        {
            throw new GBrainAuthorizationException(
                "OAuth discovery issuer does not exactly match the configured issuer.");
        }

        var tokenEndpointText = GBrainHttpJson.RequireString(root, "token_endpoint", 2_048);
        if (!Uri.TryCreate(tokenEndpointText, UriKind.Absolute, out var tokenEndpoint))
            throw new GBrainAuthorizationException("OAuth discovery token endpoint is invalid.");
        _options.ValidateDiscoveredTokenEndpoint(tokenEndpoint);

        RequireArrayContains(root, "grant_types_supported", "client_credentials");
        RequireArrayContains(root, "scopes_supported", "read");
        RequireArrayContains(root, "scopes_supported", "write");
        RequireArrayContains(root, "token_endpoint_auth_methods_supported", "client_secret_post");
        if (root.TryGetProperty("registration_endpoint", out var registration) &&
            registration.ValueKind != JsonValueKind.Null)
            throw new GBrainAuthorizationException("Dynamic client registration is forbidden for the DPS adapter.");
        return tokenEndpoint;
        }
    }

    private async ValueTask<GBrainOAuthAccessTokenLease> ExchangeAsync(
        Uri tokenEndpoint,
        Uri resourceAudience,
        GBrainOAuthClientCredentialLease credentials,
        CancellationToken cancellationToken)
    {
        var requestBytes = BuildFormBody(credentials, resourceAudience);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            request.Content = new GBrainZeroingByteArrayContent(requestBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/x-www-form-urlencoded") { CharSet = "UTF-8" };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await SendAsync(request, mutating: false, cancellationToken)
                .ConfigureAwait(false);
            RequireSuccess(response, "OAuth token exchange", mutating: false);
            using var document = await GBrainHttpJson.ReadDocumentAsync(
                response,
                _options.MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            var root = GBrainHttpJson.RequireObject(document.RootElement, "OAuth token response");
            var tokenText = GBrainHttpJson.RequireString(root, "access_token", 8_192);
            if (tokenText.Any(static character => character is < '!' or > '~'))
                throw new GBrainAuthorizationException("OAuth access token is not visible ASCII.");
            var tokenBytes = Encoding.ASCII.GetBytes(tokenText);
            try
            {
                if (!string.Equals(
                        GBrainHttpJson.RequireString(root, "token_type", 32),
                        "bearer",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new GBrainAuthorizationException("OAuth token_type must be bearer.");
                }

                var expiresIn = GBrainHttpJson.RequireInt32(root, "expires_in", 60, 86_400);
                var granted = ParseScopeSet(GBrainHttpJson.RequireString(root, "scope", 128));
                if (!granted.SetEquals(RequiredScopes))
                    throw new GBrainAuthorizationException("OAuth token must grant exactly read and write scopes.");

                return new GBrainOAuthAccessTokenLease(
                    tokenBytes,
                    credentials.ClientId,
                    credentials.BoundSourceId,
                    credentials.BoundSoulId,
                    _timeProvider.GetUtcNow().AddSeconds(expiresIn));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool mutating,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GBrainProtocolException("GBrain OAuth request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new GBrainProtocolException("GBrain OAuth transport failed.", exception);
        }
    }

    private static void RequireSuccess(
        HttpResponseMessage response,
        string operation,
        bool mutating)
    {
        if (response.IsSuccessStatusCode)
            return;
        if ((int)response.StatusCode is 401 or 403)
            throw new GBrainAuthorizationException($"{operation} was rejected.");
        throw new GBrainProtocolException($"{operation} returned HTTP {(int)response.StatusCode}.");
    }

    private static byte[] BuildFormBody(
        GBrainOAuthClientCredentialLease credentials,
        Uri resourceAudience)
    {
        const string Prefix = "grant_type=client_credentials&client_id=";
        const string SecretMarker = "&client_secret=";
        const string ScopeAndResourceMarker = "&scope=read%20write&resource=";
        var clientId = Encoding.ASCII.GetBytes(credentials.ClientId);
        var resource = Encoding.ASCII.GetBytes(resourceAudience.AbsoluteUri);
        var length = checked(
            Prefix.Length + PercentEncodedLength(clientId) +
            SecretMarker.Length + PercentEncodedLength(credentials.ClientSecretUtf8) +
            ScopeAndResourceMarker.Length + PercentEncodedLength(resource));
        var body = new byte[length];
        var offset = 0;
        try
        {
            WriteAscii(body, ref offset, Prefix);
            WritePercentEncoded(body, ref offset, clientId);
            WriteAscii(body, ref offset, SecretMarker);
            WritePercentEncoded(body, ref offset, credentials.ClientSecretUtf8);
            WriteAscii(body, ref offset, ScopeAndResourceMarker);
            WritePercentEncoded(body, ref offset, resource);
            if (offset != body.Length)
                throw new GBrainProtocolException("OAuth request body length calculation failed.");
            return body;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(body);
            throw;
        }
    }

    private static int PercentEncodedLength(ReadOnlySpan<byte> value)
    {
        var length = 0;
        foreach (var character in value)
            length = checked(length + (IsFormUnreserved(character) ? 1 : 3));
        return length;
    }

    private static void WriteAscii(Span<byte> destination, ref int offset, string value)
    {
        foreach (var character in value)
            destination[offset++] = checked((byte)character);
    }

    private static void WritePercentEncoded(
        Span<byte> destination,
        ref int offset,
        ReadOnlySpan<byte> value)
    {
        const string Hex = "0123456789ABCDEF";
        foreach (var character in value)
        {
            if (IsFormUnreserved(character))
            {
                destination[offset++] = character;
                continue;
            }

            destination[offset++] = (byte)'%';
            destination[offset++] = (byte)Hex[character >> 4];
            destination[offset++] = (byte)Hex[character & 0x0f];
        }
    }

    private static bool IsFormUnreserved(byte character) =>
        character is >= (byte)'a' and <= (byte)'z' or
            >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'0' and <= (byte)'9' or
            (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';

    private static void RequireArrayContains(JsonElement root, string property, string expected)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new GBrainAuthorizationException($"OAuth discovery is missing {property}.");
        if (!value.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), expected, StringComparison.Ordinal)))
        {
            throw new GBrainAuthorizationException($"OAuth discovery does not advertise {expected}.");
        }
    }

    private static HashSet<string> ParseScopeSet(string value)
    {
        var scopes = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HashSet<string>(scopes, StringComparer.Ordinal);
    }

    private static bool UriEquals(Uri left, Uri right) =>
        GBrainCompanyOptions.SameAuthority(left, right) &&
        string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.Ordinal) &&
        string.Equals(left.Query, right.Query, StringComparison.Ordinal) &&
        string.Equals(left.Fragment, right.Fragment, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(right.UserInfo);

    private static string? ExtractQuotedChallengeParameter(string? parameters, string name)
    {
        if (string.IsNullOrEmpty(parameters) || parameters.Length > 4_096)
            return null;
        var marker = name + "=\"";
        var start = parameters.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = parameters.IndexOf('"', start);
        if (end <= start)
            throw new GBrainAuthorizationException("MCP Bearer challenge is malformed.");
        var value = parameters[start..end];
        if (value.Any(static character => char.IsControl(character) || character == '\\'))
            throw new GBrainAuthorizationException("MCP Bearer challenge contains an unsafe metadata URL.");
        return value;
    }

    private sealed record GBrainProtectedResource(Uri ResourceAudience, Uri AuthorizationServer);
}

internal sealed class GBrainMcpSession : IAsyncDisposable
{
    private readonly GBrainCompanyOptions _options;
    private readonly HttpClient _httpClient;
    private readonly GBrainOAuthAccessTokenLease _token;
    private readonly TimeProvider _timeProvider;
    private long _requestId;
    private string? _negotiatedProtocolVersion;
    private string? _sessionId;

    private GBrainMcpSession(
        GBrainCompanyOptions options,
        HttpClient httpClient,
        GBrainOAuthAccessTokenLease token,
        TimeProvider timeProvider)
    {
        _options = options;
        _httpClient = httpClient;
        _token = token;
        _timeProvider = timeProvider;
    }

    internal static async ValueTask<GBrainMcpSession> OpenAsync(
        GBrainCompanyOptions options,
        IGBrainOAuthClientCredentialSource credentialSource,
        HttpClient httpClient,
        TimeProvider timeProvider,
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        var provider = new GBrainOAuthClientCredentialsTokenProvider(
            options,
            credentialSource,
            httpClient,
            timeProvider);
        var token = await provider.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
        var session = new GBrainMcpSession(options, httpClient, token, timeProvider);
        try
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await session.SendInitializedNotificationAsync(cancellationToken).ConfigureAwait(false);
            await session.ValidateCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            await session.ValidateIdentityAsync(scope, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task<JsonDocument> CallToolAsync(
        GBrainMcpTool tool,
        object arguments,
        bool mutating,
        CancellationToken cancellationToken)
    {
        return CallToolCoreAsync(tool, arguments, mutating, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_sessionId is not null)
                await CloseSessionAsync().ConfigureAwait(false);
        }
        catch (GBrainProtocolException)
        {
            // Session cleanup is best-effort and cannot revoke an already-issued
            // exact read-back receipt. The short-lived token is still zeroed.
        }
        finally
        {
            _token.Dispose();
        }
    }

    private async Task SendInitializedNotificationAsync(CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });
        using var request = CreateMcpRequest(HttpMethod.Post, body, includeAuthorization: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GBrainProtocolException("MCP initialized notification timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new GBrainProtocolException("MCP initialized notification failed.", exception);
        }
        using (response)
        {
            if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.NoContent))
                throw new GBrainProtocolException("MCP initialized notification was not accepted.");
        }
    }

    private async Task CloseSessionAsync()
    {
        using var request = CreateMcpRequest(HttpMethod.Delete, body: null, includeAuthorization: true);
        using var timeout = new CancellationTokenSource(_options.OperationTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new GBrainProtocolException("MCP session close failed.", exception);
        }
        using (response)
        {
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NoContent))
                throw new GBrainProtocolException("MCP session close was not accepted.");
        }
    }

    private HttpRequestMessage CreateMcpRequest(
        HttpMethod method,
        byte[]? body,
        bool includeAuthorization)
    {
        var request = new HttpRequestMessage(method, _options.McpEndpoint);
        if (body is not null)
        {
            request.Content = new GBrainZeroingByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation(
            "MCP-Protocol-Version",
            _negotiatedProtocolVersion ?? _options.PreferredMcpProtocolVersion);
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (includeAuthorization)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Encoding.ASCII.GetString(_token.TokenUtf8));
        }
        return request;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var response = await PostJsonRpcAsync(
            "initialize",
            new
            {
                protocolVersion = _options.PreferredMcpProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "dps-soul-memory-adapter", version = "0.2.0" }
            },
            mutating: false,
            cancellationToken).ConfigureAwait(false);
        var result = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(response.RootElement, "result"),
            "MCP initialize result");
        var capabilities = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(result, "capabilities"),
            "MCP initialize capabilities");
        _ = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(capabilities, "tools"),
            "MCP initialize tools capability");
        var negotiated = GBrainHttpJson.RequireString(result, "protocolVersion", 32);
        if (!_options.IsAllowedMcpProtocolVersion(negotiated))
        {
            throw new GBrainCapabilityException("GBrain negotiated an untested MCP protocol version.");
        }
        _negotiatedProtocolVersion = negotiated;

        var info = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(result, "serverInfo"),
            "MCP serverInfo");
        if (!string.Equals(
                GBrainHttpJson.RequireString(info, "name", 64),
                _options.ExpectedServerName,
                StringComparison.Ordinal) ||
            !string.Equals(
                GBrainHttpJson.RequireString(info, "version", 64),
                _options.ExpectedServerVersion,
                StringComparison.Ordinal))
        {
            throw new GBrainCapabilityException("GBrain server name or version does not match the deployment pin.");
        }
    }

    private async Task ValidateCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var discovered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 16; pageNumber++)
        {
            using var response = await PostJsonRpcAsync(
                "tools/list",
                cursor is null ? new { } : new { cursor },
                mutating: false,
                cancellationToken).ConfigureAwait(false);
            var result = GBrainHttpJson.RequireObject(
                GBrainHttpJson.RequireProperty(response.RootElement, "result"),
                "MCP tools/list result");
            var tools = GBrainHttpJson.RequireProperty(result, "tools");
            if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > 256)
                throw new GBrainCapabilityException("MCP tools/list returned an invalid page.");
            foreach (var item in tools.EnumerateArray())
            {
                var tool = GBrainHttpJson.RequireObject(item, "MCP tool");
                var name = GBrainHttpJson.RequireString(tool, "name", 128);
                if (!discovered.TryAdd(name, tool.Clone()))
                    throw new GBrainCapabilityException("MCP tools/list contains duplicate tool names.");
                if (discovered.Count > 512)
                    throw new GBrainCapabilityException("MCP tools/list exceeded the total tool limit.");
            }

            if (!result.TryGetProperty("nextCursor", out var nextCursor) ||
                nextCursor.ValueKind == JsonValueKind.Null)
                break;
            if (nextCursor.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(nextCursor.GetString()) ||
                nextCursor.GetString()!.Length > 512 ||
                !seenCursors.Add(nextCursor.GetString()!))
                throw new GBrainCapabilityException("MCP tools/list pagination cursor is invalid.");
            cursor = nextCursor.GetString();
            if (pageNumber == 15)
                throw new GBrainCapabilityException("MCP tools/list exceeded the pagination limit.");
        }

        foreach (var profile in GBrainCapabilityProfile.Required)
        {
            if (!discovered.TryGetValue(profile.Name, out var tool))
                throw new GBrainCapabilityException($"Pinned GBrain tool {profile.Name} is missing.");
            profile.Validate(tool);
        }
    }

    private async Task ValidateIdentityAsync(
        GBrainSoulScope scope,
        CancellationToken cancellationToken)
    {
        using var document = await CallToolCoreAsync(
            GBrainMcpTool.WhoAmI,
            new { },
            mutating: false,
            cancellationToken).ConfigureAwait(false);
        var root = GBrainHttpJson.RequireObject(document.RootElement, "whoami result");
        if (!string.Equals(
                GBrainHttpJson.RequireString(root, "transport", 16),
                "oauth",
                StringComparison.Ordinal) ||
            !string.Equals(
                GBrainHttpJson.RequireString(root, "client_id", 256),
                _token.ClientId,
                StringComparison.Ordinal))
        {
            throw new GBrainAuthorizationException("GBrain whoami does not match the OAuth client lease.");
        }

        var scopes = GBrainHttpJson.RequireProperty(root, "scopes");
        if (scopes.ValueKind != JsonValueKind.Array)
            throw new GBrainAuthorizationException("GBrain whoami scopes are missing.");
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in scopes.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !values.Add(value.GetString()!))
                throw new GBrainAuthorizationException("GBrain whoami scopes are invalid.");
        }
        if (!values.SetEquals(["read", "write"]))
            throw new GBrainAuthorizationException("GBrain whoami must report exactly read and write scopes.");
        if (_token.ExpiresAt <= _timeProvider.GetUtcNow().Add(_options.MaximumClockSkew))
            throw new GBrainAuthorizationException("GBrain access token is too close to expiry.");
        var reportedExpiresAt = GBrainHttpJson.RequireInt64(root, "expires_at");
        DateTimeOffset reportedExpiry;
        try
        {
            reportedExpiry = DateTimeOffset.FromUnixTimeSeconds(reportedExpiresAt);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new GBrainAuthorizationException("GBrain whoami expiry is invalid.", exception);
        }
        if ((_token.ExpiresAt - reportedExpiry).Duration() > _options.MaximumClockSkew)
            throw new GBrainAuthorizationException("GBrain whoami expiry does not match the minted token.");
        if (!string.Equals(_token.SourceId, scope.SourceId, StringComparison.Ordinal) ||
            !string.Equals(_token.SoulId, scope.SoulId, StringComparison.Ordinal))
            throw new GBrainAuthorizationException("GBrain token lease scope changed before use.");
    }

    private async Task<JsonDocument> CallToolCoreAsync(
        GBrainMcpTool tool,
        object arguments,
        bool mutating,
        CancellationToken cancellationToken)
    {
        using var response = await PostJsonRpcAsync(
            "tools/call",
            new { name = GBrainMcpToolNames.Get(tool), arguments },
            mutating,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = GBrainHttpJson.RequireObject(
                GBrainHttpJson.RequireProperty(response.RootElement, "result"),
                "MCP tools/call result");
            var content = GBrainHttpJson.RequireProperty(result, "content");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1)
                throw new GBrainProtocolException(
                    "GBrain tool result must contain exactly one text item.");
            var item = GBrainHttpJson.RequireObject(content[0], "MCP content item");
            if (!string.Equals(
                    GBrainHttpJson.RequireString(item, "type", 16),
                    "text",
                    StringComparison.Ordinal))
                throw new GBrainProtocolException("GBrain tool result content must be text.");
            var text = GBrainHttpJson.RequireString(item, "text", _options.MaximumResponseBytes);
            var nested = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
            GBrainHttpJson.EnsureNoDuplicateProperties(nested.RootElement);
            var isError = false;
            if (result.TryGetProperty("isError", out var isErrorElement))
            {
                if (isErrorElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw MutationAware("GBrain tool result isError must be a boolean.", mutating);
                isError = isErrorElement.ValueKind == JsonValueKind.True;
            }
            if (isError)
            {
                var error = GBrainHttpJson.RequireObject(nested.RootElement, "GBrain tool error");
                var errorCode = GBrainHttpJson.RequireString(error, "error", 64);
                if (errorCode.Any(static character => character is < '!' or > '~'))
                    throw new GBrainProtocolException(
                        "GBrain tool error code is not a bounded token.");
                if (!mutating || errorCode is "page_not_found" or "invalid_params" or "permission_denied")
                    throw new GBrainToolCallException(GBrainMcpToolNames.Get(tool), errorCode);
                throw new GBrainUnknownOutcomeException(
                    "Mutating GBrain tool returned an indeterminate error; reconcile by exact read.");
            }
            return nested;
        }
        catch (GBrainUnknownOutcomeException)
        {
            throw;
        }
        catch (GBrainToolCallException)
        {
            throw;
        }
        catch (GBrainProtocolException exception)
        {
            throw MutationAware(exception.Message, mutating, exception);
        }
        catch (JsonException exception)
        {
            throw MutationAware("GBrain tool returned malformed nested JSON.", mutating, exception);
        }
    }

    private async Task<JsonDocument> PostJsonRpcAsync(
        string method,
        object parameters,
        bool mutating,
        CancellationToken cancellationToken)
    {
        if (method is not ("initialize" or "tools/list" or "tools/call"))
            throw new GBrainCapabilityException("Unknown MCP method.");
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _requestId);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });
        if (body.Length > _options.MaximumRequestBytes)
            throw new GBrainProtocolException("MCP request exceeds the configured byte limit.");

        using var request = CreateMcpRequest(HttpMethod.Post, body, includeAuthorization: true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (mutating)
        {
            throw new GBrainUnknownOutcomeException(
                "GBrain MCP mutation was cancelled after dispatch.",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw MutationAware("GBrain MCP request timed out.", mutating, exception);
        }
        catch (HttpRequestException exception)
        {
            throw MutationAware("GBrain MCP transport failed.", mutating, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 403)
                    throw new GBrainAuthorizationException("GBrain MCP request was rejected.");
                throw MutationAware(
                    $"GBrain MCP returned HTTP {(int)response.StatusCode}.",
                    mutating);
            }

            try
            {
                if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
                {
                    var values = sessionValues.ToArray();
                    if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) ||
                        values[0].Length > 256 || values[0].Any(static character => character is < '!' or > '~'))
                        throw MutationAware("MCP session identifier is invalid.", mutating);
                    if (_sessionId is null && method == "initialize")
                        _sessionId = values[0];
                    else if (!string.Equals(_sessionId, values[0], StringComparison.Ordinal))
                        throw MutationAware("MCP session identifier changed unexpectedly.", mutating);
                }
                using var wire = await GBrainHttpJson.ReadDocumentOrSseAsync(
                    response,
                    _options.MaximumResponseBytes,
                    timeout.Token).ConfigureAwait(false);
                var root = GBrainHttpJson.RequireObject(wire.RootElement, "MCP JSON-RPC response");
                if (!string.Equals(GBrainHttpJson.RequireString(root, "jsonrpc", 8), "2.0", StringComparison.Ordinal) ||
                    GBrainHttpJson.RequireInt64(root, "id") != id)
                    throw MutationAware("MCP response correlation failed.", mutating);
                if (root.TryGetProperty("error", out _))
                    throw MutationAware("MCP JSON-RPC returned an error.", mutating);
                _ = GBrainHttpJson.RequireProperty(root, "result");
                return JsonDocument.Parse(root.GetRawText(), new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (GBrainUnknownOutcomeException)
            {
                throw;
            }
            catch (GBrainProtocolException exception)
            {
                throw MutationAware(exception.Message, mutating, exception);
            }
            catch (JsonException exception)
            {
                throw MutationAware("MCP response JSON is invalid.", mutating, exception);
            }
            catch (OperationCanceledException exception) when (mutating)
            {
                throw new GBrainUnknownOutcomeException(
                    "GBrain MCP mutation response was cancelled after dispatch.",
                    exception);
            }
            catch (IOException exception)
            {
                throw MutationAware("GBrain MCP response body failed.", mutating, exception);
            }
            catch (Exception exception) when (mutating && exception is not OutOfMemoryException)
            {
                throw new GBrainUnknownOutcomeException(
                    "GBrain MCP mutation response failed after dispatch.",
                    exception);
            }
        }
    }

    private static GBrainProtocolException MutationAware(
        string message,
        bool mutating,
        Exception? inner = null)
    {
        return mutating
            ? new GBrainUnknownOutcomeException(message, inner)
            : new GBrainProtocolException(message, inner);
    }
}

internal sealed record GBrainToolCapabilityProfile(
    string Name,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlySet<string> Required)
{
    internal void Validate(JsonElement tool)
    {
        var schema = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(tool, "inputSchema"),
            $"{Name} inputSchema");
        if (!string.Equals(GBrainHttpJson.RequireString(schema, "type", 16), "object", StringComparison.Ordinal))
            throw new GBrainCapabilityException($"Pinned GBrain tool {Name} is not an object schema.");
        var properties = GBrainHttpJson.RequireObject(
            GBrainHttpJson.RequireProperty(schema, "properties"),
            $"{Name} properties");
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            var definition = GBrainHttpJson.RequireObject(property.Value, $"{Name}.{property.Name}");
            actual.Add(property.Name, GBrainHttpJson.RequireString(definition, "type", 16));
        }
        if (actual.Count != Properties.Count || Properties.Any(pair =>
                !actual.TryGetValue(pair.Key, out var type) || !string.Equals(type, pair.Value, StringComparison.Ordinal)))
            throw new GBrainCapabilityException($"Pinned GBrain tool {Name} parameter schema changed.");

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array)
                throw new GBrainCapabilityException($"Pinned GBrain tool {Name} required list is invalid.");
            foreach (var value in requiredElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || !required.Add(value.GetString()!))
                    throw new GBrainCapabilityException($"Pinned GBrain tool {Name} required list is invalid.");
            }
        }
        if (!required.SetEquals(Required))
            throw new GBrainCapabilityException($"Pinned GBrain tool {Name} required parameters changed.");
    }
}

internal static class GBrainCapabilityProfile
{
    internal static readonly IReadOnlyList<GBrainToolCapabilityProfile> Required =
    [
        new("get_page", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slug"] = "string", ["fuzzy"] = "boolean", ["include_deleted"] = "boolean"
        }, new HashSet<string>(["slug"], StringComparer.Ordinal)),
        new("put_page", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slug"] = "string", ["content"] = "string", ["source_kind"] = "string",
            ["source_uri"] = "string", ["ingested_via"] = "string"
        }, new HashSet<string>(["slug", "content"], StringComparer.Ordinal)),
        new("delete_page", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slug"] = "string"
        }, new HashSet<string>(["slug"], StringComparer.Ordinal)),
        new("restore_page", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["slug"] = "string"
        }, new HashSet<string>(["slug"], StringComparer.Ordinal)),
        new("search", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = "string", ["limit"] = "number", ["offset"] = "number", ["mode"] = "string"
        }, new HashSet<string>(["query"], StringComparer.Ordinal)),
        new("whoami", new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal))
    ];
}

internal static class GBrainHttpJson
{
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new GBrainProtocolException("GBrain response Content-Type must be application/json.");
        var bytes = await ReadLimitedAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            EnsureNoDuplicateProperties(parsed.RootElement);
            return JsonDocument.Parse(parsed.RootElement.GetRawText(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static async Task<JsonDocument> ReadDocumentOrSseAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new GBrainProtocolException("GBrain MCP response has an unsupported Content-Type.");

        var bytes = await ReadLimitedAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
        byte[]? sseData = null;
        try
        {
            ReadOnlyMemory<byte> json = bytes;
            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                sseData = ExtractSingleSseData(bytes);
                json = sseData;
            }
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            EnsureNoDuplicateProperties(parsed.RootElement);
            return JsonDocument.Parse(parsed.RootElement.GetRawText(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        finally
        {
            if (sseData is not null)
                CryptographicOperations.ZeroMemory(sseData);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GBrainProtocolException($"{name} must be an object.");
        return value;
    }

    internal static JsonElement RequireProperty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new GBrainProtocolException($"Required GBrain field {name} is missing.");
        return value;
    }

    internal static string RequireString(JsonElement root, string name, int maximumLength)
    {
        var value = RequireProperty(root, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new GBrainProtocolException($"GBrain field {name} must be a string.");
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > maximumLength || text.IndexOf('\0') >= 0)
            throw new GBrainProtocolException($"GBrain field {name} is outside its length policy.");
        return text;
    }

    internal static int RequireInt32(JsonElement root, string name, int minimum, int maximum)
    {
        var value = RequireProperty(root, name);
        if (!value.TryGetInt32(out var result) || result < minimum || result > maximum)
            throw new GBrainProtocolException($"GBrain field {name} is outside its numeric policy.");
        return result;
    }

    internal static long RequireInt64(JsonElement root, string name)
    {
        var value = RequireProperty(root, name);
        if (!value.TryGetInt64(out var result))
            throw new GBrainProtocolException($"GBrain field {name} must be an integer.");
        return result;
    }

    internal static void EnsureNoDuplicateProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new GBrainProtocolException("GBrain JSON contains duplicate property names.");
                    EnsureNoDuplicateProperties(property.Value);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    EnsureNoDuplicateProperties(item);
                break;
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw new GBrainProtocolException("GBrain response exceeds the configured byte limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new GBrainProtocolException("GBrain response exceeds the configured byte limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static byte[] ExtractSingleSseData(byte[] body)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GBrainProtocolException("GBrain SSE is not valid UTF-8.", exception);
        }
        string? data = null;
        foreach (var line in text.Split('\n'))
        {
            var normalized = line.TrimEnd('\r');
            if (normalized.Length == 0 || normalized.StartsWith(':'))
                continue;
            if (normalized.StartsWith("event:", StringComparison.Ordinal))
            {
                if (!string.Equals(normalized[6..].Trim(), "message", StringComparison.Ordinal))
                    throw new GBrainProtocolException("GBrain SSE event type is not message.");
                continue;
            }
            if (!normalized.StartsWith("data:", StringComparison.Ordinal))
                throw new GBrainProtocolException("GBrain SSE contains an unsupported field.");
            if (data is not null)
                throw new GBrainProtocolException("GBrain SSE contains multiple data frames.");
            data = normalized[5..].TrimStart();
        }
        if (string.IsNullOrEmpty(data))
            throw new GBrainProtocolException("GBrain SSE contains no JSON data frame.");
        return StrictUtf8.GetBytes(data);
    }
}

internal sealed class GBrainZeroingByteArrayContent : HttpContent
{
    private byte[]? _bytes;

    internal GBrainZeroingByteArrayContent(byte[] bytes)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var bytes = _bytes ?? throw new ObjectDisposedException(nameof(GBrainZeroingByteArrayContent));
        return stream.WriteAsync(bytes).AsTask();
    }

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        var bytes = _bytes ?? throw new ObjectDisposedException(nameof(GBrainZeroingByteArrayContent));
        return stream.WriteAsync(bytes, cancellationToken).AsTask();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _bytes?.Length ?? 0;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (_bytes is not null)
        {
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
        base.Dispose(disposing);
    }
}
