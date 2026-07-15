using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.WindowsEdgeWorker;

public sealed record WorkerCommand(
    [property: JsonPropertyName("schema_version"), JsonRequired] string? SchemaVersion,
    [property: JsonPropertyName("contract_id"), JsonRequired] string? ContractId,
    [property: JsonPropertyName("producer_module"), JsonRequired] string? ProducerModule,
    [property: JsonPropertyName("soul_id"), JsonRequired] string? SoulId,
    [property: JsonPropertyName("device_binding_id"), JsonRequired] string? DeviceBindingId,
    [property: JsonPropertyName("platform_account_id"), JsonRequired] string? PlatformAccountId,
    [property: JsonPropertyName("trace_id"), JsonRequired] string? TraceId,
    [property: JsonPropertyName("idempotency_key"), JsonRequired] string? IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonRequired] DateTimeOffset? OccurredAt,
    [property: JsonPropertyName("privacy_class"), JsonRequired] string? PrivacyClass,
    [property: JsonPropertyName("exchange_kind"), JsonRequired] string? ExchangeKind,
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
    [property: JsonPropertyName("shadow"), JsonRequired] bool? Shadow,
    [property: JsonPropertyName("dispatch_acknowledged"), JsonRequired] bool? DispatchAcknowledged,
    [property: JsonPropertyName("native_status"), JsonRequired] string? NativeStatusValue,
    [property: JsonPropertyName("postcondition_verified"), JsonRequired] bool? PostconditionVerified,
    [property: JsonPropertyName("result_status"), JsonRequired] string? ResultStatus,
    [property: JsonPropertyName("duplicate"), JsonRequired] bool? Duplicate,
    [property: JsonPropertyName("retry_allowed"), JsonRequired] bool? RetryAllowed,
    [property: JsonPropertyName("detail"), JsonRequired] string? Detail);

public sealed record WorkerHealthReport(
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string Detail);

public static class WorkerExchangeCodec
{
    private const int MaximumWireBytes = 32768;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
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

    public static WorkerCommand DecodeCommand(ReadOnlySpan<byte> utf8Json)
    {
        var command = DecodeEnvelope(utf8Json);
        var validation = WorkerCommandValidator.GetCommandError(command, now: null);
        if (validation is not null) throw new InvalidDataException(validation);
        var actualHash = CommandHasher.Compute(command);
        if (!string.Equals(actualHash, command.RequestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("request_sha256 mismatch");
        return command;
    }

    public static WorkerCommand DecodeCommand(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return DecodeCommand(StrictUtf8.GetBytes(json));
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("edge worker exchange contains invalid Unicode", exception);
        }
    }

    public static byte[] EncodeReceipt(
        WorkerCommand sourceCommand,
        CommandReceipt receipt,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(sourceCommand);
        ArgumentNullException.ThrowIfNull(receipt);
        var sourceError = WorkerCommandValidator.GetReceiptSourceError(sourceCommand);
        if (sourceError is not null) throw new InvalidDataException(sourceError);
        if (receipt.CommandId != sourceCommand.CommandId || receipt.IdempotencyKey != sourceCommand.IdempotencyKey)
            throw new InvalidDataException("receipt identity does not match the source command");

        var envelope = new WorkerCommand(
            "1.0",
            "edge.worker.exchange/v1",
            "windows-edge-worker",
            sourceCommand.SoulId,
            sourceCommand.DeviceBindingId,
            sourceCommand.PlatformAccountId,
            sourceCommand.TraceId,
            sourceCommand.IdempotencyKey,
            occurredAt,
            sourceCommand.PrivacyClass,
            "RECEIPT",
            sourceCommand.CommandId,
            null,
            null,
            sourceCommand.RequestSha256,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            receipt.DispatchAcknowledged,
            ToWireNativeStatus(receipt.NativeStatus),
            receipt.PostconditionVerified,
            receipt.ResultStatus,
            receipt.Duplicate,
            receipt.RetryAllowed,
            receipt.Detail);
        var validation = WorkerCommandValidator.GetReceiptError(envelope);
        if (validation is not null) throw new InvalidDataException(validation);
        return Encode(envelope);
    }

    public static byte[] EncodeHealth(WorkerHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var envelope = new WorkerCommand(
            "1.0",
            "edge.worker.exchange/v1",
            "windows-edge-worker",
            report.SoulId,
            report.DeviceBindingId,
            report.PlatformAccountId,
            report.TraceId,
            report.IdempotencyKey,
            report.OccurredAt,
            report.PrivacyClass,
            "HEALTH",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            "HEALTHY",
            null,
            null,
            report.Detail);
        var validation = WorkerCommandValidator.GetHealthError(envelope);
        if (validation is not null) throw new InvalidDataException(validation);
        return Encode(envelope);
    }

    private static WorkerCommand DecodeEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumWireBytes)
            throw new InvalidDataException("edge worker exchange wire size is outside the contract range");

