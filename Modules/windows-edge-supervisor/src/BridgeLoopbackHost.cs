using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dps.WindowsEdgeSupervisor;

public sealed record BridgeExchangeV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] string OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("auth_nonce"), JsonRequired] string AuthNonce,
    [property: JsonPropertyName("exchange_kind"), JsonRequired] string ExchangeKind,
    [property: JsonPropertyName("command_id"), JsonRequired] string? CommandId,
    [property: JsonPropertyName("action_kind"), JsonRequired] string? ActionKind,
    [property: JsonPropertyName("step_kind"), JsonRequired] string? StepKind,
    [property: JsonPropertyName("selector"), JsonRequired] string? Selector,
    [property: JsonPropertyName("text"), JsonRequired] string? Text,
    [property: JsonPropertyName("wait_ms"), JsonRequired] int? WaitMs,
    [property: JsonPropertyName("expected_postcondition"), JsonRequired] string? ExpectedPostcondition,
    [property: JsonPropertyName("native_status"), JsonRequired] string? NativeStatus,
    [property: JsonPropertyName("native_detail"), JsonRequired] string? NativeDetail,
    [property: JsonPropertyName("postcondition_verified"), JsonRequired] bool? PostconditionVerified);

public static class BridgeExchangeCodec
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly IReadOnlyDictionary<string, string> AllowedPairs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OBSERVE"] = "OBSERVE_SCREEN",
            ["LOCATE"] = "LOCATE_SELECTOR",
            ["VERIFY"] = "VERIFY_POSTCONDITION",
            ["WAIT"] = "WAIT_DURATION",
            ["TAP"] = "TAP_SELECTOR",
            ["TYPE"] = "TYPE_TEXT"
        };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex CanonicalUtcPattern = new(
        "^(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-5][0-9]:[0-5][0-9](?:\\.[0-9]+)?(?:Z|\\+00:00)\\z",
        RegexOptions.CultureInvariant);

    public static BridgeExchangeV1 Decode(ReadOnlySpan<byte> utf8Json, int maximumBytes = 64 * 1024)
    {
        if (maximumBytes != WindowsHostConfigurationCodec.FixedMaximumRequestBytes ||
            utf8Json.IsEmpty || utf8Json.Length > maximumBytes)
            throw new InvalidDataException("edge bridge exchange wire size is outside the fixed ABI range");
        BridgeExchangeV1 exchange;
        try
        {
            exchange = JsonSerializer.Deserialize<BridgeExchangeV1>(utf8Json, JsonOptions) ??
                throw new InvalidDataException("edge bridge exchange is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("edge bridge exchange JSON is invalid", exception);
        }
        Validate(exchange);
        return exchange;
    }

    public static void Validate(BridgeExchangeV1 exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (exchange.SchemaVersion != "1.0" ||
            exchange.ContractId != "edge.bridge.exchange/v1" ||
            exchange.ProducerModule != "zenno-bridge")
            throw new InvalidDataException("unknown edge bridge exchange contract identity");
        RequirePrefixedHex(exchange.SoulId, "soul_", 64, "soul_id");
        RequirePrefixedHex(exchange.DeviceBindingId, "db_", 32, "device_binding_id");
        RequirePrefixedHex(exchange.PlatformAccountId, "pa_", 32, "platform_account_id");
        RequirePrefixedHex(exchange.TraceId, "trace_", 32, "trace_id");
        RequirePrefixedHex(exchange.IdempotencyKey, "idem_", 64, "idempotency_key");
        if (!CanonicalUtcPattern.IsMatch(exchange.OccurredAt) ||
            !DateTimeOffset.TryParse(
                exchange.OccurredAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAt) || occurredAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("edge bridge occurred_at is not canonical UTC");
        if (exchange.PrivacyClass is not ("internal" or "personal" or "sensitive"))
            throw new InvalidDataException("unknown edge bridge privacy class");
        RequireLowerHex(exchange.AuthNonce, 64, "auth_nonce");

        if (exchange.ExchangeKind == "POLL")
        {
            if (exchange.CommandId is not null || exchange.ActionKind is not null ||
                exchange.StepKind is not null || exchange.Selector is not null ||
                exchange.Text is not null || exchange.WaitMs is not null ||
                exchange.ExpectedPostcondition is not null || exchange.NativeStatus is not null ||
                exchange.NativeDetail is not null || exchange.PostconditionVerified is not null)
                throw new InvalidDataException("POLL cannot carry command or native-result fields");
            return;
        }
        if (exchange.ExchangeKind != "NATIVE_RESULT")
            throw new InvalidDataException("unknown edge bridge exchange kind");

        RequireText(exchange.CommandId, 1, 128, "command_id");
        if (!AllowedPairs.TryGetValue(exchange.ActionKind ?? string.Empty, out var expectedStep) ||
            exchange.StepKind != expectedStep)
            throw new InvalidDataException("unknown or mismatched edge bridge action and step");
        if (exchange.NativeStatus is not ("SUCCESS" or "FAILED" or "UNKNOWN_OUTCOME"))
            throw new InvalidDataException("unknown native result status");
        RequireText(exchange.NativeDetail, 1, 4096, "native_detail");
        RequireOptionalText(exchange.Selector, 2048, "selector");
        RequireOptionalText(exchange.Text, 4096, "text");
        RequireOptionalText(exchange.ExpectedPostcondition, 2048, "expected_postcondition");
        if (exchange.WaitMs is < 0 or > 300000)
            throw new InvalidDataException("wait_ms is outside the edge bridge range");
        if (exchange.ActionKind is "TAP" or "LOCATE" or "VERIFY" &&
            string.IsNullOrWhiteSpace(exchange.Selector))
            throw new InvalidDataException("selector is required for the native result");
        if (exchange.ActionKind == "TYPE" && string.IsNullOrEmpty(exchange.Text))
            throw new InvalidDataException("text is required for the native TYPE result");
        if (exchange.ActionKind == "WAIT" && exchange.WaitMs is null)
            throw new InvalidDataException("wait_ms is required for the native WAIT result");
        if (exchange.NativeStatus == "SUCCESS" && exchange.PostconditionVerified is null)
            throw new InvalidDataException("SUCCESS requires explicit postcondition truth");
        if (exchange.NativeStatus == "UNKNOWN_OUTCOME" && exchange.PostconditionVerified is not null)
            throw new InvalidDataException("UNKNOWN_OUTCOME cannot claim a postcondition");
    }

    public static BridgeDirectiveRequest CreateWait(BridgeExchangeV1 exchange, DateTimeOffset now)
    {
        Validate(exchange);
        if (exchange.ExchangeKind != "POLL")
            throw new InvalidOperationException("only POLL can receive the fail-closed health WAIT directive");
        return new BridgeDirectiveRequest(
            exchange.SoulId,
            exchange.DeviceBindingId,
            exchange.PlatformAccountId,
            exchange.TraceId,
            exchange.IdempotencyKey,
            now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            exchange.PrivacyClass,
            "WAIT",
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static void RequirePrefixedHex(string? value, string prefix, int length, string field)
    {
        if (value is null || value.Length != prefix.Length + length ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException(field + " is not canonical");
        RequireLowerHex(value[prefix.Length..], length, field);
    }

    private static void RequireLowerHex(string? value, int length, string field)
    {
        if (value is null || value.Length != length ||
            !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException(field + " is not canonical lowercase hex");
    }

    private static void RequireOptionalText(string? value, int maximum, string field)
    {
        if (value is not null) RequireText(value, 0, maximum, field);
    }

    private static void RequireText(string? value, int minimum, int maximum, string field)
    {
        if (value is null || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException(field + " length is outside the contract range");
        _ = StrictUtf8.GetByteCount(value);
    }

}

/// <summary>
/// Fixed Windows-only listener for the already shipped Zenno bridge ABI. The
/// POLL exchange doubles as authenticated handshake/health. Until the Worker
/// command/result channel is composed, only a signed WAIT is emitted and every
/// NATIVE_RESULT is rejected with 503; no result is acknowledged speculatively.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BridgeLoopbackHost : IDisposable
{
    private readonly WindowsHostConfiguration _configuration;
    private readonly WindowsCertificateServerIdentity _serverIdentity;
    private readonly AppendOnlyEvidenceLog _evidenceLog;
    private readonly HashSet<string> _allowedClientSids;
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _requestSlots = new(128, 128);
    private bool _disposed;

    public BridgeLoopbackHost(
        WindowsHostConfiguration configuration,
        WindowsCertificateServerIdentity serverIdentity,
        AppendOnlyEvidenceLog evidenceLog)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the Zenno loopback host can run only on Windows");
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serverIdentity = serverIdentity ?? throw new ArgumentNullException(nameof(serverIdentity));
        _evidenceLog = evidenceLog ?? throw new ArgumentNullException(nameof(evidenceLog));
        if (_serverIdentity.KeyId != _configuration.ExpectedServerKeyId)
            throw new InvalidOperationException("server identity does not match the protected host configuration");
        _allowedClientSids = new HashSet<string>(_configuration.AllowedClientSids, StringComparer.Ordinal);
        _listener.AuthenticationSchemes = AuthenticationSchemes.Negotiate;
        _listener.IgnoreWriteExceptions = false;
        _listener.UnsafeConnectionNtlmAuthentication = false;
        _listener.Prefixes.Add("http://127.0.0.1:28741/dps/edge/v1/");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var startPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            _configuration.HostId,
            _configuration.ReleaseBomSha256,
            _configuration.ProtectedPolicySha256,
            server_key_id = _serverIdentity.KeyId,
            endpoint = "http://127.0.0.1:28741/dps/edge/v1/exchange"
        });
        _evidenceLog.Append("host.start.requested", startPayload);
        _listener.Start();
        _evidenceLog.Append("host.bound", startPayload);
        var running = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                try
                {
                    await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SafeAbort(context.Response);
                    break;
                }
                running.Add(ProcessAndReleaseAsync(context, cancellationToken));
                for (var index = running.Count - 1; index >= 0; index--)
                {
                    if (!running[index].IsCompleted) continue;
                    await running[index].ConfigureAwait(false);
                    running.RemoveAt(index);
                }
            }
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(running).ConfigureAwait(false);
            _evidenceLog.Append("host.stop", Encoding.UTF8.GetBytes(_configuration.HostId));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Close();
        _requestSlots.Dispose();
    }

    private async Task ProcessAndReleaseAsync(
        HttpListenerContext context,
        CancellationToken hostCancellation)
    {
        try
        {
            await ProcessAsync(context, hostCancellation).ConfigureAwait(false);
        }
        finally
        {
            _requestSlots.Release();
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, CancellationToken hostCancellation)
    {
        try
        {
            if (!TryGetAuthorizedClientSid(context, out var clientSid))
            {
                await RejectAsync(context.Response, HttpStatusCode.Forbidden).ConfigureAwait(false);
                return;
            }
            var request = context.Request;
            if (request.HttpMethod != "POST" || request.Url is null ||
                request.Url.AbsolutePath != WindowsHostConfigurationCodec.FixedExchangePath ||
                request.Url.Query.Length != 0 ||
                !Equals(request.LocalEndPoint?.Address, IPAddress.Loopback) ||
                request.LocalEndPoint?.Port != WindowsHostConfigurationCodec.FixedPort ||
                request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                !string.Equals(request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                request.ContentLength64 is <= 0 or > WindowsHostConfigurationCodec.FixedMaximumRequestBytes)
            {
                await RejectAsync(context.Response, HttpStatusCode.BadRequest).ConfigureAwait(false);
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
            timeout.CancelAfter(_configuration.RequestTimeoutMs);
            var wire = new byte[checked((int)request.ContentLength64)];
            var offset = 0;
            while (offset < wire.Length)
            {
                var read = await request.InputStream
                    .ReadAsync(wire.AsMemory(offset, wire.Length - offset), timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("edge bridge request ended before its declared length");
                offset += read;
            }
            var exchange = BridgeExchangeCodec.Decode(wire);
            if (exchange.ExchangeKind != "POLL")
            {
                _evidenceLog.Append("bridge.native-result.rejected", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    request_sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(wire)),
                    client_sid = clientSid,
                    reason = "worker-result-channel-not-composed"
                }));
                await RejectAsync(context.Response, HttpStatusCode.ServiceUnavailable).ConfigureAwait(false);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var directive = _serverIdentity.CreateSignedDirective(
                BridgeExchangeCodec.CreateWait(exchange, now),
                exchange.AuthNonce,
                now.ToString("O", CultureInfo.InvariantCulture));
            var responseWire = BridgeDirectiveAuthenticator.Encode(directive);
            _evidenceLog.Append("bridge.poll.wait", JsonSerializer.SerializeToUtf8Bytes(new
            {
                request_sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(wire)),
                response_sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(responseWire)),
                client_sid = clientSid
            }));
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = responseWire.Length;
            context.Response.KeepAlive = true;
            await context.Response.OutputStream.WriteAsync(responseWire, timeout.Token).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (OperationCanceledException)
        {
            SafeAbort(context.Response);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or ArgumentException)
        {
            _evidenceLog.Append("bridge.request.rejected", Encoding.UTF8.GetBytes(exception.GetType().Name));
            await RejectAsync(context.Response, HttpStatusCode.BadRequest).ConfigureAwait(false);
        }
        catch
        {
            SafeAbort(context.Response);
            throw;
        }
    }

    private bool TryGetAuthorizedClientSid(HttpListenerContext context, out string sid)
    {
        sid = string.Empty;
        if (context.User?.Identity is not WindowsIdentity identity || !identity.IsAuthenticated ||
            identity.User is null)
            return false;
        sid = identity.User.Value;
        return _allowedClientSids.Contains(sid);
    }

    private static async Task RejectAsync(HttpListenerResponse response, HttpStatusCode status)
    {
        try
        {
            response.StatusCode = (int)status;
            response.ContentLength64 = 0;
            response.KeepAlive = false;
            await response.OutputStream.FlushAsync().ConfigureAwait(false);
            response.Close();
        }
        catch
        {
            SafeAbort(response);
        }
    }

    private static void SafeAbort(HttpListenerResponse response)
    {
        try { response.Abort(); }
        catch (ObjectDisposedException) { }
    }
}
