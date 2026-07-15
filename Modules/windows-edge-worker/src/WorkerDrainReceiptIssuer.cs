using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dps.EdgeLocalJournal;
using Dps.WindowsEdgeSupervisor.Contracts;

namespace Dps.WindowsEdgeWorker;

public interface IWorkerDrainSigningAuthority
{
    string KeyId { get; }

    ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> canonicalStatement,
        CancellationToken cancellationToken = default);

    bool Verify(
        ReadOnlySpan<byte> canonicalStatement,
        ReadOnlySpan<byte> signature);
}

public sealed record WorkerDrainReceiptIssuanceResult(
    byte[] ExactWorkerReceiptWireUtf8,
    string WorkerReceiptWireSha256,
    string JournalEntryId,
    string JournalEntryChecksum,
    long JournalSequence);

public sealed class WorkerDrainReceiptIssuer
{
    private const string InputFingerprintDomain =
        "dps.windows-edge-worker.drain-receipt-input/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan DefaultMaximumDrainWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultJournalAppendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultReceiptValidity = TimeSpan.FromMinutes(4);

    private readonly CommandProcessor _processor;
    private readonly EdgeLocalJournalAdapter _journal;
    private readonly DurableWorkerDrainReceiptStore _receiptStore;
    private readonly IWorkerDrainSigningAuthority _signingAuthority;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maximumDrainWait;
    private readonly TimeSpan _journalAppendTimeout;
    private readonly TimeSpan _receiptValidity;
    private readonly SemaphoreSlim _issuanceGate = new(1, 1);

    public WorkerDrainReceiptIssuer(
        CommandProcessor processor,
        EdgeLocalJournalAdapter journal,
        DurableWorkerDrainReceiptStore receiptStore,
        IWorkerDrainSigningAuthority signingAuthority,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumDrainWait = null,
        TimeSpan? journalAppendTimeout = null,
        TimeSpan? receiptValidity = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        _signingAuthority = signingAuthority ??
            throw new ArgumentNullException(nameof(signingAuthority));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumDrainWait = ValidateDuration(
            maximumDrainWait ?? DefaultMaximumDrainWait,
            TimeSpan.FromHours(1),
            nameof(maximumDrainWait));
        _journalAppendTimeout = ValidateDuration(
            journalAppendTimeout ?? DefaultJournalAppendTimeout,
            TimeSpan.FromMinutes(1),
            nameof(journalAppendTimeout));
        _receiptValidity = ValidateDuration(
            receiptValidity ?? DefaultReceiptValidity,
            TimeSpan.FromMinutes(5),
            nameof(receiptValidity));
        RequireKeyId(_signingAuthority.KeyId, nameof(signingAuthority));
    }