        try
        {
            return JsonSerializer.Deserialize<WorkerCommand>(utf8Json, JsonOptions) ??
                throw new InvalidDataException("edge worker exchange payload is null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("edge worker exchange JSON is invalid", exception);
        }
    }

    private static byte[] Encode(WorkerCommand envelope)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (payload.Length > MaximumWireBytes)
            throw new InvalidDataException("edge worker exchange exceeds the wire-size limit");
        return payload;
    }

    private static string? ToWireNativeStatus(NativeStatus? status) => status switch
    {
        NativeStatus.Success => "SUCCESS",
        NativeStatus.Failed => "FAILED",
        NativeStatus.UnknownOutcome => "UNKNOWN_OUTCOME",
        null => null,
        _ => throw new InvalidDataException("unknown native status")
    };
}

internal static class WorkerCommandValidator
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

    public static string? GetCommandError(WorkerCommand command, DateTimeOffset? now)
    {
        var common = GetCommonError(command);
        if (common is not null) return common;
        if (command.ProducerModule != "windows-edge-supervisor" || command.ExchangeKind != "COMMAND")
            return "unknown edge worker command contract identity";
        if (!JournalIdentifiers.IsAsciiToken(command.CommandId, 128) ||
            !HasLength(command.LeaseId, 1, 128))
            return "command or lease identifier length is invalid";
        if (command.LeaseExpiresAt is null || command.Shadow is null)
            return "lease_expires_at and shadow are required";
        if (command.LeaseExpiresAt.Value.Offset != TimeSpan.Zero)
            return "lease_expires_at must use an explicit zero UTC offset";
        if (now is not null && command.LeaseExpiresAt <= now) return "lease is expired";
        if (!IsLowerSha256(command.RequestSha256)) return "request_sha256 must be lowercase SHA-256";
        if (!AllowedPairs.TryGetValue(command.ActionKind ?? string.Empty, out var step) || step != command.StepKind)
            return "unknown or mismatched action and step";
        if (!HasOptionalMaximum(command.Selector, 2048) || !HasOptionalMaximum(command.Text, 4096) ||
            !HasOptionalMaximum(command.ExpectedPostcondition, 2048))
            return "selector, text, or expected_postcondition length is invalid";
        if (command.WaitMs is < 0 or > 300000) return "wait_ms is outside the contract range";
        if (command.ActionKind is "TAP" or "LOCATE" or "VERIFY" && string.IsNullOrWhiteSpace(command.Selector))
            return "selector is required";
        if (command.ActionKind == "TYPE" && string.IsNullOrEmpty(command.Text)) return "text is required";
        if (command.ActionKind == "WAIT" && command.WaitMs is null) return "wait_ms is required";
        if (command.DispatchAcknowledged is not null || command.NativeStatusValue is not null ||
            command.PostconditionVerified is not null || command.ResultStatus is not null ||
            command.Duplicate is not null || command.RetryAllowed is not null || command.Detail is not null)
            return "COMMAND cannot contain result fields";
        return null;
    }

    public static string? GetReceiptSourceError(WorkerCommand command)
    {
        var common = GetCommonError(command);
        if (common is not null) return common;
        if (command.ProducerModule != "windows-edge-supervisor" || command.ExchangeKind != "COMMAND")
            return "receipt source is not a supervisor COMMAND";
        if (!JournalIdentifiers.IsAsciiToken(command.CommandId, 128) ||
            !IsLowerSha256(command.RequestSha256))
            return "receipt source command_id or request_sha256 is invalid";
        return null;
    }

    public static string? GetReceiptError(WorkerCommand envelope)
    {
        var common = GetCommonError(envelope);
        if (common is not null) return common;
        if (envelope.ProducerModule != "windows-edge-worker" || envelope.ExchangeKind != "RECEIPT")
            return "unknown edge worker receipt contract identity";
        if (!JournalIdentifiers.IsAsciiToken(envelope.CommandId, 128) ||
            !IsLowerSha256(envelope.RequestSha256))
            return "receipt command_id or original request_sha256 is invalid";
        if (!CommandPayloadIsNull(envelope, includeCommandIdAndHash: false))
            return "RECEIPT contains command-only fields";
        if (envelope.Shadow != false || envelope.Duplicate is null || envelope.RetryAllowed is null ||
            !HasLength(envelope.Detail, 1, 4096))
            return "receipt shadow, duplicate, retry, or detail fields are invalid";
        return ReceiptTruthIsValid(envelope) ? null : "receipt result fields are internally inconsistent";
    }

    public static string? GetHealthError(WorkerCommand envelope)
    {
        var common = GetCommonError(envelope);
        if (common is not null) return common;
        if (envelope.ProducerModule != "windows-edge-worker" || envelope.ExchangeKind != "HEALTH")
            return "unknown edge worker health contract identity";
        if (!CommandPayloadIsNull(envelope, includeCommandIdAndHash: true))
            return "HEALTH contains command-only fields";
        if (envelope.Shadow != false || envelope.DispatchAcknowledged is not null ||
            envelope.NativeStatusValue is not null || envelope.PostconditionVerified is not null ||
            envelope.ResultStatus != "HEALTHY" || envelope.Duplicate is not null || envelope.RetryAllowed is not null)
            return "HEALTH result fields are invalid";
        return HasLength(envelope.Detail, 1, 4096) ? null : "HEALTH detail length is invalid";
    }

    private static string? GetCommonError(WorkerCommand command)
    {
        if (command.SchemaVersion != "1.0" || command.ContractId != "edge.worker.exchange/v1")
            return "unknown edge worker exchange contract identity";
        if (!CanonicalIds.IsSoul(command.SoulId) || !CanonicalIds.IsDeviceBinding(command.DeviceBindingId) ||
            !CanonicalIds.IsPlatformAccount(command.PlatformAccountId))
            return "invalid canonical identity scope";
        if (!CanonicalIds.IsTrace(command.TraceId) || !CanonicalIds.IsIdempotency(command.IdempotencyKey))
            return "trace or idempotency identifier is not canonical";
        if (command.OccurredAt is null) return "occurred_at is required";
        if (command.OccurredAt.Value.Offset != TimeSpan.Zero)
            return "occurred_at must use an explicit zero UTC offset";
        if (command.PrivacyClass is not ("internal" or "personal" or "sensitive"))
            return "unknown privacy_class";
        return AllStringsAreStrictUtf8(command) ? null : "exchange contains invalid Unicode";
    }

    private static bool ReceiptTruthIsValid(WorkerCommand envelope) => envelope.ResultStatus switch
    {
        "VERIFIED_SUCCESS" =>
            envelope.DispatchAcknowledged == true && envelope.NativeStatusValue == "SUCCESS" &&
            envelope.PostconditionVerified == true && envelope.RetryAllowed == false,
        "UNKNOWN_OUTCOME" =>
            envelope.DispatchAcknowledged != false && envelope.NativeStatusValue == "UNKNOWN_OUTCOME" &&
            envelope.PostconditionVerified is null && envelope.RetryAllowed == false,
        "FAILED" =>
            envelope.DispatchAcknowledged is not null && envelope.RetryAllowed == false &&
            (envelope.DispatchAcknowledged == false
                ? envelope.NativeStatusValue == "FAILED" && envelope.PostconditionVerified == false
                : envelope.NativeStatusValue == "SUCCESS"
                    ? envelope.PostconditionVerified == false
                    : envelope.NativeStatusValue == "FAILED" && envelope.PostconditionVerified is not null),
        "REJECTED" or "QUARANTINED" =>
            envelope.DispatchAcknowledged == false && envelope.NativeStatusValue is null &&
            envelope.PostconditionVerified is null && envelope.Duplicate == false &&
            envelope.RetryAllowed == false,
        "IN_PROGRESS" =>
            envelope.DispatchAcknowledged == false && envelope.NativeStatusValue is null &&
            envelope.PostconditionVerified is null && envelope.Duplicate == true &&
            envelope.RetryAllowed == true,
        "SHADOWED" =>
            envelope.DispatchAcknowledged == false && envelope.NativeStatusValue is null &&
            envelope.PostconditionVerified is null && envelope.RetryAllowed == false,
        _ => false
    };

    private static bool CommandPayloadIsNull(WorkerCommand envelope, bool includeCommandIdAndHash) =>
        (!includeCommandIdAndHash || (envelope.CommandId is null && envelope.RequestSha256 is null)) &&
        envelope.LeaseId is null && envelope.LeaseExpiresAt is null && envelope.ActionKind is null &&
        envelope.StepKind is null && envelope.Selector is null && envelope.Text is null &&
        envelope.WaitMs is null && envelope.ExpectedPostcondition is null;

    private static bool AllStringsAreStrictUtf8(WorkerCommand command)
    {
        string?[] fields =
        [
            command.SchemaVersion, command.ContractId, command.ProducerModule, command.SoulId,
            command.DeviceBindingId, command.PlatformAccountId, command.TraceId, command.IdempotencyKey,
            command.PrivacyClass, command.ExchangeKind, command.CommandId, command.LeaseId,
            command.RequestSha256, command.ActionKind, command.StepKind, command.Selector, command.Text,
            command.ExpectedPostcondition, command.NativeStatusValue, command.ResultStatus, command.Detail
        ];
        try
        {
            foreach (var field in fields)
                if (field is not null) _ = StrictUtf8.GetByteCount(field);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasLength(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum;

    private static bool HasOptionalMaximum(string? value, int maximum) => value is null || value.Length <= maximum;

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
