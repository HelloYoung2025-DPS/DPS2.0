using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.WindowsEdgeSupervisor;

public sealed record EdgeWorkerCommandRequest(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string CommandId,
    string LeaseId,
    DateTimeOffset LeaseExpiresAt,
    string ActionKind,
    string StepKind,
    string? Selector,
    string? Text,
    int? WaitMs,
    string? ExpectedPostcondition,
    bool Shadow);

public sealed record EdgeWorkerDrainRequest(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string Detail);

public sealed record EdgeWorkerExchangeV1(
    [property: JsonPropertyName("schema_version"), JsonRequired] string SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string PrivacyClass,
    [property: JsonPropertyName("exchange_kind"), JsonRequired] string ExchangeKind,
    [property: JsonPropertyName("command_id"), JsonRequired] string? CommandId,
    [property: JsonPropertyName("lease_id"), JsonRequired] string? LeaseId,
    [property: JsonPropertyName("lease_expires_at"), JsonRequired] DateTimeOffset? LeaseExpiresAt,
    [property: JsonPropertyName("request_sha256"), JsonRequired] string? RequestSha256,
    [property: JsonPropertyName("action_kind"), JsonRequired] string? ActionKind,
    [property: JsonPropertyName("step_kind"), JsonRequired] string? StepKind,
    [property: JsonPropertyName("selector"), JsonRequired] string? Selector,
    [property: JsonPropertyName("text"), JsonRequired] string? Text,
    [property: JsonPropertyName("wait_ms"), JsonRequired] int? WaitMs,
    [property: JsonPropertyName("expected_postcondition"), JsonRequired] string? ExpectedPostcondition,
    [property: JsonPropertyName("shadow"), JsonRequired] bool Shadow,
    [property: JsonPropertyName("dispatch_acknowledged"), JsonRequired] bool? DispatchAcknowledged,
    [property: JsonPropertyName("native_status"), JsonRequired] string? NativeStatus,
    [property: JsonPropertyName("postcondition_verified"), JsonRequired] bool? PostconditionVerified,
    [property: JsonPropertyName("result_status"), JsonRequired] string? ResultStatus,
    [property: JsonPropertyName("duplicate"), JsonRequired] bool? Duplicate,
    [property: JsonPropertyName("retry_allowed"), JsonRequired] bool? RetryAllowed,
    [property: JsonPropertyName("detail"), JsonRequired] string? Detail);