    public async Task<WorkerDrainReceiptIssuanceResult> IssueAsync(
        ReadOnlyMemory<byte> exactSignedDirectiveWireUtf8,
        DrainDirectiveExpectationV1 expectation,
        RSA externallyPinnedSupervisorPublicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(externallyPinnedSupervisorPublicKey);
        if (exactSignedDirectiveWireUtf8.IsEmpty ||
            exactSignedDirectiveWireUtf8.Length > DrainDirectiveV1Codec.MaximumWireBytes)
            throw new ArgumentOutOfRangeException(nameof(exactSignedDirectiveWireUtf8));

        await _issuanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directiveWire = exactSignedDirectiveWireUtf8.ToArray();
            var authorizationNow = ReadExactUtcNow();
            var supervisorKeyId = DrainDirectiveV1Codec.ComputeKeyId(
                externallyPinnedSupervisorPublicKey);
            var directiveCandidate = DrainDirectiveV1Codec.Deserialize(directiveWire);
            if (!directiveWire.AsSpan().SequenceEqual(
                    DrainDirectiveV1Codec.Serialize(directiveCandidate)))
                throw new InvalidDataException(
                    "drain directive wire is not the exact canonical serialization");
            var inputFingerprint = ComputeInputFingerprint(
                directiveCandidate,
                expectation,
                supervisorKeyId,
                _signingAuthority.KeyId);

            var existing = _receiptStore.ReadExisting(expectation.DrainId);
            SignedDrainDirectiveV1 directive;
            var isDurableContinuation = existing is not null &&
                existing.InputFingerprintSha256 == inputFingerprint;
            if (isDurableContinuation)
            {
                directive = DrainDirectiveV1Codec.DecodeAndVerifyDurableContinuation(
                    directiveWire,
                    expectation,
                    externallyPinnedSupervisorPublicKey).Envelope;
            }
            else
            {
                var verified = DrainDirectiveV1Codec.DecodeAndVerify(
                    directiveWire,
                    expectation,
                    externallyPinnedSupervisorPublicKey,
                    authorizationNow);
                directive = verified.Envelope;
                var directiveIssuedAt = ParseCanonicalUtc(directive.IssuedAt, "issued_at");
                if (authorizationNow < directiveIssuedAt)
                    throw new InvalidDataException(
                        "Worker clock is earlier than the signed directive issue time");

                if (existing is not null)
                {
                    _ = _receiptStore.Read(expectation.DrainId, inputFingerprint);
                    throw new InvalidOperationException(
                        "conflicting drain input was expected to quarantine the durable receipt store");
                }
            }

            _processor.StopIntake();
            await AwaitDurableDrainAsync(cancellationToken).ConfigureAwait(false);

            PersistedWorkerDrainReceipt persisted;
            if (existing is not null)
            {
                persisted = ValidatePersistedReceipt(
                    existing,
                    expectation,
                    inputFingerprint);
            }
            else
            {
                var completionNow = ReadExactUtcNow();
                if (completionNow < authorizationNow)
                    throw new InvalidDataException(
                        "Worker clock moved backwards while the drain was in progress");
                var receipt = await CreateSignedReceiptAsync(
                    directive,
                    completionNow,
                    cancellationToken).ConfigureAwait(false);
                var exactWorkerWire = WorkerDrainReceiptContractCodec.Serialize(receipt);
                ValidateSignedWorkerReceipt(exactWorkerWire, expectation);
                persisted = _receiptStore.Prepare(
                    directive.DrainId,
                    inputFingerprint,
                    exactWorkerWire);
            }

            if (persisted.State == WorkerDrainReceiptPersistenceState.Committed)
                return ToResult(persisted);

            var signedReceipt = ValidateSignedWorkerReceipt(
                persisted.ExactWireUtf8,
                expectation);
            var journalRequest = CreateJournalRequest(
                signedReceipt,
                persisted.WireSha256);

            WorkerJournalAppendReceipt journalReceipt;
            try
            {
                journalReceipt = await _journal.AppendAsync(
                        journalRequest,
                        CancellationToken.None)
                    .WaitAsync(_journalAppendTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    "Journal append did not return a durable receipt within the bounded attempt; exact PREPARED Worker wire remains retryable",
                    exception);
            }

            WorkerJournalReceiptValidator.Validate(journalRequest, journalReceipt);
            var committed = _receiptStore.Commit(
                persisted.DrainId,
                inputFingerprint,
                persisted.WireSha256,
                journalReceipt.EntryId,
                journalReceipt.EntryChecksum,
                journalReceipt.Sequence);
            return ToResult(committed);
        }
        finally
        {
            _issuanceGate.Release();
        }
    }

    private async Task AwaitDurableDrainAsync(CancellationToken cancellationToken)
    {
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        drainCancellation.CancelAfter(_maximumDrainWait);
        try
        {
            while (true)
            {
                await _processor.ReconcilePreparedCompletionsAsync(drainCancellation.Token)
                    .ConfigureAwait(false);
                if (_processor.IsDrained)
                    return;
                await Task.Delay(TimeSpan.FromMilliseconds(25), drainCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Worker did not reach durable zero-in-flight drain truth within the bounded wait",
                exception);
        }
    }

    private async Task<SignedWorkerDrainReceiptV1> CreateSignedReceiptAsync(
        SignedDrainDirectiveV1 directive,
        DateTimeOffset completionNow,
        CancellationToken cancellationToken)
    {
        var issuedAt = FormatCanonicalUtc(completionNow);
        var expiresAt = FormatCanonicalUtc(completionNow.Add(_receiptValidity));
        var claims = new WorkerDrainReceiptClaimsV1(
            WorkerDrainReceiptContractCodec.SchemaVersion,
            WorkerDrainReceiptContractCodec.ContractId,
            WorkerDrainReceiptContractCodec.ProducerModule,
            directive.SoulId,
            directive.DeviceBindingId,
            directive.PlatformAccountId,
            directive.TraceId,
            directive.IdempotencyKey,
            directive.OccurredAt,
            directive.PrivacyClass,
            directive.DrainId,
            directive.Slot,
            directive.WorkerVersion,
            directive.WorkerArtifactSha256,
            directive.JournalArtifactSha256,
            directive.ReleaseBomSha256,
            directive.ProtectedPolicySha256,
            directive.RoutingEpoch,
            IntakeStopped: true,
            WorkerDrained: true,
            RemainingInFlight: 0,
            issuedAt,
            issuedAt,
            expiresAt);
        var statement = WorkerDrainReceiptContractCodec.CreateSigningStatement(claims);
        byte[]? signature = null;
        try
        {
            signature = await _signingAuthority.SignAsync(statement, cancellationToken)
                .ConfigureAwait(false);
            if (signature is null || signature.Length == 0 ||
                !_signingAuthority.Verify(statement, signature))
                throw new CryptographicException(
                    "Worker signing authority did not return a verifiable RSA-PSS signature");
            return WorkerDrainReceiptContractCodec.AttachSignature(
                claims,
                _signingAuthority.KeyId,
                Convert.ToBase64String(signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statement);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }
    }

    private SignedWorkerDrainReceiptV1 ValidateSignedWorkerReceipt(
        ReadOnlySpan<byte> exactWireUtf8,
        DrainDirectiveExpectationV1 expectation)
    {
        var receipt = WorkerDrainReceiptContractCodec.Deserialize(exactWireUtf8);
        if (!exactWireUtf8.SequenceEqual(WorkerDrainReceiptContractCodec.Serialize(receipt)))
            throw new InvalidDataException(
                "persisted Worker drain receipt is not the exact canonical wire");
        ValidateWorkerReceiptScope(receipt, expectation);
        if (receipt.WorkerKeyId != _signingAuthority.KeyId)
            throw new CryptographicException(
                "persisted Worker drain receipt key differs from the active Release BOM signing identity");
        var statement = WorkerDrainReceiptContractCodec.CreateSigningStatement(receipt);
        var signature = Convert.FromBase64String(receipt.WorkerSignature);
        try
        {
            if (!_signingAuthority.Verify(statement, signature))
                throw new CryptographicException(
                    "persisted Worker drain receipt signature cannot be verified by the active Worker identity");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statement);
            CryptographicOperations.ZeroMemory(signature);
        }
        return receipt;
    }

    private PersistedWorkerDrainReceipt ValidatePersistedReceipt(
        PersistedWorkerDrainReceipt persisted,
        DrainDirectiveExpectationV1 expectation,
        string inputFingerprint)
    {
        if (persisted.InputFingerprintSha256 != inputFingerprint ||
            persisted.WireSha256 != WorkerDrainReceiptContractCodec.ComputeSha256(
                persisted.ExactWireUtf8))
            throw new InvalidDataException(
                "persisted Worker drain receipt no longer matches its signed input or exact wire digest");
        _ = ValidateSignedWorkerReceipt(persisted.ExactWireUtf8, expectation);
        return persisted;
    }

    private static WorkerJournalAppendRequest CreateJournalRequest(
        SignedWorkerDrainReceiptV1 receipt,
        string workerReceiptWireSha256)
    {
        var payload = WorkerDrainReceiptContractCodec.CreateJournalPayload(
            receipt,
            workerReceiptWireSha256);
        var payloadJson = StrictUtf8.GetString(payload);
        if (CanonicalJson.Canonicalize(payloadJson) != payloadJson)
            throw new InvalidDataException(
                "Supervisor-owned Worker receipt payload is not exact Journal canonical JSON");
        var drainBody = receipt.DrainId["drain-".Length..];
        return new WorkerJournalAppendRequest(
            "1.0",
            "edge.journal.append/v1",
            "windows-edge-worker",
            receipt.DrainId,
            "worker-drain-" + drainBody,
            "WORKER_DRAINED",
            receipt.TraceId,
            receipt.IdempotencyKey,
            receipt.PrivacyClass,
            receipt.SoulId,
            receipt.DeviceBindingId,
            receipt.PlatformAccountId,
            payloadJson,
            WorkerDrainReceiptContractCodec.ComputeSha256(payload),
            ParseCanonicalUtc(receipt.OccurredAt, "occurred_at"));
    }

    private static void ValidateWorkerReceiptScope(
        SignedWorkerDrainReceiptV1 receipt,
        DrainDirectiveExpectationV1 expectation)
    {
        if (receipt.DrainId != expectation.DrainId ||
            receipt.Slot != expectation.Slot ||
            receipt.WorkerVersion != expectation.WorkerVersion ||
            receipt.WorkerArtifactSha256 != expectation.WorkerArtifactSha256 ||
            receipt.JournalArtifactSha256 != expectation.JournalArtifactSha256 ||
            receipt.ReleaseBomSha256 != expectation.ReleaseBomSha256 ||
            receipt.ProtectedPolicySha256 != expectation.ProtectedPolicySha256 ||
            receipt.RoutingEpoch != expectation.RoutingEpoch ||
            receipt.SoulId != expectation.SoulId ||
            receipt.DeviceBindingId != expectation.DeviceBindingId ||
            receipt.PlatformAccountId != expectation.PlatformAccountId ||
            receipt.TraceId != expectation.TraceId ||
            receipt.IdempotencyKey != expectation.IdempotencyKey ||
            receipt.OccurredAt != expectation.OccurredAt ||
            !receipt.IntakeStopped || !receipt.WorkerDrained ||
            receipt.RemainingInFlight != 0)
            throw new InvalidDataException(
                "Worker drain receipt does not match exact active scope and durable zero-in-flight truth");
    }

    private static string ComputeInputFingerprint(
        SignedDrainDirectiveV1 directive,
        DrainDirectiveExpectationV1 expectation,
        string supervisorKeyId,
        string workerKeyId)
    {
        var statement = DrainDirectiveV1Codec.CreateSigningStatement(
            ToDirectiveClaims(directive));
        using var stream = new MemoryStream();
        WriteComponent(stream, InputFingerprintDomain);
        WriteComponent(stream, WorkerDrainReceiptContractCodec.ComputeSha256(statement));
        WriteComponent(stream, expectation.DrainId);
        WriteComponent(stream, expectation.Slot);
        WriteComponent(stream, expectation.WorkerVersion);
        WriteComponent(stream, expectation.WorkerArtifactSha256);
        WriteComponent(stream, expectation.JournalArtifactSha256);
        WriteComponent(stream, expectation.ReleaseBomSha256);
        WriteComponent(stream, expectation.ProtectedPolicySha256);
        WriteComponent(stream, expectation.RoutingEpoch.ToString(CultureInfo.InvariantCulture));
        WriteComponent(stream, expectation.SoulId);
        WriteComponent(stream, expectation.DeviceBindingId);
        WriteComponent(stream, expectation.PlatformAccountId);
        WriteComponent(stream, expectation.TraceId);
        WriteComponent(stream, expectation.IdempotencyKey);
        WriteComponent(stream, expectation.OccurredAt);
        WriteComponent(stream, supervisorKeyId);
        WriteComponent(stream, workerKeyId);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statement);
        }
    }

    private static DrainDirectiveClaimsV1 ToDirectiveClaims(
        SignedDrainDirectiveV1 directive) => new(
        directive.SchemaVersion,
        directive.ContractId,
        directive.ProducerModule,
        directive.SoulId,
        directive.DeviceBindingId,
        directive.PlatformAccountId,
        directive.TraceId,
        directive.IdempotencyKey,
        directive.OccurredAt,
        directive.PrivacyClass,
        directive.DrainId,
        directive.Slot,
        directive.WorkerVersion,
        directive.WorkerArtifactSha256,
        directive.JournalArtifactSha256,
        directive.ReleaseBomSha256,
        directive.ProtectedPolicySha256,
        directive.RoutingEpoch,
        directive.IssuedAt,
        directive.NotBefore,
        directive.ExpiresAt,
        directive.SupervisorKeyId,
        directive.SignatureAlgorithm);

    private static void WriteComponent(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private DateTimeOffset ReadExactUtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        if (now.Offset != TimeSpan.Zero)
            throw new InvalidDataException(
                "Worker TimeProvider must return an explicit zero-offset UTC value");
        return now;
    }

    private static DateTimeOffset ParseCanonicalUtc(string value, string field)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) || parsed.Offset != TimeSpan.Zero ||
            parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) != value)
            throw new InvalidDataException(
                field + " must be canonical UTC with seven fractional digits and +00:00");
        return parsed;
    }

    private static string FormatCanonicalUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new InvalidDataException(
                "Worker receipt time must have an explicit zero UTC offset");
        return value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture);
    }

    private static TimeSpan ValidateDuration(
        TimeSpan value,
        TimeSpan maximum,
        string parameter)
    {
        if (value <= TimeSpan.Zero || value > maximum)
            throw new ArgumentOutOfRangeException(parameter);
        return value;
    }

    private static void RequireKeyId(string value, string parameter)
    {
        if (value is null || value.Length != 71 ||
            !value.StartsWith("sha256_", StringComparison.Ordinal) ||
            value[7..].Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            throw new ArgumentException(
                "Worker signing key id must be sha256_<64 lowercase hex>",
                parameter);
    }

    private static WorkerDrainReceiptIssuanceResult ToResult(
        PersistedWorkerDrainReceipt persisted)
    {
        if (persisted.State != WorkerDrainReceiptPersistenceState.Committed ||
            persisted.JournalEntryId is null ||
            persisted.JournalEntryChecksum is null ||
            persisted.JournalSequence is null)
            throw new InvalidDataException(
                "Worker drain receipt cannot be returned before durable Journal commit");
        return new WorkerDrainReceiptIssuanceResult(
            persisted.ExactWireUtf8.ToArray(),
            persisted.WireSha256,
            persisted.JournalEntryId,
            persisted.JournalEntryChecksum,
            persisted.JournalSequence.Value);
    }
}
