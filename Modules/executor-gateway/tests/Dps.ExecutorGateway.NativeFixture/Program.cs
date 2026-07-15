using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

return await FixtureHost.RunAsync(args);

internal static class FixtureHost
{
    private const string RequestSchema = "dps.native-fixture.request/v1";
    private const string ResponseSchema = "dps.native-fixture.response/v1";
    private const string EvidenceKind = "REAL_LOCAL_PROCESS";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--state-file", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[1]))
            return 64;

        var store = new FixtureStateStore(Path.GetFullPath(args[1]), Json);
        var state = await store.LoadAsync();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            FixtureWireResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<FixtureWireRequest>(line, Json)
                    ?? throw new InvalidDataException("Request is empty.");
                response = await HandleAsync(request, state, store);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                response = FixtureWireResponse.Error("unknown", "INVALID_REQUEST");
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, Json));
            await Console.Out.FlushAsync();
        }

        return 0;
    }

    private static async Task<FixtureWireResponse> HandleAsync(FixtureWireRequest request, FixtureState state, FixtureStateStore store)
    {
        RequireExact(request.SchemaVersion, RequestSchema, nameof(request.SchemaVersion));
        if (request.RequestId == Guid.Empty) throw new InvalidDataException("request_id is required.");

        switch (request.Operation)
        {
            case "hello":
                return FixtureWireResponse.Ok(request.RequestId, activeBinding: state.ActiveBinding.ToWire(), sideEffectCount: state.SideEffectCount);
            case "read_active":
                return FixtureWireResponse.Ok(request.RequestId, activeBinding: state.ActiveBinding.ToWire());
            case "read_state":
                return FixtureWireResponse.Ok(request.RequestId, activeBinding: state.ActiveBinding.ToWire(), sideEffectCount: state.SideEffectCount);
            case "set_mode":
                state.Mode = request.Mode switch
                {
                    "none" or "crash_before_flush" or "crash_after_flush" or "old_attempt_result" or "cross_scope_result" or "switch_bom_after_effect" => request.Mode,
                    _ => throw new InvalidDataException("Unknown fixture mode.")
                };
                await store.SaveAsync(state);
                return FixtureWireResponse.Ok(request.RequestId, activeBinding: state.ActiveBinding.ToWire(), sideEffectCount: state.SideEffectCount);
            case "submit":
                return await SubmitAsync(request, state, store);
            case "complete":
                return Complete(request, state);
            default:
                throw new InvalidDataException("Unknown fixture operation.");
        }
    }

    private static async Task<FixtureWireResponse> SubmitAsync(FixtureWireRequest request, FixtureState state, FixtureStateStore store)
    {
        var execution = request.Execution ?? throw new InvalidDataException("Execution payload is required.");
        ValidateExecution(execution, state.ActiveBinding);
        var executionKey = $"{execution.CommandId:N}:{execution.LeaseId:N}:{execution.Attempt}";
        var submittedRequestDigest = NativeSubmissionWireProtocol.ComputeSubmittedRequestSha256(execution);
        if (state.Executions.TryGetValue(executionKey, out var stored))
        {
            if (!FixedDigestEquals(stored.SubmittedRequestSha256, submittedRequestDigest))
                return FixtureWireResponse.Error(request.RequestId.ToString("D"), "IDEMPOTENCY_CONFLICT");
            ValidateStoredAcknowledgement(execution, submittedRequestDigest, stored.Acknowledgement);
            return FixtureWireResponse.Ok(request.RequestId, submissionAck: stored.Acknowledgement, sideEffectCount: state.SideEffectCount);
        }

        var mode = state.Mode;
        if (string.Equals(mode, "crash_before_flush", StringComparison.Ordinal))
        {
            state.Mode = "none";
            await store.SaveAsync(state);
            Environment.Exit(72);
        }

        if (execution.Command.IsSideEffect) state.SideEffectCount++;
        var result = CreateResult(execution, state.SideEffectCount);
        result = state.Mode switch
        {
            "old_attempt_result" => result with { Attempt = execution.Attempt == 1 ? 2 : execution.Attempt - 1 },
            "cross_scope_result" => result with { SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            _ => result
        };
        state.Mode = "none";
        var unsignedAcknowledgement = new WireSubmissionAck(
            "1.0.0",
            "native.submission.ack/v1",
            "windows-edge-worker",
            Guid.NewGuid(),
            Guid.NewGuid(),
            execution.CommandId,
            execution.LeaseId,
            execution.Attempt,
            execution.SoulId,
            execution.DeviceBindingId,
            execution.PlatformAccountId,
            execution.TraceId,
            execution.IdempotencyKey,
            execution.Command.OccurredAt.AddSeconds(1),
            "internal",
            "REQUEST_AND_STATE_FLUSHED",
            execution.CommandSha256,
            execution.AuthorizationSha256,
            execution.SubmissionAttemptId,
            execution.SubmissionIntentSha256,
            execution.PendingStateSha256,
            execution.ActiveReleaseBomSha256,
            execution.ActiveReleaseBomGeneration,
            execution.ActiveReleaseBomTokenSha256,
            submittedRequestDigest,
            new string('0', 64));
        var acknowledgement = unsignedAcknowledgement with
        {
            AcknowledgementSha256 = NativeSubmissionWireProtocol.ComputeAcknowledgementSha256(unsignedAcknowledgement)
        };
        state.Executions[executionKey] = new StoredExecution(submittedRequestDigest, acknowledgement, result);
        if (string.Equals(mode, "switch_bom_after_effect", StringComparison.Ordinal))
        {
            state.ActiveBinding = new FixtureActiveBinding(
                state.ActiveBinding.SchemaVersion,
                state.ActiveBinding.DeviceBindingId,
                new string('e', 64),
                checked(state.ActiveBinding.Generation + 1),
                0x43);
        }
        await store.SaveAsync(state);

        if (string.Equals(mode, "crash_after_flush", StringComparison.Ordinal))
            Environment.Exit(73);

        // An acknowledgement may cross the process boundary only after the exact stored request,
        // acknowledgement, result, and side-effect state have been flushed and read back.
        var durableState = await store.LoadAsync();
        if (!durableState.Executions.TryGetValue(executionKey, out var durableExecution) ||
            durableState.SideEffectCount != state.SideEffectCount ||
            !FixedDigestEquals(durableExecution.SubmittedRequestSha256, submittedRequestDigest))
            throw new InvalidDataException("Durable submission state read-back failed.");
        ValidateStoredAcknowledgement(execution, submittedRequestDigest, durableExecution.Acknowledgement);
        return FixtureWireResponse.Ok(request.RequestId, submissionAck: durableExecution.Acknowledgement, sideEffectCount: durableState.SideEffectCount);
    }

    private static FixtureWireResponse Complete(FixtureWireRequest request, FixtureState state)
    {
        var completionHandleId = request.CompletionHandleId ?? throw new InvalidDataException("Completion handle is required.");
        if (completionHandleId == Guid.Empty) throw new InvalidDataException("Completion handle cannot be empty.");
        var stored = state.Executions.Values.SingleOrDefault(value => value.Acknowledgement.CompletionHandleId == completionHandleId)
            ?? throw new InvalidDataException("Unknown completion handle.");
        return FixtureWireResponse.Ok(request.RequestId, nativeResult: stored.Result, sideEffectCount: state.SideEffectCount);
    }

    private static WireNativeResult CreateResult(WireExecution execution, int sideEffectCount)
    {
        var step = execution.Command.Steps.Single();
        return new WireNativeResult(
            Guid.NewGuid(), execution.CommandId, execution.LeaseId, execution.Attempt,
            execution.SoulId, execution.DeviceBindingId, execution.PlatformAccountId,
            execution.TraceId, execution.IdempotencyKey, execution.Command.OccurredAt.AddSeconds(1),
            execution.ActiveReleaseBomSha256, execution.ActiveReleaseBomGeneration, execution.ActiveReleaseBomTokenSha256,
            [new WireNativeStepResult(step.StepId, step.StepKind, "SUCCESS", "FIXTURE_OK", Sha256(Encoding.UTF8.GetBytes($"{execution.CommandId:N}:{step.StepId:N}:{sideEffectCount}"))) ]);
    }

    private static void ValidateExecution(WireExecution execution, FixtureActiveBinding active)
    {
        if (execution.CommandId == Guid.Empty || execution.LeaseId == Guid.Empty || execution.Attempt is < 1 or > 3)
            throw new InvalidDataException("Invalid execution identity.");
        RequireOpaque(execution.SoulId, "soul_", 64, nameof(execution.SoulId));
        RequireOpaque(execution.DeviceBindingId, "db_", 32, nameof(execution.DeviceBindingId));
        RequireOpaque(execution.PlatformAccountId, "pa_", 32, nameof(execution.PlatformAccountId));
        RequireOpaque(execution.TraceId, "trace_", 32, nameof(execution.TraceId));
        RequireOpaque(execution.IdempotencyKey, "idem_", 64, nameof(execution.IdempotencyKey));
        RequireOpaque(active.DeviceBindingId, "db_", 32, nameof(active.DeviceBindingId));
        if (execution.Command.CommandId != execution.CommandId || execution.Command.LeaseId != execution.LeaseId || execution.Command.Attempt != execution.Attempt ||
            !string.Equals(execution.Command.SoulId, execution.SoulId, StringComparison.Ordinal) ||
            !string.Equals(execution.Command.DeviceBindingId, execution.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(execution.Command.PlatformAccountId, execution.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(execution.Command.TraceId, execution.TraceId, StringComparison.Ordinal) ||
            !string.Equals(execution.Command.IdempotencyKey, execution.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidDataException("Execution scope differs from command scope.");
        if (execution.Command.Steps.Count != 1 || execution.Command.Steps[0].StepId != execution.StepId ||
            !string.Equals(execution.Command.Steps[0].StepKind, execution.StepKind, StringComparison.Ordinal))
            throw new InvalidDataException("Execution step differs from command step.");
        RequireSha256(execution.Command.ApprovalSha256, nameof(execution.Command.ApprovalSha256));
        RequireSha256(execution.CommandSha256, nameof(execution.CommandSha256));
        RequireSha256(execution.AuthorizationSha256, nameof(execution.AuthorizationSha256));
        if (execution.SubmissionAttemptId == Guid.Empty)
            throw new InvalidDataException("Policy-owned submission attempt id is required before native dispatch.");
        RequireSha256(execution.SubmissionIntentSha256, nameof(execution.SubmissionIntentSha256));
        RequireSha256(execution.PendingStateSha256, nameof(execution.PendingStateSha256));
        RequireExact(execution.DeviceBindingId, active.DeviceBindingId, nameof(execution.DeviceBindingId));
        if (!FixedDigestEquals(execution.ActiveReleaseBomSha256, active.ReleaseBomSha256) || execution.ActiveReleaseBomGeneration != active.Generation ||
            !string.Equals(execution.ActiveReleaseBomExecutionTokenBase64, active.ExecutionTokenBase64, StringComparison.Ordinal))
            throw new InvalidDataException("Execution does not match active BOM truth.");
        var token = DecodeCanonicalToken(execution.ActiveReleaseBomExecutionTokenBase64);
        try
        {
            if (!FixedDigestEquals(Sha256(token), execution.ActiveReleaseBomTokenSha256))
                throw new InvalidDataException("Execution token digest mismatch.");
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private static byte[] DecodeCanonicalToken(string value)
    {
        var token = Convert.FromBase64String(value);
        if (token.Length != 32 || !string.Equals(Convert.ToBase64String(token), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(token);
            throw new InvalidDataException("Execution token is not canonical 256-bit Base64.");
        }
        return token;
    }

    private static void RequireExact(string actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException($"Unsupported {name}.");
    }

    private static void RequireSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)) || value.Any(char.IsUpper))
            throw new InvalidDataException($"Invalid {name}.");
    }

    private static void RequireOpaque(string value, string prefix, int hexadecimalLength, string name)
    {
        if (value is null || value.Length != prefix.Length + hexadecimalLength || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid {name}.");
        for (var index = prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                throw new InvalidDataException($"Invalid {name}.");
        }
    }

    private static void ValidateStoredAcknowledgement(
        WireExecution execution,
        string submittedRequestDigest,
        WireSubmissionAck acknowledgement)
    {
        RequireExact(acknowledgement.SchemaVersion, "1.0.0", nameof(acknowledgement.SchemaVersion));
        RequireExact(acknowledgement.ContractId, "native.submission.ack/v1", nameof(acknowledgement.ContractId));
        RequireExact(acknowledgement.ProducerModule, "windows-edge-worker", nameof(acknowledgement.ProducerModule));
        if (acknowledgement.SubmissionId == Guid.Empty || acknowledgement.CompletionHandleId == Guid.Empty ||
            acknowledgement.CommandId != execution.CommandId || acknowledgement.LeaseId != execution.LeaseId ||
            acknowledgement.Attempt != execution.Attempt ||
            !string.Equals(acknowledgement.SoulId, execution.SoulId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.DeviceBindingId, execution.DeviceBindingId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.PlatformAccountId, execution.PlatformAccountId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.TraceId, execution.TraceId, StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.IdempotencyKey, execution.IdempotencyKey, StringComparison.Ordinal) ||
            acknowledgement.OccurredAt != execution.Command.OccurredAt.AddSeconds(1) ||
            !string.Equals(acknowledgement.PrivacyClass, "internal", StringComparison.Ordinal) ||
            !string.Equals(acknowledgement.Durability, "REQUEST_AND_STATE_FLUSHED", StringComparison.Ordinal) ||
            !FixedDigestEquals(acknowledgement.CommandSha256, execution.CommandSha256) ||
            !FixedDigestEquals(acknowledgement.AuthorizationSha256, execution.AuthorizationSha256) ||
            acknowledgement.SubmissionAttemptId != execution.SubmissionAttemptId ||
            !FixedDigestEquals(acknowledgement.SubmissionIntentSha256, execution.SubmissionIntentSha256) ||
            !FixedDigestEquals(acknowledgement.PendingStateSha256, execution.PendingStateSha256) ||
            !FixedDigestEquals(acknowledgement.ActiveReleaseBomSha256, execution.ActiveReleaseBomSha256) ||
            acknowledgement.ActiveReleaseBomGeneration != execution.ActiveReleaseBomGeneration ||
            !FixedDigestEquals(acknowledgement.ActiveReleaseBomTokenSha256, execution.ActiveReleaseBomTokenSha256) ||
            !FixedDigestEquals(acknowledgement.SubmittedRequestSha256, submittedRequestDigest) ||
            !FixedDigestEquals(acknowledgement.AcknowledgementSha256, NativeSubmissionWireProtocol.ComputeAcknowledgementSha256(acknowledgement)))
            throw new InvalidDataException("Persisted submission acknowledgement is not the exact durable request binding.");
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool FixedDigestEquals(string left, string right)
    {
        RequireSha256(left, nameof(left));
        RequireSha256(right, nameof(right));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }
}

internal static class NativeSubmissionWireProtocol
{
    private const string SubmittedRequestDomain = "dps.executor-gateway.submitted-request/v1";
    private const string AcknowledgementDomain = "dps.executor-gateway.native-submission-ack/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ComputeSubmittedRequestSha256(WireExecution request) => Hash(writer =>
    {
        writer.Field(SubmittedRequestDomain);
        writer.Field(request.CommandId);
        writer.Field(request.LeaseId);
        writer.Field(request.Attempt);
        writer.Field(request.SoulId);
        writer.Field(request.DeviceBindingId);
        writer.Field(request.PlatformAccountId);
        writer.Field(request.TraceId);
        writer.Field(request.IdempotencyKey);
        writer.Field(request.StepId);
        writer.Field(request.StepKind);
        writer.Field(request.CommandSha256);
        writer.Field(request.AuthorizationSha256);
        writer.Field(request.ActiveReleaseBomSha256);
        writer.Field(request.ActiveReleaseBomGeneration);
        writer.Field(request.ActiveReleaseBomTokenSha256);
        writer.Field(request.SubmissionAttemptId);
        writer.Field(request.SubmissionIntentSha256);
        writer.Field(request.PendingStateSha256);
    });

    public static string ComputeAcknowledgementSha256(WireSubmissionAck acknowledgement) => Hash(writer =>
    {
        writer.Field(AcknowledgementDomain);
        writer.Field(acknowledgement.SchemaVersion);
        writer.Field(acknowledgement.ContractId);
        writer.Field(acknowledgement.ProducerModule);
        writer.Field(acknowledgement.SubmissionId);
        writer.Field(acknowledgement.CompletionHandleId);
        writer.Field(acknowledgement.CommandId);
        writer.Field(acknowledgement.LeaseId);
        writer.Field(acknowledgement.Attempt);
        writer.Field(acknowledgement.SoulId);
        writer.Field(acknowledgement.DeviceBindingId);
        writer.Field(acknowledgement.PlatformAccountId);
        writer.Field(acknowledgement.TraceId);
        writer.Field(acknowledgement.IdempotencyKey);
        writer.Field(acknowledgement.OccurredAt);
        writer.Field(acknowledgement.PrivacyClass);
        writer.Field(acknowledgement.Durability);
        writer.Field(acknowledgement.CommandSha256);
        writer.Field(acknowledgement.AuthorizationSha256);
        writer.Field(acknowledgement.SubmissionAttemptId);
        writer.Field(acknowledgement.SubmissionIntentSha256);
        writer.Field(acknowledgement.PendingStateSha256);
        writer.Field(acknowledgement.ActiveReleaseBomSha256);
        writer.Field(acknowledgement.ActiveReleaseBomGeneration);
        writer.Field(acknowledgement.ActiveReleaseBomTokenSha256);
        writer.Field(acknowledgement.SubmittedRequestSha256);
    });

    private static string Hash(Action<CanonicalWriter> write)
    {
        using var writer = new CanonicalWriter();
        write(writer);
        var bytes = writer.ToArray();
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class CanonicalWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        public void Field(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                _stream.Write(length);
                _stream.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Field(Guid value) => Field(value.ToString("N"));
        public void Field(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        public void Field(long value) => Field(value.ToString(CultureInfo.InvariantCulture));
        public void Field(DateTimeOffset value) => Field(value.ToString("O", CultureInfo.InvariantCulture));
        public byte[] ToArray() => _stream.ToArray();
        public void Dispose() => _stream.Dispose();
    }
}

internal sealed class FixtureStateStore(string path, JsonSerializerOptions json)
{
    public async Task<FixtureState> LoadAsync()
    {
        if (!File.Exists(path)) return FixtureState.CreateDefault();
        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<FixtureState>(stream, json)
            ?? throw new InvalidDataException("Fixture state is empty.");
        state.Executions = new Dictionary<string, StoredExecution>(state.Executions, StringComparer.Ordinal);
        return state;
    }

    public async Task SaveAsync(FixtureState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, json);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }
}

internal sealed class FixtureState
{
    public required FixtureActiveBinding ActiveBinding { get; set; }
    public string Mode { get; set; } = "none";
    public int SideEffectCount { get; set; }
    public Dictionary<string, StoredExecution> Executions { get; set; } = new(StringComparer.Ordinal);

    public static FixtureState CreateDefault() => new()
    {
        ActiveBinding = new FixtureActiveBinding(
            "dps.active-release-bom-binding/v1",
            "db_0123456789abcdef0123456789abcdef",
            new string('a', 64),
            7,
            0x42)
    };
}

internal sealed class FixtureWireRequest
{
    public required string SchemaVersion { get; init; }
    public required Guid RequestId { get; init; }
    public required string Operation { get; init; }
    public string? Mode { get; init; }
    public WireExecution? Execution { get; init; }
    public Guid? CompletionHandleId { get; init; }
}

internal sealed record FixtureWireResponse(
    string SchemaVersion,
    string EvidenceKind,
    string RequestId,
    string Status,
    string? ErrorCode,
    WireActiveBinding? ActiveBinding,
    WireSubmissionAck? SubmissionAck,
    WireNativeResult? NativeResult,
    int? SideEffectCount)
{
    public static FixtureWireResponse Ok(Guid requestId, WireActiveBinding? activeBinding = null, WireSubmissionAck? submissionAck = null, WireNativeResult? nativeResult = null, int? sideEffectCount = null) =>
        new("dps.native-fixture.response/v1", "REAL_LOCAL_PROCESS", requestId.ToString("D"), "OK", null, activeBinding, submissionAck, nativeResult, sideEffectCount);

    public static FixtureWireResponse Error(string requestId, string code) =>
        new("dps.native-fixture.response/v1", "REAL_LOCAL_PROCESS", requestId, "ERROR", code, null, null, null, null);
}

internal sealed record WireActiveBinding(string SchemaVersion, string DeviceBindingId, string ReleaseBomSha256, long Generation, string ExecutionTokenBase64);
internal sealed record FixtureActiveBinding(string SchemaVersion, string DeviceBindingId, string ReleaseBomSha256, long Generation, byte TokenMarker)
{
    [JsonIgnore]
    public string ExecutionTokenBase64 => Convert.ToBase64String(Enumerable.Repeat(TokenMarker, 32).ToArray());
    public WireActiveBinding ToWire() => new(SchemaVersion, DeviceBindingId, ReleaseBomSha256, Generation, ExecutionTokenBase64);
}
internal sealed record StoredExecution(string SubmittedRequestSha256, WireSubmissionAck Acknowledgement, WireNativeResult Result);
internal sealed record WireExecution(
    WireCommand Command,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    Guid StepId,
    string StepKind,
    string CommandSha256,
    string AuthorizationSha256,
    Guid SubmissionAttemptId,
    string SubmissionIntentSha256,
    string PendingStateSha256,
    string ActiveReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomExecutionTokenBase64,
    string ActiveReleaseBomTokenSha256);
internal sealed record WireSubmissionAck(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("submission_id")] Guid SubmissionId,
    [property: JsonPropertyName("completion_handle_id")] Guid CompletionHandleId,
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("lease_id")] Guid LeaseId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(FixtureUtcDateTimeOffsetConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("durability")] string Durability,
    [property: JsonPropertyName("command_sha256")] string CommandSha256,
    [property: JsonPropertyName("authorization_sha256")] string AuthorizationSha256,
    [property: JsonPropertyName("submission_attempt_id")] Guid SubmissionAttemptId,
    [property: JsonPropertyName("submission_intent_sha256")] string SubmissionIntentSha256,
    [property: JsonPropertyName("pending_state_sha256")] string PendingStateSha256,
    [property: JsonPropertyName("active_release_bom_sha256")] string ActiveReleaseBomSha256,
    [property: JsonPropertyName("active_release_bom_generation")] long ActiveReleaseBomGeneration,
    [property: JsonPropertyName("active_release_bom_token_sha256")] string ActiveReleaseBomTokenSha256,
    [property: JsonPropertyName("submitted_request_sha256")] string SubmittedRequestSha256,
    [property: JsonPropertyName("acknowledgement_sha256")] string AcknowledgementSha256);

internal sealed class FixtureUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (raw is null || !DateTimeOffset.TryParseExact(
                raw,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
            throw new JsonException("Fixture native acknowledgement occurred_at is not canonical UTC.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        if (value.Offset != TimeSpan.Zero) throw new JsonException("Fixture native acknowledgement occurred_at must be UTC.");
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
internal sealed record WireCommand(
    string SchemaVersion,
    string ContractId,
    string ProducerModule,
    Guid CommandId,
    Guid OperationId,
    Guid ApprovalId,
    string ApprovalSha256,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string PrivacyClass,
    string ActionKind,
    bool IsSideEffect,
    string? PlatformAuthorizationId,
    Guid LeaseId,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt,
    int Attempt,
    IReadOnlyList<WireCommandStep> Steps);
internal sealed record WireCommandStep(Guid StepId, string StepKind, IReadOnlyDictionary<string, string> Arguments, bool RetrySafe, string PostconditionKind);
internal sealed record WireNativeResult(
    Guid NativeResultId,
    Guid CommandId,
    Guid LeaseId,
    int Attempt,
    string SoulId,
    string DeviceBindingId,
    string PlatformAccountId,
    string TraceId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    string ActiveReleaseBomSha256,
    long ActiveReleaseBomGeneration,
    string ActiveReleaseBomTokenSha256,
    IReadOnlyList<WireNativeStepResult> StepResults);
internal sealed record WireNativeStepResult(Guid StepId, string StepKind, string Status, string NativeCode, string EvidenceDigest);