public static class EdgeWorkerExchangeCodec
{
    private const int MaximumWireBytes = 32768;
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static EdgeWorkerExchangeV1 CreateCommand(EdgeWorkerCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = new EdgeWorkerExchangeV1(
            "1.0",
            "edge.worker.exchange/v1",
            "windows-edge-supervisor",
            request.SoulId,
            request.DeviceBindingId,
            request.PlatformAccountId,
            request.TraceId,
            request.IdempotencyKey,
            request.OccurredAt,
            request.PrivacyClass,
            "COMMAND",
            request.CommandId,
            request.LeaseId,
            request.LeaseExpiresAt,
            null,
            request.ActionKind,
            request.StepKind,
            request.Selector,
            request.Text,
            request.WaitMs,
            request.ExpectedPostcondition,
            request.Shadow,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        ValidateCommand(envelope, requireRequestHash: false);
        return envelope with { RequestSha256 = EdgeWorkerRequestHasher.Compute(envelope) };
    }

    public static byte[] EncodeCommand(EdgeWorkerExchangeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateCommand(envelope, requireRequestHash: true);
        var actualHash = EdgeWorkerRequestHasher.Compute(envelope);
        if (!string.Equals(actualHash, envelope.RequestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("request_sha256 does not match the canonical command");
        return Encode(envelope);
    }

    public static EdgeWorkerExchangeV1 CreateDrain(EdgeWorkerDrainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new InvalidDataException(
            "free-text edge.worker.exchange DRAIN is forbidden; use the signed edge.worker.drain.directive/v1 contract");
    }

    public static byte[] EncodeDrain(EdgeWorkerExchangeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        throw new InvalidDataException(
            "free-text edge.worker.exchange DRAIN is forbidden; use the signed edge.worker.drain.directive/v1 contract");
    }

    public static EdgeWorkerExchangeV1 DecodeReceipt(
        ReadOnlySpan<byte> utf8Json,
        EdgeWorkerExchangeV1 sourceCommand)
    {
        ArgumentNullException.ThrowIfNull(sourceCommand);
        ValidateCommand(sourceCommand, requireRequestHash: true);
        if (!string.Equals(
                EdgeWorkerRequestHasher.Compute(sourceCommand),
                sourceCommand.RequestSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("source command request_sha256 is invalid");
        var receipt = DecodeWorkerEnvelope(utf8Json, "RECEIPT");
        if (receipt.SoulId != sourceCommand.SoulId ||
            receipt.DeviceBindingId != sourceCommand.DeviceBindingId ||
            receipt.PlatformAccountId != sourceCommand.PlatformAccountId ||
            receipt.TraceId != sourceCommand.TraceId ||
            receipt.IdempotencyKey != sourceCommand.IdempotencyKey ||
            receipt.PrivacyClass != sourceCommand.PrivacyClass ||
            receipt.CommandId != sourceCommand.CommandId ||
            receipt.RequestSha256 != sourceCommand.RequestSha256)
            throw new InvalidDataException("receipt does not match the exact source command scope and hash");
        return receipt;
    }

    public static EdgeWorkerExchangeV1 DecodeHealth(ReadOnlySpan<byte> utf8Json) =>
        DecodeWorkerEnvelope(utf8Json, "HEALTH");

    public static EdgeWorkerExchangeV1 DecodeReceipt(string json, EdgeWorkerExchangeV1 sourceCommand)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return DecodeReceipt(EdgeWorkerRequestHasher.StrictUtf8.GetBytes(json), sourceCommand);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("edge worker exchange contains invalid Unicode", exception);
        }
    }

    public static EdgeWorkerExchangeV1 DecodeHealth(string json) =>
        DecodeWorkerEnvelope(json, "HEALTH");

    public static void ValidateCommand(EdgeWorkerExchangeV1 envelope, bool requireRequestHash = true)
    {
        ValidateCommon(envelope);
        if (envelope.ProducerModule != "windows-edge-supervisor" || envelope.ExchangeKind != "COMMAND")
            throw new InvalidDataException("unknown edge worker command contract identity");
        RequireLength(envelope.CommandId, 1, 128, "command_id");
        RequireLength(envelope.LeaseId, 1, 128, "lease_id");
        if (envelope.LeaseExpiresAt is null)
            throw new InvalidDataException("lease_expires_at is required");
        if (envelope.LeaseExpiresAt.Value.Offset != TimeSpan.Zero)
            throw new InvalidDataException("lease_expires_at must use an explicit zero UTC offset");
        if (!AllowedPairs.TryGetValue(envelope.ActionKind ?? string.Empty, out var expectedStep) ||
            expectedStep != envelope.StepKind)
            throw new InvalidDataException("unknown or mismatched action and step");
        RequireOptionalLength(envelope.Selector, 2048, "selector");
        RequireOptionalLength(envelope.Text, 4096, "text");
        RequireOptionalLength(envelope.ExpectedPostcondition, 2048, "expected_postcondition");
        if (envelope.WaitMs is < 0 or > 300000)
            throw new InvalidDataException("wait_ms is outside the contract range");
        if (envelope.ActionKind is "TAP" or "LOCATE" or "VERIFY" && string.IsNullOrWhiteSpace(envelope.Selector))
            throw new InvalidDataException("selector is required for the action");
        if (envelope.ActionKind == "TYPE" && string.IsNullOrEmpty(envelope.Text))
            throw new InvalidDataException("text is required for TYPE");
        if (envelope.ActionKind == "WAIT" && envelope.WaitMs is null)
            throw new InvalidDataException("wait_ms is required for WAIT");
        if (envelope.DispatchAcknowledged is not null || envelope.NativeStatus is not null ||
            envelope.PostconditionVerified is not null || envelope.ResultStatus is not null ||
            envelope.Duplicate is not null || envelope.RetryAllowed is not null || envelope.Detail is not null)
            throw new InvalidDataException("COMMAND cannot contain result fields");
        if (requireRequestHash && !IsLowerSha256(envelope.RequestSha256))
            throw new InvalidDataException("request_sha256 is required and must be lowercase SHA-256");
    }

    private static EdgeWorkerExchangeV1 DecodeWorkerEnvelope(ReadOnlySpan<byte> utf8Json, string expectedKind)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumWireBytes)
            throw new InvalidDataException("edge worker exchange wire size is outside the contract range");

        EdgeWorkerExchangeV1 envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EdgeWorkerExchangeV1>(utf8Json, JsonOptions) ??
                throw new InvalidDataException("edge worker exchange payload is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("edge worker exchange JSON is invalid", exception);
        }

        if (envelope.ExchangeKind != expectedKind)
            throw new InvalidDataException($"expected {expectedKind} edge worker exchange");
        if (expectedKind == "RECEIPT") ValidateReceipt(envelope);
        else ValidateHealth(envelope);
        return envelope;
    }

    private static EdgeWorkerExchangeV1 DecodeWorkerEnvelope(string json, string expectedKind)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return DecodeWorkerEnvelope(EdgeWorkerRequestHasher.StrictUtf8.GetBytes(json), expectedKind);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("edge worker exchange contains invalid Unicode", exception);
        }
    }

    private static void ValidateReceipt(EdgeWorkerExchangeV1 envelope)
    {
        ValidateCommon(envelope);
        if (envelope.ProducerModule != "windows-edge-worker" || envelope.ExchangeKind != "RECEIPT")
            throw new InvalidDataException("unknown edge worker receipt contract identity");
        RequireLength(envelope.CommandId, 1, 128, "command_id");
        if (!IsLowerSha256(envelope.RequestSha256))
            throw new InvalidDataException("receipt must carry the original command request_sha256");
        RequireCommandPayloadNull(envelope, includeCommandIdAndHash: false);
        if (envelope.Shadow || envelope.Duplicate is null || envelope.RetryAllowed is null)
            throw new InvalidDataException("receipt shadow, duplicate, or retry fields are invalid");
        RequireLength(envelope.Detail, 1, 4096, "detail");
        ValidateReceiptTruth(envelope);
    }

    private static void ValidateReceiptTruth(EdgeWorkerExchangeV1 envelope)
    {
        var valid = envelope.ResultStatus switch
        {
            "VERIFIED_SUCCESS" =>
                envelope.DispatchAcknowledged == true && envelope.NativeStatus == "SUCCESS" &&
                envelope.PostconditionVerified == true && envelope.RetryAllowed == false,
            "UNKNOWN_OUTCOME" =>
                envelope.DispatchAcknowledged != false && envelope.NativeStatus == "UNKNOWN_OUTCOME" &&
                envelope.PostconditionVerified is null && envelope.RetryAllowed == false,
            "FAILED" =>
                envelope.DispatchAcknowledged is not null && envelope.RetryAllowed == false &&
                (envelope.DispatchAcknowledged == false
                    ? envelope.NativeStatus == "FAILED" && envelope.PostconditionVerified == false
                    : envelope.NativeStatus == "SUCCESS"
                        ? envelope.PostconditionVerified == false
                        : envelope.NativeStatus == "FAILED" && envelope.PostconditionVerified is not null),
            "REJECTED" or "QUARANTINED" =>
                envelope.DispatchAcknowledged == false && envelope.NativeStatus is null &&
                envelope.PostconditionVerified is null && envelope.Duplicate == false &&
                envelope.RetryAllowed == false,
            "IN_PROGRESS" =>
                envelope.DispatchAcknowledged == false && envelope.NativeStatus is null &&
                envelope.PostconditionVerified is null && envelope.Duplicate == true &&
                envelope.RetryAllowed == true,
            "SHADOWED" =>
                envelope.DispatchAcknowledged == false && envelope.NativeStatus is null &&
                envelope.PostconditionVerified is null && envelope.RetryAllowed == false,
            _ => false
        };
        if (!valid) throw new InvalidDataException("receipt result fields are internally inconsistent");
    }

    private static void ValidateHealth(EdgeWorkerExchangeV1 envelope)
    {
        ValidateCommon(envelope);
        if (envelope.ProducerModule != "windows-edge-worker" || envelope.ExchangeKind != "HEALTH")
            throw new InvalidDataException("unknown edge worker health contract identity");
        RequireCommandPayloadNull(envelope, includeCommandIdAndHash: true);
        if (envelope.Shadow || envelope.DispatchAcknowledged is not null || envelope.NativeStatus is not null ||
            envelope.PostconditionVerified is not null || envelope.ResultStatus != "HEALTHY" ||
            envelope.Duplicate is not null || envelope.RetryAllowed is not null)
            throw new InvalidDataException("health result fields are invalid");
        RequireLength(envelope.Detail, 1, 4096, "detail");
    }

    private static void ValidateCommon(EdgeWorkerExchangeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != "1.0" || envelope.ContractId != "edge.worker.exchange/v1")
            throw new InvalidDataException("unknown edge worker exchange contract identity");
        if (!CanonicalIds.IsSoul(envelope.SoulId) ||
            !CanonicalIds.IsPrefixedLowerHex(envelope.DeviceBindingId, "db_", 32) ||
            !CanonicalIds.IsPrefixedLowerHex(envelope.PlatformAccountId, "pa_", 32))
            throw new InvalidDataException("invalid canonical identity scope");
        if (!CanonicalIds.IsPrefixedLowerHex(envelope.TraceId, "trace_", 32) ||
            !CanonicalIds.IsPrefixedLowerHex(envelope.IdempotencyKey, "idem_", 64))
            throw new InvalidDataException("trace_id or idempotency_key is not canonical");
        if (envelope.OccurredAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("occurred_at must use an explicit zero UTC offset");
        if (envelope.PrivacyClass is not ("internal" or "personal" or "sensitive"))
            throw new InvalidDataException("unknown privacy_class");
    }

    private static void RequireCommandPayloadNull(EdgeWorkerExchangeV1 envelope, bool includeCommandIdAndHash)
    {
        if ((includeCommandIdAndHash && (envelope.CommandId is not null || envelope.RequestSha256 is not null)) ||
            envelope.LeaseId is not null || envelope.LeaseExpiresAt is not null ||
            envelope.ActionKind is not null || envelope.StepKind is not null || envelope.Selector is not null ||
            envelope.Text is not null || envelope.WaitMs is not null || envelope.ExpectedPostcondition is not null)
            throw new InvalidDataException("exchange contains command-only fields");
    }

    private static byte[] Encode(EdgeWorkerExchangeV1 envelope)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (payload.Length > MaximumWireBytes)
            throw new InvalidDataException("edge worker exchange exceeds the wire-size limit");
        return payload;
    }

    private static void RequireLength(string? value, int minimum, int maximum, string field)
    {
        if (value is null || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException($"{field} length is outside the contract range");
        EnsureStrictUtf8(value, field);
    }

    private static void RequireOptionalLength(string? value, int maximum, string field)
    {
        if (value is null) return;
        if (value.Length > maximum)
            throw new InvalidDataException($"{field} length is outside the contract range");
        EnsureStrictUtf8(value, field);
    }

    private static void EnsureStrictUtf8(string value, string field)
    {
        try
        {
            _ = EdgeWorkerRequestHasher.StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"{field} contains invalid Unicode", exception);
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static class CanonicalIds
    {
        public static bool IsSoul(string? value) =>
            value is { Length: 69 } && value.StartsWith("soul_", StringComparison.Ordinal) &&
            value.AsSpan(5).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

        public static bool IsPrefixedLowerHex(string? value, string prefix, int bodyLength) =>
            value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Length == prefix.Length + bodyLength &&
            value.AsSpan(prefix.Length).ToString().All(
                character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

public static class EdgeWorkerRequestHasher
{
    private const string Domain = "dps.windows-edge-worker.command-request-sha256/v1";
    internal static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Compute(EdgeWorkerExchangeV1 command)
    {
        var canonical = CanonicalizeCommand(command);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(canonical));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    public static byte[] CanonicalizeCommand(EdgeWorkerExchangeV1 command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var leaseId = command.LeaseId ?? throw new InvalidDataException("lease_id is required for request hashing");
        var leaseExpiresAt = command.LeaseExpiresAt ?? throw new InvalidDataException("lease_expires_at is required for request hashing");
        var commandId = command.CommandId ?? throw new InvalidDataException("command_id is required for request hashing");
        var actionKind = command.ActionKind ?? throw new InvalidDataException("action_kind is required for request hashing");
        var stepKind = command.StepKind ?? throw new InvalidDataException("step_kind is required for request hashing");
        if (command.OccurredAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("occurred_at must use an explicit zero UTC offset for request hashing");

        string?[] fields =
        [
            command.ContractId,
            command.ProducerModule,
            command.SoulId,
            command.DeviceBindingId,
            command.PlatformAccountId,
            commandId,
            command.TraceId,
            command.IdempotencyKey,
            command.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            command.PrivacyClass,
            leaseId,
            leaseExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            actionKind,
            stepKind,
            command.Selector,
            command.Text,
            command.WaitMs?.ToString(CultureInfo.InvariantCulture),
            command.ExpectedPostcondition,
            command.Shadow ? "1" : "0"
        ];
        return Encode(fields);
    }

    private static byte[] Encode(IReadOnlyList<string?> fields)
    {
        var domainBytes = StrictUtf8.GetBytes(Domain);
        var encodedFields = new byte[]?[fields.Count];
        try
        {
            var outputLength = checked(sizeof(uint) + domainBytes.Length + sizeof(uint));
            for (var index = 0; index < fields.Count; index++)
            {
                outputLength = checked(outputLength + sizeof(byte));
                if (fields[index] is not { } field) continue;
                encodedFields[index] = StrictUtf8.GetBytes(field);
                outputLength = checked(outputLength + sizeof(uint) + encodedFields[index]!.Length);
            }

            var output = GC.AllocateUninitializedArray<byte>(outputLength);
            var offset = 0;
            WriteLengthPrefixed(output, ref offset, domainBytes);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset, sizeof(uint)), checked((uint)fields.Count));
            offset += sizeof(uint);
            foreach (var field in encodedFields)
            {
                if (field is null)
                {
                    output[offset++] = 0;
                    continue;
                }

                output[offset++] = 1;
                WriteLengthPrefixed(output, ref offset, field);
            }
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainBytes);
            foreach (var field in encodedFields)
                if (field is not null) CryptographicOperations.ZeroMemory(field);
        }
    }

    private static void WriteLengthPrefixed(byte[] destination, ref int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), checked((uint)value.Length));
        offset += sizeof(uint);
        value.AsSpan().CopyTo(destination.AsSpan(offset, value.Length));
        offset += value.Length;
    }
}
