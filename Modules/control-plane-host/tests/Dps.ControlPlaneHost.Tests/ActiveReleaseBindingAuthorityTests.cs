using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dps.ControlPlaneHost.Contracts;
using Xunit;

namespace Dps.ControlPlaneHost.Tests;

public sealed class ActiveReleaseBindingAuthorityTests
{
    private const string Device = "db_11111111111111111111111111111111";
    private const string OtherDevice = "db_22222222222222222222222222222222";
    private const string NumberCorpusSha256 =
        "14f115b4acb3b11e4cc97b4fd657eea6b112841b3ee7bdc6b293e9fae4add4d3";
    private const string StringCorpusSha256 =
        "a7a132a48170ce6495af87706faa722670d4ceb856620436b5906e78d1ee42f9";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private static string Token(string seed)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("token:" + seed)));

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexStringLower(SHA256.HashData(value));

    private static string BomId(byte[] bom)
    {
        using var document = JsonDocument.Parse(bom);
        return document.RootElement.GetProperty("bom_id").GetString()!;
    }

    private static string ActivationRequestSha256(
        string deviceBindingId,
        ReadOnlySpan<byte> candidateBom,
        ReadOnlySpan<byte> previousStableBom,
        string token)
    {
        var domain = Encoding.UTF8.GetBytes("dps.release.binding.activate/v2\n");
        var device = Encoding.UTF8.GetBytes(deviceBindingId);
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var material = new byte[
            domain.Length
            + sizeof(long) + device.Length
            + sizeof(long) + candidateBom.Length
            + sizeof(long) + previousStableBom.Length
            + sizeof(long) + tokenBytes.Length];
        var offset = 0;
        domain.CopyTo(material, offset);
        offset += domain.Length;

        static void Append(
            ReadOnlySpan<byte> value,
            Span<byte> destination,
            ref int offset)
        {
            BinaryPrimitives.WriteInt64BigEndian(
                destination.Slice(offset, sizeof(long)),
                value.Length);
            offset += sizeof(long);
            value.CopyTo(destination[offset..]);
            offset += value.Length;
        }

        Append(device, material, ref offset);
        Append(candidateBom, material, ref offset);
        Append(previousStableBom, material, ref offset);
        Append(tokenBytes, material, ref offset);
        return Sha256Hex(material);
    }

    private static string NonCanonicalBase64PadAlias(string value)
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var padding = value.Length - value.TrimEnd('=').Length;
        Assert.Contains(padding, new[] { 1, 2 });
        var index = value.Length - padding - 1;
        var replacement = alphabet[alphabet.IndexOf(value[index]) ^ 1];
        var alias = value[..index] + replacement + value[(index + 1)..];
        Assert.Equal(Convert.FromBase64String(value), Convert.FromBase64String(alias));
        Assert.NotEqual(value, alias);
        return alias;
    }

    private static byte[] BuildRsaRepresentativeAliasBom(
        TestSigner signer,
        Func<byte[]> buildValidBom,
        bool shortI2osp)
    {
        var modulus = signer.Rsa.ExportParameters(false).Modulus!;
        for (var attempt = 0; attempt < 4_096; attempt++)
        {
            var node = JsonNode.Parse(buildValidBom())!.AsObject();
            var signatureNode = node["signature"]!.AsObject();
            var signature = Convert.FromBase64String(
                signatureNode["value"]!.GetValue<string>());
            byte[] alias;
            if (shortI2osp)
            {
                if (signature[0] != 0)
                    continue;
                alias = signature[1..];
            }
            else
            {
                var sum = new BigInteger(signature, isUnsigned: true, isBigEndian: true)
                    + new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
                var raw = sum.ToByteArray(isUnsigned: true, isBigEndian: true);
                if (raw.Length > modulus.Length)
                    continue;
                alias = new byte[modulus.Length];
                raw.CopyTo(alias, alias.Length - raw.Length);
            }
            Assert.NotEqual(signature, alias);
            signatureNode["value"] = Convert.ToBase64String(alias);
            using var document = JsonDocument.Parse(node.ToJsonString());
            return ReleaseBomCanonicalJson.Serialize(document.RootElement);
        }
        throw new InvalidOperationException(
            shortI2osp
                ? "unable to construct a short RSA I2OSP representative"
                : "unable to construct an RSA s+n representative alias");
    }

    private sealed class TestSigner : IDisposable
    {
        private readonly Dictionary<string, byte[]> _stableTwins =
            new(StringComparer.Ordinal);

        public RSA Rsa { get; } = RSA.Create(2048);
        public string KeyId { get; }
        public string Identity { get; }

        public TestSigner(string keyId = "test-bom-key-v1", string identity = "test-release-controller")
        {
            KeyId = keyId;
            Identity = identity;
        }

        public ReleaseBomTrustKey TrustKey
        {
            get
            {
                var parameters = Rsa.ExportParameters(false);
                return new ReleaseBomTrustKey(
                    KeyId,
                    Identity,
                    Convert.ToHexStringLower(parameters.Modulus!),
                    65537);
            }
        }

        private static string WireBomId(string bomId)
            => bomId.Length >= 8 ? bomId : "test-" + bomId;

        /// <summary>
        /// Builds a minimal legal signed Release BOM carrying the exact
        /// candidate_bom_validator._BOM_FIELDS top-level set. Deep subtrees
        /// (modules, evidence, ...) are placeholders because the activation
        /// authority only enforces the activation-safety subset.
        /// </summary>
        public byte[] SignBom(
            string bomId,
            long signerGeneration,
            string executionTokenBase64,
            string? previousStableBomSha256,
            string? previousStableBomId = null,
            Action<JsonObject>? mutateBeforeSign = null,
            string? algorithm = null,
            string? keyIdOverride = null)
        {
            var tokenBytes = Convert.FromBase64String(executionTokenBase64);
            var payload = new JsonObject
            {
                ["schema_version"] = "dps.release-bom/v1",
                ["bom_id"] = WireBomId(bomId),
                ["status"] = "SIGNED",
                ["integration_commit"] = new string('a', 40),
                ["created_at"] = "2026-07-14T00:00:00.0000001Z",
                ["release_bom_generation"] = signerGeneration,
                ["activation_token_sha256"] = Convert.ToHexStringLower(SHA256.HashData(tokenBytes)),
                ["modules"] = new JsonArray(),
                ["instruction_hashes"] = new JsonObject(),
                ["contracts"] = new JsonArray(),
                ["database_versions"] = new JsonObject(),
                ["dependency_dag_sha256"] = new string('b', 64),
                ["compatibility_matrix_sha256"] = new string('c', 64),
                ["feature_flags"] = new JsonObject(),
                ["kill_switches"] = new JsonArray(),
                ["ai_toolchain"] = new JsonObject(),
                ["evidence"] = new JsonArray(),
                ["risk"] = new JsonObject(),
                ["release_approval"] = new JsonObject(),
                ["rollout"] = new JsonObject(),
                ["rollback"] = new JsonObject(),
                ["previous_stable_bom"] = previousStableBomSha256 is null
                    ? null
                    : (JsonNode)(previousStableBomId ?? "bom-previous-" + bomId),
                ["previous_stable_bom_sha256"] = previousStableBomSha256,
                ["native_stop_authorities"] = new JsonArray(),
                ["device_route_assignment_authorities"] = new JsonArray(),
                ["native_stop_challenge_authorities"] = new JsonArray()
            };
            mutateBeforeSign?.Invoke(payload);
            using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
            var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
            var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n").Concat(canonical).ToArray();
            var signature = Rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = algorithm ?? "rsa-pss-sha256",
                ["key_id"] = keyIdOverride ?? KeyId,
                ["value"] = Convert.ToBase64String(signature)
            };
            // The authority only accepts the canonical sorted compact wire,
            // so the fixture emits exactly that encoding.
            using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
            return ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement);
        }

        public byte[] StableTwin(byte[] signedBom)
        {
            var cacheKey = Sha256Hex(signedBom);
            if (_stableTwins.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var stable = ResignStableTwin(signedBom);
            _stableTwins.Add(cacheKey, stable);
            return stable;
        }

        public byte[] ResignStableTwin(
            byte[] signedBom,
            Action<JsonObject>? mutateBeforeSign = null)
        {
            var payload = JsonNode.Parse(signedBom)!.AsObject();
            payload["status"] = "STABLE";
            payload.Remove("signature");
            mutateBeforeSign?.Invoke(payload);
            using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
            var canonical = ReleaseBomCanonicalJson.Serialize(payloadDocument.RootElement);
            var message = Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
                .Concat(canonical)
                .ToArray();
            var signature = Rsa.SignData(
                message,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            payload["signature"] = new JsonObject
            {
                ["algorithm"] = "rsa-pss-sha256",
                ["key_id"] = KeyId,
                ["value"] = Convert.ToBase64String(signature)
            };
            using var fullDocument = JsonDocument.Parse(payload.ToJsonString());
            return ReleaseBomCanonicalJson.Serialize(fullDocument.RootElement);
        }

        public void Dispose() => Rsa.Dispose();
    }

    private sealed class FrozenTruthStore(IReadOnlyList<ReleaseBindingTruthRecord> records)
        : IReleaseBindingTruthStore,
          IActiveReleaseBindingRecoveryCoordinator
    {
        public void Append(ReleaseBindingTruthRecord record) { }
        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll() => records;

        public long LoadDeviceHeadSequence(string deviceBindingId)
            => records
                .Where(record => string.Equals(
                    record.DeviceBindingId, deviceBindingId, StringComparison.Ordinal))
                .Select(record => record.Receipt.Sequence)
                .DefaultIfEmpty(0)
                .Max();

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
            string deviceBindingId,
            long afterSequence)
            => [.. records
                .Where(record => string.Equals(
                        record.DeviceBindingId, deviceBindingId, StringComparison.Ordinal)
                    && record.Receipt.Sequence > afterSequence)
                .OrderBy(record => record.Receipt.Sequence)];

        public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
            string deviceBindingId,
            long afterSequence)
        {
            var delta = LoadAfter(deviceBindingId, afterSequence);
            return new ReleaseBindingJournalSnapshot(
                LoadDeviceHeadSequence(deviceBindingId),
                delta);
        }

        public ValueTask<IActiveReleaseBindingRecoveryScope> AcquireAsync(
            string deviceBindingId,
            CancellationToken cancellationToken)
            => ValueTask.FromException<IActiveReleaseBindingRecoveryScope>(
                new NotSupportedException(
                    "Frozen journal recovery fixtures do not issue recovery scopes."));
    }

    private static ActiveReleaseBindingAuthority Authority(
        TestSigner signer,
        IReleaseBindingTruthStore? store = null)
        => new([signer.TrustKey], store ?? InMemoryReleaseBindingTruthStore.CreateTestOnly(), () => Now);

    private static (byte[] Bom, string Token) MakeBom(
        TestSigner signer,
        string bomId,
        long signerGeneration,
        byte[]? previousBom)
    {
        var previousStable = previousBom is null
            ? null
            : signer.StableTwin(previousBom);
        string? previousStableBomId = null;
        if (previousStable is not null)
        {
            using var document = JsonDocument.Parse(previousStable);
            previousStableBomId = document.RootElement
                .GetProperty("bom_id")
                .GetString();
        }
        return (signer.SignBom(
                    bomId,
                    signerGeneration,
                    Token(bomId),
                    previousStable is null ? null : Sha256Hex(previousStable),
                    previousStableBomId),
                Token(bomId));
    }

    private static ReleaseBindingReceiptV1 ActivateNext(
        ActiveReleaseBindingAuthority authority,
        TestSigner signer,
        string deviceBindingId,
        byte[] candidateBom,
        byte[] currentSignedBom,
        string executionTokenBase64)
        => authority.Activate(
            deviceBindingId,
            candidateBom,
            signer.StableTwin(currentSignedBom),
            executionTokenBase64);

    private static (byte[] Bom, string Token) MakeBomFromStable(
        TestSigner signer,
        string bomId,
        long signerGeneration,
        byte[] previousStableBom)
    {
        var token = Token(bomId);
        return (signer.SignBom(
                    bomId,
                    signerGeneration,
                    token,
                    Sha256Hex(previousStableBom),
                    BomId(previousStableBom)),
                token);
    }

    // ----- lifecycle -----

    [Fact, Trait("Category", "Unit")]
    public void ActivateExposesVerifiedActiveBinding()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        var receipt = authority.Activate(Device, bom, token);

        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.NotNull(binding);
        Assert.Equal(Sha256Hex(bom), binding!.ReleaseBomSha256);
        Assert.Equal(1, binding.Generation);
        Assert.Equal(1, binding.ReleaseBomGeneration);
        Assert.Equal(token, binding.ExecutionTokenBase64);
        Assert.Equal("active", binding.Status);
        Assert.Equal(signer.Identity, binding.SignerIdentity);
        Assert.Equal(signer.KeyId, binding.SignerKeyId);
        Assert.Equal("activation", receipt.ReceiptKind);
        Assert.Null(receipt.From);
        Assert.Equal(binding.ReleaseBomSha256, receipt.To.ReleaseBomSha256);
        Assert.Equal(1, receipt.Sequence);
        Assert.Equal(binding.ReceiptId, receipt.ReceiptId);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task StoreIssuedRecoveryScopeReadsExactActiveAndSerializesOnlyItsDevice()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-coordinated", 1, null);
        authority.Activate(Device, bom, token);

        var scope = await authority.RecoveryCapability.AcquireAsync(
            Device, TestContext.Current.CancellationToken);
        Assert.Equal(Device, scope.ActiveBinding.DeviceBindingId);
        Assert.Equal(Sha256Hex(bom), scope.ActiveBinding.ReleaseBomSha256);
        Assert.Equal(1, scope.ActiveBinding.Generation);
        Assert.Equal(token, scope.ActiveBinding.ExecutionTokenBase64);

        var sameDeviceTransition = Task.Run(() => authority.Revoke(Device, 1));
        var (otherBom, otherToken) = MakeBom(signer, "bom-other", 1, null);
        var otherDeviceTransition = Task.Run(
            () => authority.Activate(OtherDevice, otherBom, otherToken));
        var otherReceipt = await otherDeviceTransition.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal("activation", otherReceipt.ReceiptKind);
        await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        Assert.False(sameDeviceTransition.IsCompleted);

        await scope.DisposeAsync();
        // Public lease disposal is idempotent and cannot over-release the
        // exact store primitive it owns.
        await scope.DisposeAsync();
        var revoked = await sameDeviceTransition.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal("revocation", revoked.ReceiptKind);
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public async Task FailedRecoveryScopeAcquisitionReleasesTheTransitionPrimitive()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);

        await Assert.ThrowsAsync<ActiveReleaseBindingException>(
            () => authority.RecoveryCapability.AcquireAsync(
                Device, TestContext.Current.CancellationToken).AsTask());

        var (bom, token) = MakeBom(signer, "bom-after-failed-scope", 1, null);
        var receipt = await Task.Run(() => authority.Activate(Device, bom, token))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("activation", receipt.ReceiptKind);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task CancelledRecoveryWaitDoesNotLeakTheTransitionPrimitive()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-cancelled-wait", 1, null);
        authority.Activate(Device, bom, token);
        var firstScope = await authority.RecoveryCapability.AcquireAsync(
            Device,
            TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => authority.RecoveryCapability.AcquireAsync(
                Device, cancelled.Token).AsTask());

        await firstScope.DisposeAsync();
        var receipt = await Task.Run(() => authority.Revoke(Device, 1))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("revocation", receipt.ReceiptKind);
    }

    [Fact, Trait("Category", "Unit")]
    public void SecondActivationIsMonotonicAndHidesPreviousToken()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);

        var receipt = ActivateNext(authority, signer, Device, second, first, secondToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(2, binding!.Generation);
        Assert.Equal(2, binding.ReleaseBomGeneration);
        Assert.Equal(secondToken, binding.ExecutionTokenBase64);
        Assert.NotEqual(firstToken, binding.ExecutionTokenBase64);
        Assert.NotNull(receipt.From);
        Assert.Equal("previous", receipt.From!.Status);
        Assert.Equal(Sha256Hex(first), receipt.From.ReleaseBomSha256);
        Assert.Equal("active", receipt.To.Status);
        Assert.Equal(2, receipt.Sequence);
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokeFailsReaderClosedAndWritesVersionedReceipt()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var active));

        var receipt = authority.Revoke(Device, active!.Generation);
        Assert.False(authority.TryReadActive(Device, out var afterRevoke));
        Assert.Null(afterRevoke);
        Assert.Equal("revocation", receipt.ReceiptKind);
        Assert.Equal("active", receipt.From!.Status);
        Assert.Equal("revoked", receipt.To.Status);
        Assert.Equal(active.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal(active.Generation, receipt.To.Generation);
        Assert.Equal(2, receipt.Sequence);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackRestoresPreviousDigestWithNewGenerationAndSignerToken()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(authority, signer, Device, second, first, secondToken);
        Assert.True(authority.TryReadActive(Device, out var abandoned));

        var receipt = authority.Rollback(Device, firstToken);
        Assert.True(authority.TryReadActive(Device, out var restored));
        Assert.Equal(Sha256Hex(first), restored!.ReleaseBomSha256);
        Assert.Equal(3, restored.Generation);
        // Signer ordinal legitimately reverts; the runtime ordinal advances.
        Assert.Equal(1, restored.ReleaseBomGeneration);
        Assert.Equal(firstToken, restored.ExecutionTokenBase64);
        Assert.NotEqual(abandoned!.ExecutionTokenBase64, restored.ExecutionTokenBase64);
        Assert.Equal("rollback", receipt.ReceiptKind);
        Assert.Equal("revoked", receipt.From!.Status);
        Assert.Equal(abandoned.ReleaseBomSha256, receipt.From.ReleaseBomSha256);
        Assert.Equal("active", receipt.To.Status);
        Assert.Equal(3, receipt.To.Generation);
        Assert.Equal(3, receipt.Sequence);
        Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void ActivationOverRevokedRecordsRevokedFromAndNeverLaundersItToPrevious()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        var activated = ActivateNext(authority, signer, Device, second, first, secondToken);
        authority.Revoke(Device, activated.To.Generation);

        var (third, thirdToken) = MakeBom(signer, "bom-3", 3, second);
        var receipt = ActivateNext(authority, signer, Device, third, second, thirdToken);

        // The receipt tells the truth: the prior binding stays revoked, it is
        // not demoted to "previous".
        Assert.NotNull(receipt.From);
        Assert.Equal("revoked", receipt.From!.Status);
        Assert.Equal(2, receipt.From.Generation);
        // No rollback path survives across a revocation: neither the revoked
        // bom-2 nor the older bom-1 is reachable.
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device, secondToken));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device, firstToken));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(3, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackAwayFromRevokedActiveRestoresTheTruePrevious()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        var activated = ActivateNext(authority, signer, Device, second, first, secondToken);
        authority.Revoke(Device, activated.To.Generation);

        var receipt = authority.Rollback(Device, firstToken);

        Assert.Equal("revoked", receipt.From!.Status);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(first), binding!.ReleaseBomSha256);
        Assert.Equal(3, binding.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void GenerationIsStrictlyMonotonicAcrossManyActivations()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        byte[]? previous = null;
        for (var round = 1; round <= 5; round++)
        {
            var (bom, token) = MakeBom(signer, "bom-" + round, round, previous);
            if (previous is null)
            {
                authority.Activate(Device, bom, token);
            }
            else
            {
                ActivateNext(authority, signer, Device, bom, previous, token);
            }
            previous = bom;
            Assert.True(authority.TryReadActive(Device, out var binding));
            Assert.Equal(round, binding!.Generation);
            Assert.Equal(round, binding.ReleaseBomGeneration);
        }
        var receipts = authority.ReadReceipts(Device);
        Assert.Equal(5, receipts.Count);
        Assert.Equal(
            Enumerable.Range(1, 5).Select(static value => (long)value),
            receipts.Select(static receipt => receipt.Sequence));
    }

    [Fact, Trait("Category", "Unit")]
    public void ReceiptSequenceIsSharedAcrossAllKinds()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(authority, signer, Device, second, first, secondToken);
        authority.Rollback(Device, firstToken);
        var (third, thirdToken) = MakeBom(signer, "bom-3", 3, first);
        ActivateNext(authority, signer, Device, third, first, thirdToken);
        Assert.True(authority.TryReadActive(Device, out var latest));
        authority.Revoke(Device, latest!.Generation);

        var receipts = authority.ReadReceipts(Device);
        Assert.Equal(
            new[] { "activation", "activation", "rollback", "activation", "revocation" },
            receipts.Select(static receipt => receipt.ReceiptKind));
        Assert.Equal(
            new long[] { 1, 2, 3, 4, 5 },
            receipts.Select(static receipt => receipt.Sequence));
        Assert.All(receipts, static receipt =>
            Assert.Equal(receipt.ComputePayloadSha256(), receipt.PayloadSha256));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownDeviceReadsFailClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);

        Assert.False(authority.TryReadActive(Device, out var binding));
        Assert.Null(binding);
        Assert.Empty(authority.ReadReceipts(Device));
        Assert.Throws<ArgumentException>(() => authority.Revoke("not-a-device", 1));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Revoke(OtherDevice, 1));
    }

    // ----- signature and trust -----

    [Fact, Trait("Category", "Unit")]
    public void BadSignatureFailsClosedWithZeroStateResidue()
    {
        using var signer = new TestSigner();
        using var stranger = new TestSigner("stranger-key-v1", "stranger-controller");
        var authority = Authority(signer);
        // Signed by an untrusted RSA key but claiming the trusted key id.
        var forged = stranger.SignBom("bom-1", 1, Token("bom-1"), null, keyIdOverride: signer.KeyId);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, forged, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownKeyIdFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1", 1, Token("bom-1"), null, keyIdOverride: "unknown-key-v1");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void UnknownAlgorithmFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom("bom-1", 1, Token("bom-1"), null, algorithm: "rsa-sha256");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void WrongPurposeKeyIsNeverTrusted()
    {
        using var signer = new TestSigner();
        var parameters = signer.Rsa.ExportParameters(false);
        var policy = new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject
                {
                    ["key_id"] = signer.KeyId,
                    ["identity"] = signer.Identity,
                    ["algorithm"] = "rsa-pss-sha256",
                    ["modulus_hex"] = Convert.ToHexStringLower(parameters.Modulus!),
                    ["exponent"] = 65537,
                    ["purposes"] = new JsonArray("artifact")
                })
        };
        using var document = JsonDocument.Parse(policy.ToJsonString());

        // The parser refuses to yield any bom key from a wrong-purpose policy.
        Assert.Throws<ActiveReleaseBindingException>(
            () => ReleaseBomTrustKey.FromTrustPolicy(document.RootElement));

        // A key that adds "bom" to another purpose is not a BOM-only release
        // authority either. The external signer contract requires the exact
        // singleton purpose, so shared-purpose keys fail closed.
        policy["keys"]![0]!["purposes"] = new JsonArray("bom", "artifact");
        using var mixedDocument = JsonDocument.Parse(policy.ToJsonString());
        Assert.Throws<ActiveReleaseBindingException>(
            () => ReleaseBomTrustKey.FromTrustPolicy(mixedDocument.RootElement));
    }

    [Fact, Trait("Category", "Unit")]
    public void DirectTrustKeyConstructionCannotBypassBomKeyProfile()
    {
        using var signer = new TestSigner();
        var valid = signer.TrustKey;
        var invalidKeys = new[]
        {
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                valid.ModulusHex,
                valid.Exponent,
                "rsa-sha256",
                ["bom"]),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                valid.ModulusHex,
                valid.Exponent,
                ReleaseBomTrustKey.RequiredAlgorithm,
                ["artifact"]),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                valid.ModulusHex,
                valid.Exponent,
                ReleaseBomTrustKey.RequiredAlgorithm,
                ["bom", "artifact"]),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                valid.ModulusHex,
                valid.Exponent,
                ReleaseBomTrustKey.RequiredAlgorithm,
                ["bom", "bom"]),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                "0" + valid.ModulusHex,
                valid.Exponent),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                "A" + valid.ModulusHex[1..],
                valid.Exponent),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                new string('a', 510),
                valid.Exponent),
            new ReleaseBomTrustKey(
                valid.KeyId,
                valid.Identity,
                valid.ModulusHex,
                3)
        };

        Assert.All(
            invalidKeys,
            key => Assert.Throws<ActiveReleaseBindingException>(
                () => new ActiveReleaseBindingAuthority(
                    [key],
                    InMemoryReleaseBindingTruthStore.CreateTestOnly(),
                    () => Now)));
    }

    [Fact, Trait("Category", "Unit")]
    public void TrustPolicyParserAcceptsBomPurposeKeys()
    {
        using var signer = new TestSigner();
        var parameters = signer.Rsa.ExportParameters(false);
        var policy = new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject
                {
                    ["key_id"] = signer.KeyId,
                    ["identity"] = signer.Identity,
                    ["algorithm"] = "rsa-pss-sha256",
                    ["modulus_hex"] = Convert.ToHexStringLower(parameters.Modulus!),
                    ["exponent"] = 65537,
                    ["purposes"] = new JsonArray("bom")
                })
        };
        using var document = JsonDocument.Parse(policy.ToJsonString());
        var keys = ReleaseBomTrustKey.FromTrustPolicy(document.RootElement);
        var authority = new ActiveReleaseBindingAuthority(
            keys, InMemoryReleaseBindingTruthStore.CreateTestOnly(), () => Now);

        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(signer.Identity, binding!.SignerIdentity);
    }

    [Fact, Trait("Category", "Unit")]
    public void TamperedPayloadReplayFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        var text = Encoding.UTF8.GetString(bom).Replace("bom-1", "bom-9");
        var tampered = Encoding.UTF8.GetBytes(text);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, tampered, token));
        Assert.False(authority.TryReadActive(Device, out _));
        // The untampered original still verifies afterwards.
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out _));
    }

    // ----- F1: strict BOM shape and signer-committed token binding -----

    [Fact, Trait("Category", "Unit")]
    public void MissingTopLevelFieldFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload.Remove("risk"));

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void ExtraTopLevelFieldFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["surprise"] = true);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void WrongSchemaVersionFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["schema_version"] = "dps.release-bom/v2");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonSignedStatusFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["status"] = "STABLE");

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonPositiveSignerGenerationFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload => payload["release_bom_generation"] = 0);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void MalformedActivationTokenDigestFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var bom = signer.SignBom(
            "bom-1", 1, Token("bom-1"), null,
            mutateBeforeSign: static payload =>
                payload["activation_token_sha256"] = new string('C', 64));

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("bom-1")));
    }

    [Fact, Trait("Category", "Unit")]
    public void TokenNotMatchingSignerCommitmentFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, _) = MakeBom(signer, "bom-1", 1, null);

        // A perfectly canonical 32-byte token that is not the committed one.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, Token("some-other-token")));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void NonCanonicalExecutionTokenFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        // Wrong size (33 bytes).
        var oversized = Convert.ToBase64String(new byte[33]);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, oversized));
        // Non-canonical re-encoding of the right token (padding bits abuse).
        var nonCanonical = token[..^2] + "//";
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, nonCanonical));
        // Not base64 at all.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, bom, "!not-base64!"));
        Assert.False(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void SignerOrdinalAntiRollbackIsEnforced()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-5", 5, null);
        authority.Activate(Device, first, firstToken);

        // Same signer ordinal, different bytes: equivocation, fail-closed.
        var (conflict, conflictToken) = MakeBom(signer, "bom-5b", 5, first);
        var conflictError = Assert.Throws<ActiveReleaseBindingException>(
            () => ActivateNext(authority, signer, Device, conflict, first, conflictToken));
        Assert.Contains("conflicting re-submission", conflictError.Message, StringComparison.Ordinal);
        // Lower signer ordinal: anti-rollback, fail-closed.
        var (older, olderToken) = MakeBom(signer, "bom-4", 4, first);
        Assert.Throws<ActiveReleaseBindingException>(
            () => ActivateNext(authority, signer, Device, older, first, olderToken));
        // Strictly higher ordinal proceeds.
        var (next, nextToken) = MakeBom(signer, "bom-6", 6, first);
        ActivateNext(authority, signer, Device, next, first, nextToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(6, binding!.ReleaseBomGeneration);
    }

    [Fact, Trait("Category", "Unit")]
    public void PreviousStableBomChainIsEnforced()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        // First activation must carry a null previous chain.
        var withBogusPrevious = signer.SignBom(
            "bom-1", 1, Token("bom-1"), new string('e', 64));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, withBogusPrevious, Token("bom-1")));

        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var stable = signer.StableTwin(first);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);

        // A valid non-bootstrap candidate never degrades to a bootstrap-like
        // three-argument call: the exact externally signed STABLE wire is
        // mandatory and is part of the idempotency identity.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, second, secondToken));

        // The previous reference must bind this exact STABLE wire by both id
        // and digest.
        var wrongChain = signer.SignBom("bom-2", 2, Token("bom-2"), new string('e', 64));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                wrongChain,
                signer.StableTwin(first),
                Token("bom-2")));
        var nullChain = signer.SignBom("bom-2", 2, Token("bom-2"), null);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                nullChain,
                signer.StableTwin(first),
                Token("bom-2")));
        var wrongId = signer.SignBom(
            "bom-2-id",
            2,
            Token("bom-2-id"),
            Sha256Hex(stable),
            "different-stable-id");
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                wrongId,
                stable,
                Token("bom-2-id")));

        // SIGNED is never accepted in the STABLE evidence slot.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, second, first, secondToken));

        // A fresh, independently valid RSA-PSS signature over the same
        // lifecycle fields produces a different exact wire. The candidate
        // references the persisted first twin, so the alternative is not an
        // alias and must be rejected.
        var reSignedStable = signer.ResignStableTwin(first);
        Assert.NotEqual(Sha256Hex(stable), Sha256Hex(reSignedStable));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                second,
                reSignedStable,
                secondToken));

        // Noncanonical transport bytes and a canonical wire with a corrupted
        // signature both fail before any journal append.
        var indentedStable = Encoding.UTF8.GetBytes(
            JsonNode.Parse(stable)!.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                second,
                indentedStable,
                secondToken));
        var badSignatureNode = JsonNode.Parse(stable)!.AsObject();
        var signatureValue = badSignatureNode["signature"]!["value"]!.GetValue<string>();
        badSignatureNode["signature"]!["value"] =
            (signatureValue[0] == 'A' ? "B" : "A") + signatureValue[1..];
        using var badSignatureDocument =
            JsonDocument.Parse(badSignatureNode.ToJsonString());
        var badSignatureStable =
            ReleaseBomCanonicalJson.Serialize(badSignatureDocument.RootElement);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                second,
                badSignatureStable,
                secondToken));

        Assert.Single(store.LoadAll());
        ActivateNext(authority, signer, Device, second, first, secondToken);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
        Assert.Equal(stable, store.LoadAll()[1].PreviousStableBomBytes);
    }

    [Fact, Trait("Category", "Unit")]
    public void PreviousStableBomMustBeTheCurrentSignedLifecycleTwin()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);

        var driftedStable = signer.ResignStableTwin(
            first,
            static payload =>
                payload["feature_flags"] =
                    new JsonObject { ["drifted_after_activation"] = true });
        var (candidate, candidateToken) = MakeBomFromStable(
            signer,
            "bom-2-drift",
            2,
            driftedStable);

        var error = Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device,
                candidate,
                driftedStable,
                candidateToken));
        Assert.Contains("lifecycle twin", error.Message, StringComparison.Ordinal);
        Assert.Single(store.LoadAll());
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(first), binding!.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void BootstrapActivationRejectsAnyPreviousStableWire()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        var stable = signer.StableTwin(first);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, first, stable, firstToken));
        Assert.Empty(store.LoadAll());

        authority.Activate(Device, first, firstToken);
        Assert.Single(store.LoadAll());
    }

    [Fact, Trait("Category", "Unit")]
    public void RevokeWrongGenerationFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Revoke(Device, 2));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal("active", binding!.Status);
        Assert.Single(authority.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackWithoutPreviousFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);

        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(Device, token));
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Rollback(OtherDevice, token));
    }

    [Fact, Trait("Category", "Unit")]
    public void RollbackWithWrongTokenFailsClosed()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(authority, signer, Device, second, first, secondToken);

        // The active BOM's token is not the previous binding's commitment.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Rollback(Device, secondToken));
        // An arbitrary canonical token is not either.
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Rollback(Device, Token("unrelated")));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
    }

    // ----- F2: recoverable state -----

    [Fact, Trait("Category", "Unit")]
    public void RecoveryFromStoreRestoresBindingsTokensAndCounters()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var first = Authority(signer, store);
        var (bom1, token1) = MakeBom(signer, "bom-1", 1, null);
        first.Activate(Device, bom1, token1);
        var (bom2, token2) = MakeBom(signer, "bom-2", 2, bom1);
        ActivateNext(first, signer, Device, bom2, bom1, token2);

        var recovered = Authority(signer, store);
        Assert.True(recovered.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(bom2), binding!.ReleaseBomSha256);
        Assert.Equal(2, binding.Generation);
        Assert.Equal(token2, binding.ExecutionTokenBase64);
        Assert.Equal(2, recovered.ReadReceipts(Device).Count);

        // Response-loss retry after restart: the exact candidate, exact
        // previous STABLE wire, and exact token reproduce the committed
        // request identity and return the original receipt without append.
        var activationReplay = recovered.Activate(
            Device,
            bom2,
            signer.StableTwin(bom1),
            token2);
        Assert.Equal(store.LoadAll()[1].Receipt, activationReplay);
        Assert.Equal(2, store.LoadAll().Count);

        // Counters continue: rollback still reaches bom-1, sequence goes 3,
        // and no receipt id repeats.
        var receipt = recovered.Rollback(Device, token1);
        Assert.Equal(3, receipt.Sequence);
        Assert.Equal(3, receipt.To.Generation);
        var ids = recovered.ReadReceipts(Device).Select(static value => value.ReceiptId).ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        // Signer anti-rollback survives recovery: ordinal 2 is spent.
        var (stale, staleToken) = MakeBom(signer, "bom-2x", 2, bom1);
        Assert.Throws<ActiveReleaseBindingException>(
            () => ActivateNext(recovered, signer, Device, stale, bom1, staleToken));
    }

    [Fact, Trait("Category", "Unit")]
    public void RecoveryAfterRevokeKeepsReaderClosed()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var first = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        first.Activate(Device, bom, token);
        first.Revoke(Device, 1);

        var recovered = Authority(signer, store);
        Assert.False(recovered.TryReadActive(Device, out _));
        Assert.Equal(2, recovered.ReadReceipts(Device).Count);
        Assert.Throws<ActiveReleaseBindingException>(() => recovered.Rollback(Device, token));
    }

    [Fact, Trait("Category", "Unit")]
    public void ForkedOrRegressedJournalRefusesService()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom1, token1) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var (bom2, token2) = MakeBom(signer, "bom-2", 2, bom1);
        ActivateNext(authority, signer, Device, bom2, bom1, token2);
        var records = store.LoadAll();

        // Sequence gap (dropped first record).
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([records[1]])));
        // Duplicated record (replayed receipt identity).
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([records[0], records[0]])));
        // Tampered receipt payload digest: rejected by
        // ReleaseBindingReceiptV1.Validate itself (fixed-time digest check),
        // which recovery invokes on every journal receipt.
        var tampered = records[1] with
        {
            Receipt = records[1].Receipt with { PayloadSha256 = new string('0', 64) }
        };
        Assert.Throws<ArgumentException>(
            () => Authority(signer, new FrozenTruthStore([records[0], tampered])));
    }

    // ----- F3: idempotency and concurrency -----

    [Fact, Trait("Category", "Unit")]
    public void IdenticalActivateResubmissionReturnsOriginalReceiptWithoutStateChange()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        var original = authority.Activate(Device, bom, token);

        var replay = authority.Activate(Device, bom, token);
        Assert.Equal(original, replay);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.Single(store.LoadAll());
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(1, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void ActivationRequestIdentityBindsTheDevice()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-shared-device", 1, null);

        authority.Activate(Device, bom, token);
        authority.Activate(OtherDevice, bom, token);

        var records = store.LoadAll();
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].RequestSha256, records[1].RequestSha256);
        var restarted = Authority(signer, store);
        Assert.True(restarted.TryReadActive(Device, out _));
        Assert.True(restarted.TryReadActive(OtherDevice, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void IdenticalRevokeAndRollbackResubmissionsAreIdempotent()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom1, token1) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom1, token1);
        var (bom2, token2) = MakeBom(signer, "bom-2", 2, bom1);
        ActivateNext(authority, signer, Device, bom2, bom1, token2);

        var rollback = authority.Rollback(Device, token1);
        var rollbackReplay = authority.Rollback(Device, token1);
        Assert.Equal(rollback, rollbackReplay);

        Assert.True(authority.TryReadActive(Device, out var active));
        var revoke = authority.Revoke(Device, active!.Generation);
        var revokeReplay = authority.Revoke(Device, active.Generation);
        Assert.Equal(revoke, revokeReplay);
        Assert.Equal(4, authority.ReadReceipts(Device).Count);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ConcurrentFirstActivationNeverForksGenerationOne()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bomA, tokenA) = MakeBom(signer, "bom-a", 1, null);
        var (bomB, tokenB) = MakeBom(signer, "bom-b", 1, null);

        using var barrier = new Barrier(2);
        var outcomes = new object?[2];
        void Run(int slot, byte[] bom, string token)
        {
            barrier.SignalAndWait();
            try
            {
                outcomes[slot] = authority.Activate(Device, bom, token);
            }
            catch (ActiveReleaseBindingException exception)
            {
                outcomes[slot] = exception;
            }
        }
        await Task.WhenAll(
            Task.Run(() => Run(0, bomA, tokenA), TestContext.Current.CancellationToken),
            Task.Run(() => Run(1, bomB, tokenB), TestContext.Current.CancellationToken));

        // Exactly one contender wins generation 1; the other fails closed on
        // the spent signer ordinal. Never two forked generation-1 bindings.
        var receipts = outcomes.OfType<ReleaseBindingReceiptV1>().ToArray();
        var failures = outcomes.OfType<ActiveReleaseBindingException>().ToArray();
        Assert.Single(receipts);
        Assert.Single(failures);
        Assert.Equal(1, receipts[0].To.Generation);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(1, binding!.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public async Task ConcurrentIdenticalActivationsConvergeOnOneReceipt()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        using var barrier = new Barrier(2);
        var receipts = new ReleaseBindingReceiptV1[2];
        void Run(int slot)
        {
            barrier.SignalAndWait();
            receipts[slot] = authority.Activate(Device, bom, token);
        }
        await Task.WhenAll(
            Task.Run(() => Run(0), TestContext.Current.CancellationToken),
            Task.Run(() => Run(1), TestContext.Current.CancellationToken));

        Assert.Equal(receipts[0], receipts[1]);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.Single(store.LoadAll());
    }

    // ----- second adversarial review: F1-F3 regressions -----

    [Fact, Trait("Category", "Unit")]
    public void SupersededExactRequestsAreRejectedForEveryTransitionKind()
    {
        using var signer = new TestSigner();

        // Activation: the exact activation result was superseded by
        // revocation. Redelivery cannot reactivate the old candidate.
        var activationStore = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var activationAuthority = Authority(signer, activationStore);
        var (activationBom, activationToken) =
            MakeBom(signer, "activation-a", 1, null);
        activationAuthority.Activate(
            Device,
            activationBom,
            activationToken);
        activationAuthority.Revoke(Device, 1);

        Assert.Throws<ActiveReleaseBindingException>(
            () => activationAuthority.Activate(
                Device,
                activationBom,
                activationToken));
        Assert.Equal(2, activationStore.LoadAll().Count);
        Assert.Equal(2, activationAuthority.ReadReceipts(Device).Count);
        Assert.False(
            activationAuthority.TryReadActive(
                Device,
                out var revokedActive));
        Assert.Null(revokedActive);
        var revokedEndpoint =
            activationAuthority.ReadReceipts(Device)[^1].To;
        Assert.Equal("revoked", revokedEndpoint.Status);
        Assert.Equal(1, revokedEndpoint.Generation);

        // Revocation: a later activation superseded the committed revoked
        // postcondition. Redelivery of the old generation cannot revoke the
        // new binding.
        var revocationStore = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var revocationAuthority = Authority(signer, revocationStore);
        var (revocationFirst, revocationFirstToken) =
            MakeBom(signer, "revocation-a", 1, null);
        revocationAuthority.Activate(
            Device,
            revocationFirst,
            revocationFirstToken);
        revocationAuthority.Revoke(Device, 1);
        var (revocationSecond, revocationSecondToken) =
            MakeBom(signer, "revocation-b", 2, revocationFirst);
        ActivateNext(
            revocationAuthority,
            signer,
            Device,
            revocationSecond,
            revocationFirst,
            revocationSecondToken);

        Assert.Throws<ActiveReleaseBindingException>(
            () => revocationAuthority.Revoke(Device, 1));
        Assert.Equal(3, revocationStore.LoadAll().Count);
        Assert.Equal(3, revocationAuthority.ReadReceipts(Device).Count);
        Assert.True(revocationAuthority.TryReadActive(Device, out var active));
        Assert.Equal(Sha256Hex(revocationSecond), active!.ReleaseBomSha256);
        Assert.Equal(2, active.Generation);

        // Rollback is the critical reachable replay: A -> B -> rollback A ->
        // activate C makes A a valid previous binding again. The old token-A
        // bytes are still the same committed request, not a fresh intent.
        var rollbackStore = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, rollbackStore);
        var (first, firstToken) = MakeBom(signer, "bom-a", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-b", 2, first);
        ActivateNext(authority, signer, Device, second, first, secondToken);
        authority.Rollback(Device, firstToken);
        // A newer BOM supersedes the rolled-back binding (and legitimately
        // demotes it to previous again).
        var (third, thirdToken) = MakeBom(signer, "bom-c", 3, first);
        ActivateNext(authority, signer, Device, third, first, thirdToken);

        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Rollback(Device, firstToken));
        Assert.Equal(4, rollbackStore.LoadAll().Count);
        Assert.Equal(4, authority.ReadReceipts(Device).Count);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(third), binding!.ReleaseBomSha256);
        Assert.Equal(4, binding.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void ReencodedSignedBomVariantsAreRejected()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        // Same signed payload, fields re-ordered (compact but unsorted).
        var reordered = new JsonObject();
        foreach (var pair in JsonNode.Parse(bom)!.AsObject().ToArray().Reverse())
        {
            reordered[pair.Key] = pair.Value?.DeepClone();
        }
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(
            Device, Encoding.UTF8.GetBytes(reordered.ToJsonString()), token));

        // Same signed payload with whitespace (indented re-encoding).
        var indented = JsonNode.Parse(bom)!.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true });
        Assert.Throws<ActiveReleaseBindingException>(() => authority.Activate(
            Device, Encoding.UTF8.GetBytes(indented), token));

        // Signature value with an embedded newline: base64 decoding ignores
        // whitespace so the signature still verifies; only the canonical
        // base64 re-encoding check rejects it.
        var mutated = JsonNode.Parse(bom)!.AsObject();
        var signatureObject = mutated["signature"]!.AsObject();
        signatureObject["value"] =
            signatureObject["value"]!.GetValue<string>().Insert(10, "\n");
        using var mutatedDocument = JsonDocument.Parse(mutated.ToJsonString());
        var newlineVariant = ReleaseBomCanonicalJson.Serialize(mutatedDocument.RootElement);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, newlineVariant, token));

        // Non-zero Base64 pad bits decode to the exact same RSA signature
        // bytes. The canonical re-encoding guard still rejects the alias.
        mutated = JsonNode.Parse(bom)!.AsObject();
        signatureObject = mutated["signature"]!.AsObject();
        signatureObject["value"] = NonCanonicalBase64PadAlias(
            signatureObject["value"]!.GetValue<string>());
        using var padAliasDocument = JsonDocument.Parse(mutated.ToJsonString());
        var padAliasVariant = ReleaseBomCanonicalJson.Serialize(
            padAliasDocument.RootElement);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, padAliasVariant, token));

        var representativeAlias = BuildRsaRepresentativeAliasBom(
            signer,
            () => signer.SignBom("bom-1", 1, token, null),
            shortI2osp: false);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, representativeAlias, token));

        var shortRepresentative = BuildRsaRepresentativeAliasBom(
            signer,
            () => signer.SignBom("bom-1", 1, token, null),
            shortI2osp: true);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, shortRepresentative, token));

        // Zero state residue, and the true canonical wire still activates.
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void CanonicalBomOrderingMatchesPythonUnicodeScalarOrdering()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var token = Token("unicode-scalar-order");
        var bom = signer.SignBom(
            "bom-unicode-scalar-order",
            1,
            token,
            null,
            mutateBeforeSign: payload =>
            {
                payload["feature_flags"] = new JsonObject
                {
                    ["\uE000"] = true,
                    ["\U00010000"] = false
                };
            });

        var wire = Encoding.UTF8.GetString(bom);
        Assert.Contains(
            "\"\uE000\":true,\"\U00010000\":false",
            wire,
            StringComparison.Ordinal);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out _));
    }

    [Fact, Trait("Category", "Unit")]
    public void TamperedRecoveryBindingsRefuseService()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(authority, signer, Device, second, first, secondToken);
        var records = store.LoadAll();
        var head = records[0];

        // 1. Current binding digest no longer matches the recorded BOM.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with
            {
                CurrentBinding = head.CurrentBinding with { ReleaseBomSha256 = new string('f', 64) }
            }])));
        // 2. Signer identity swapped under the same signed BOM.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with
            {
                CurrentBinding = head.CurrentBinding with { SignerIdentity = "stranger-controller" }
            }])));
        // 3. Execution token swapped for a self-consistent but uncommitted one.
        var evilToken = Token("evil");
        var evilTokenSha = Convert.ToHexStringLower(
            SHA256.HashData(Convert.FromBase64String(evilToken)));
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with
            {
                CurrentBinding = head.CurrentBinding with
                {
                    ExecutionTokenBase64 = evilToken,
                    ActivationTokenSha256 = evilTokenSha
                }
            }])));
        // 4. Previous binding dropped from an activation over an active one.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([records[0], records[1] with { PreviousBinding = null }])));
        // 5. Recorded signed BOM bytes tampered.
        var tamperedBytes = head.SignedBomBytes!.ToArray();
        tamperedBytes[^20] ^= 0x01;
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore([head with { SignedBomBytes = tamperedBytes }])));
        // 6. The exact previous STABLE wire is independently journal-bound.
        var stableTamper = records[1].PreviousStableBomBytes!.ToArray();
        stableTamper[^20] ^= 0x01;
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore(
                [records[0], records[1] with { PreviousStableBomBytes = stableTamper }])));
        // 7. Request identity is recomputed from candidate + stable + token;
        // a syntactically valid replacement digest cannot survive recovery.
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore(
                [records[0], records[1] with { RequestSha256 = new string('e', 64) }])));

        // The untampered journal still recovers.
        var recovered = Authority(signer, new FrozenTruthStore(records));
        Assert.True(recovered.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Unit")]
    public void BindingToStringRedactsTheExecutionToken()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var binding));

        var printed = binding!.ToString();
        Assert.DoesNotContain(token, printed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", printed, StringComparison.Ordinal);
        // Redaction is print-only: value equality still covers the token.
        Assert.Equal(binding, binding with { });
        Assert.NotEqual(binding, binding with { ExecutionTokenBase64 = Token("other") });
    }

    [Fact, Trait("Category", "Unit")]
    public void RecoveryRejectsPreviousStableWireOutsideNonBootstrapActivation()
    {
        using var signer = new TestSigner();
        var rollbackStore = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var rollbackAuthority = Authority(signer, rollbackStore);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        rollbackAuthority.Activate(Device, first, firstToken);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(
            rollbackAuthority,
            signer,
            Device,
            second,
            first,
            secondToken);
        rollbackAuthority.Rollback(Device, firstToken);
        var rollbackRecords = rollbackStore.LoadAll();
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore(
                [
                    rollbackRecords[0],
                    rollbackRecords[1],
                    rollbackRecords[2] with
                    {
                        PreviousStableBomBytes = signer.StableTwin(first)
                    }
                ])));

        var revokeStore = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var revokeAuthority = Authority(signer, revokeStore);
        var (revokeBom, revokeToken) = MakeBom(
            signer,
            "bom-revoke",
            1,
            null);
        revokeAuthority.Activate(Device, revokeBom, revokeToken);
        revokeAuthority.Revoke(Device, 1);
        var revokeRecords = revokeStore.LoadAll();
        Assert.Throws<ActiveReleaseBindingException>(() => Authority(
            signer,
            new FrozenTruthStore(
                [
                    revokeRecords[0],
                    revokeRecords[1] with
                    {
                        PreviousStableBomBytes = signer.StableTwin(revokeBom)
                    }
                ])));
    }

    [Fact, Trait("Category", "Unit")]
    public void StoreAppendRejectsForkedOrNonContiguousSequences()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();

        // Two authority instances sharing one store: both begin with an empty
        // cache. The second transition resyncs the winner before append, then
        // rejects its different same-generation request instead of forking the
        // journal or mistaking it for an exact replay.
        var left = Authority(signer, store);
        var right = Authority(signer, store);
        var (leftBom, leftToken) = MakeBom(signer, "bom-1", 1, null);
        left.Activate(Device, leftBom, leftToken);
        var (rightBom, rightToken) = MakeBom(signer, "bom-1b", 1, null);
        Assert.Throws<ActiveReleaseBindingException>(
            () => right.Activate(Device, rightBom, rightToken));

        // The losing instance made no visible change and the journal holds
        // exactly the winner's record.
        var records = store.LoadAll();
        var record = Assert.Single(records);
        Assert.Equal(Sha256Hex(leftBom), record.CurrentBinding.ReleaseBomSha256);
        // Revision-aware reads: the loser's cached empty view is stale, so
        // its next read resyncs from the shared journal and serves the
        // winner's binding — it never reports the superseded empty state.
        Assert.True(right.TryReadActive(Device, out var resynced));
        Assert.Equal(Sha256Hex(leftBom), resynced!.ReleaseBomSha256);
        Assert.Equal(record.Receipt, Assert.Single(right.ReadReceipts(Device)));

        // Direct store misuse: skipping ahead or replaying a sequence faults.
        Assert.Throws<ReleaseBindingTruthConflictException>(
            () => store.Append(record with
            {
                Receipt = record.Receipt with
                {
                    Sequence = 3,
                    PayloadSha256 = (record.Receipt with { Sequence = 3 }).ComputePayloadSha256()
                }
            }));
        Assert.Throws<ReleaseBindingTruthConflictException>(() => store.Append(record));
    }

    // ----- third adversarial review: F1-F4 regressions -----

    private sealed class FailingAppendStore
        : IReleaseBindingTruthStore,
          IActiveReleaseBindingRecoveryCoordinator
    {
        private readonly InMemoryReleaseBindingTruthStore _inner = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        public bool FailNextAppend { get; set; }

        public void Append(ReleaseBindingTruthRecord record)
        {
            if (FailNextAppend)
            {
                throw new InvalidOperationException("truth store append refused");
            }
            _inner.Append(record);
        }

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll() => _inner.LoadAll();

        public long LoadDeviceHeadSequence(string deviceBindingId)
            => _inner.LoadDeviceHeadSequence(deviceBindingId);

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
            string deviceBindingId,
            long afterSequence)
            => _inner.LoadAfter(deviceBindingId, afterSequence);

        public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
            string deviceBindingId,
            long afterSequence)
            => _inner.LoadSnapshotAfter(deviceBindingId, afterSequence);

        public ValueTask<IActiveReleaseBindingRecoveryScope> AcquireAsync(
            string deviceBindingId,
            CancellationToken cancellationToken)
            => ((IActiveReleaseBindingRecoveryCoordinator)_inner).AcquireAsync(
                deviceBindingId, cancellationToken);
    }

    private sealed class FixedBindingReader(ActiveReleaseBindingV1? binding) : IActiveReleaseBindingReader
    {
        public bool TryReadActive(string deviceBindingId, out ActiveReleaseBindingV1? read)
        {
            read = binding;
            return binding is not null;
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void AppendFailureLeavesZeroVisibleChange()
    {
        using var signer = new TestSigner();
        var store = new FailingAppendStore { FailNextAppend = true };
        var authority = new ActiveReleaseBindingAuthority([signer.TrustKey], store, () => Now);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);

        // Append fails: nothing is published anywhere.
        Assert.Throws<InvalidOperationException>(() => authority.Activate(Device, bom, token));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));
        Assert.Empty(store.LoadAll());

        // The idempotency table is unpolluted: the identical request now
        // succeeds as a first-time activation.
        store.FailNextAppend = false;
        var receipt = authority.Activate(Device, bom, token);
        Assert.Equal(1, receipt.To.Generation);
        Assert.Single(authority.ReadReceipts(Device));
        Assert.Single(store.LoadAll());

        // Same discipline on revocation: a failed append leaves the binding
        // active and a later revoke still works.
        store.FailNextAppend = true;
        Assert.Throws<InvalidOperationException>(() => authority.Revoke(Device, 1));
        Assert.True(authority.TryReadActive(Device, out var stillActive));
        Assert.Equal("active", stillActive!.Status);
        store.FailNextAppend = false;
        authority.Revoke(Device, 1);
        Assert.False(authority.TryReadActive(Device, out _));
    }

    private static string ForgedReceiptId(string device, long sequence)
        => "receipt_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "dps.release.binding.receipt/v1\n" + device + "\n" + sequence)))[..32];

    /// <summary>
    /// Builds a journal activation record from a VALIDLY SIGNED BOM whose
    /// receipt/binding pass every structural and cryptographic recovery
    /// check — only the previous-chain invariant can reject it.
    /// </summary>
    private static ReleaseBindingTruthRecord ForgeActivationRecord(
        TestSigner signer,
        string device,
        long sequence,
        long runtimeGeneration,
        byte[] bomBytes,
        string token,
        ReleaseBindingEndpointV1? from,
        ActiveReleaseBindingV1? previousBinding,
        byte[]? previousStableBomBytes = null)
    {
        using var document = JsonDocument.Parse(bomBytes);
        var root = document.RootElement;
        var signerGeneration = root.GetProperty("release_bom_generation").GetInt64();
        var activationTokenSha = root.GetProperty("activation_token_sha256").GetString()!;
        var signatureSha = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(
            root.GetProperty("signature").GetProperty("value").GetString()!)));
        var bomSha = Sha256Hex(bomBytes);
        var receiptId = ForgedReceiptId(device, sequence);
        var binding = new ActiveReleaseBindingV1(
            "1.0.0", "active.release.binding/v1", "control-plane-host",
            device, bomSha, runtimeGeneration, signerGeneration, token, activationTokenSha,
            "active", signer.Identity, signer.KeyId, signatureSha, Now, receiptId,
            SoulId: null, PlatformAccountId: null, TraceId: null, IdempotencyKey: null,
            OccurredAt: Now, PrivacyClass: "internal");
        var unhashed = new ReleaseBindingReceiptV1(
            "1.0.0", "release.binding.receipt/v1", "control-plane-host",
            "activation", device, from,
            new ReleaseBindingEndpointV1(bomSha, runtimeGeneration, "active"),
            sequence, signer.Identity, Now, new string('0', 64), receiptId,
            SoulId: null, PlatformAccountId: null, TraceId: null, IdempotencyKey: null,
            PrivacyClass: "internal");
        var receipt = unhashed with { PayloadSha256 = unhashed.ComputePayloadSha256() };
        return new ReleaseBindingTruthRecord(
            device, receipt, binding, previousBinding, signerGeneration,
            ActivationRequestSha256(
                device,
                bomBytes,
                previousStableBomBytes is null
                    ? ReadOnlySpan<byte>.Empty
                    : previousStableBomBytes,
                token),
            bomBytes,
            previousStableBomBytes);
    }

    [Fact, Trait("Category", "Unit")]
    public void RecoveryRejectsValidlySignedBomsAtTheWrongChainPosition()
    {
        using var signer = new TestSigner();

        // 1. Bootstrap: a signed BOM carrying a non-null previous chain
        //    journaled as the device's first activation.
        var chained = signer.SignBom("bom-x", 1, Token("bom-x"), new string('e', 64));
        var bootstrapForgery = ForgeActivationRecord(
            signer, Device, sequence: 1, runtimeGeneration: 1, chained, Token("bom-x"),
            from: null, previousBinding: null);
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([bootstrapForgery])));

        // 2. Mid-chain: second activation whose signed chain digest is not
        //    the prior binding's digest.
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var live = Authority(signer, store);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        live.Activate(Device, first, firstToken);
        var records = store.LoadAll();
        var wrongChain = signer.SignBom("bom-2x", 2, Token("bom-2x"), new string('e', 64));
        var midChainForgery = ForgeActivationRecord(
            signer, Device, sequence: 2, runtimeGeneration: 2, wrongChain, Token("bom-2x"),
            from: new ReleaseBindingEndpointV1(Sha256Hex(first), 1, "previous"),
            previousBinding: records[0].CurrentBinding with { Status = "previous" },
            previousStableBomBytes: signer.StableTwin(first));
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([records[0], midChainForgery])));

        // 3. After revocation: activation-over-revoked journaled with a
        //    bootstrap-shaped (null-chain) signed BOM.
        live.Revoke(Device, 1);
        var afterRevoke = store.LoadAll();
        var nullChain = signer.SignBom("bom-3", 2, Token("bom-3"), null);
        var revokeForgery = ForgeActivationRecord(
            signer, Device, sequence: 3, runtimeGeneration: 2, nullChain, Token("bom-3"),
            from: new ReleaseBindingEndpointV1(Sha256Hex(first), 1, "revoked"),
            previousBinding: null,
            previousStableBomBytes: signer.StableTwin(first));
        Assert.Throws<ActiveReleaseBindingException>(
            () => Authority(signer, new FrozenTruthStore([.. afterRevoke, revokeForgery])));
    }

    [Fact, Trait("Category", "Unit")]
    public void FloatFeatureFlagsActivateButReencodedFloatFailsSignature()
    {
        using var signer = new TestSigner();
        var authority = Authority(signer);
        var token = Token("bom-float");
        var bom = signer.SignBom(
            "bom-float", 1, token, null,
            mutateBeforeSign: static payload =>
                payload["feature_flags"] = new JsonObject { ["shadow_ratio"] = 0.5 });

        // Sign the non-canonical alias itself. A raw-number pass-through
        // verifier would accept this signature, so rejection proves the
        // Python-compatible canonical-number guard is independently active.
        using var canonicalDocument = JsonDocument.Parse(bom);
        var canonicalPayload = ReleaseBomCanonicalJson.SerializeObjectWithout(
            canonicalDocument.RootElement, "signature");
        var aliasPayload = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(canonicalPayload).Replace("0.5", "5e-1"));
        var aliasMessage = Encoding.ASCII.GetBytes("dps-release-bom/v1\n")
            .Concat(aliasPayload).ToArray();
        var aliasSignature = Convert.ToBase64String(
            signer.Rsa.SignData(
                aliasMessage, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.True(signer.Rsa.VerifyData(
            aliasMessage,
            Convert.FromBase64String(aliasSignature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
        var originalSignature = canonicalDocument.RootElement
            .GetProperty("signature").GetProperty("value").GetString()!;
        var reencoded = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(bom)
                .Replace("0.5", "5e-1")
                .Replace(originalSignature, aliasSignature));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, reencoded, token));
        Assert.False(authority.TryReadActive(Device, out _));
        Assert.Empty(authority.ReadReceipts(Device));

        // The legally signed float BOM activates.
        var receipt = authority.Activate(Device, bom, token);
        Assert.Equal(1, receipt.To.Generation);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(bom), binding!.ReleaseBomSha256);
    }

    [Fact, Trait("Category", "Contract")]
    public void CanonicalNumberCorpusMatchesCandidateValidator()
    {
        const string resource =
            "Dps.ControlPlaneHost.Tests.release-bom.canonical-number.v1.corpus.json";
        var corpusBytes = LoadEmbeddedResource(resource);
        Assert.Equal(
            NumberCorpusSha256,
            Convert.ToHexStringLower(SHA256.HashData(corpusBytes)));
        using var corpus = JsonDocument.Parse(corpusBytes);
        Assert.Equal(
            "dps.release-bom-canonical-number-corpus/v1",
            corpus.RootElement.GetProperty("schema_version").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(62, cases.Length);
        Assert.Equal(18, cases.Count(item => item.GetProperty("outcome").GetString() == "accept"));
        Assert.Equal(35, cases.Count(item => item.GetProperty("outcome").GetString() == "normalize"));
        Assert.Equal(9, cases.Count(item => item.GetProperty("outcome").GetString() == "reject"));
        Assert.All(cases, item => Assert.Contains(
            item.GetProperty("outcome").GetString(),
            new[] { "accept", "normalize", "reject" }));

        foreach (var item in cases)
        {
            var wire = item.GetProperty("wire").GetString()!;
            var outcome = item.GetProperty("outcome").GetString()!;
            if (outcome == "reject")
            {
                var exception = Record.Exception(() => CanonicalizeNumber(wire));
                Assert.NotNull(exception);
                Assert.True(
                    exception is JsonException or ActiveReleaseBindingException,
                    $"Unexpected exception type: {exception.GetType().FullName}");
                continue;
            }

            var canonical = item.GetProperty("canonical").GetString()!;
            Assert.Equal("{\"n\":" + canonical + "}", CanonicalizeNumber(wire));
            Assert.Equal(outcome == "accept", wire == canonical);
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void CanonicalStringCorpusMatchesCandidateValidator()
    {
        const string resource =
            "Dps.ControlPlaneHost.Tests.release-bom.canonical-string.v1.corpus.json";
        var corpusBytes = LoadEmbeddedResource(resource);
        Assert.Equal(
            StringCorpusSha256,
            Convert.ToHexStringLower(SHA256.HashData(corpusBytes)));
        using var corpus = JsonDocument.Parse(corpusBytes);
        Assert.Equal(
            "dps.release-bom-canonical-string-corpus/v1",
            corpus.RootElement.GetProperty("schema_version").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(4, cases.Length);
        Assert.Equal(4, cases.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        foreach (var item in cases)
        {
            var wire = Convert.FromBase64String(
                item.GetProperty("wire_base64").GetString()!);
            var expected = Convert.FromBase64String(
                item.GetProperty("canonical_base64").GetString()!);
            using var value = JsonDocument.Parse(wire);
            Assert.Equal(expected, ReleaseBomCanonicalJson.Serialize(value.RootElement));
        }
    }

    [Fact, Trait("Category", "Contract")]
    public void FullCandidateValidatorCorpusActivatesExactStableTwin()
    {
        const string prefix =
            "Dps.ControlPlaneHost.Tests.release-binding-compat.";
        var metadataBytes = LoadEmbeddedResource(prefix + "corpus.json");
        var policyBytes = LoadEmbeddedResource(prefix + "trust-policy.json");
        var tokenBytes = LoadEmbeddedResource(prefix + "token-preimages.json");
        var candidateBytes = LoadEmbeddedResource(prefix + "candidate-bom.json");
        var previousSignedBytes =
            LoadEmbeddedResource(prefix + "previous-signed-bom.json");
        var previousStableBytes =
            LoadEmbeddedResource(prefix + "previous-stable-bom.json");

        using var metadata = JsonDocument.Parse(metadataBytes);
        Assert.Equal(
            "dps.r0c-release-binding-compat-corpus/v1",
            metadata.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(
            "generated once with a 2048-bit test-only RSA key held only in volatile "
            + "process memory; the signing component was discarded and is absent "
            + "from this corpus and repository",
            metadata.RootElement
                .GetProperty("ephemeral_signing_component_disposition")
                .GetString());
        AssertCompatCorpusFile(
            metadata.RootElement, "trust-policy.json", policyBytes);
        AssertCompatCorpusFile(
            metadata.RootElement, "token-preimages.json", tokenBytes);
        AssertCompatCorpusFile(
            metadata.RootElement, "bundle/candidate-bom.json", candidateBytes);
        AssertCompatCorpusFile(
            metadata.RootElement,
            "bundle/previous-signed-bom.json",
            previousSignedBytes);
        AssertCompatCorpusFile(
            metadata.RootElement,
            "bundle/previous-stable-bom.json",
            previousStableBytes);

        using var policy = JsonDocument.Parse(policyBytes);
        var trustKey = Assert.Single(
            ReleaseBomTrustKey.FromTrustPolicy(policy.RootElement));
        var publicKey =
            metadata.RootElement.GetProperty("controller_public_key");
        Assert.Equal(publicKey.GetProperty("key_id").GetString(), trustKey.KeyId);
        Assert.Equal(
            publicKey.GetProperty("identity").GetString(),
            trustKey.Identity);
        Assert.Equal(
            publicKey.GetProperty("modulus_hex").GetString(),
            trustKey.ModulusHex);
        Assert.Equal(65537, trustKey.Exponent);
        Assert.Equal(
            256,
            publicKey.GetProperty("unsigned_modulus_octets").GetInt32());
        Assert.Equal(
            ["bom"],
            publicKey.GetProperty("purposes")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray());

        using var tokens = JsonDocument.Parse(tokenBytes);
        Assert.Equal(
            "dps.r0c-release-binding-token-preimages/v1",
            tokens.RootElement.GetProperty("schema_version").GetString());
        var previousToken = tokens.RootElement
            .GetProperty("previous_execution_token_base64")
            .GetString()!;
        var candidateToken = tokens.RootElement
            .GetProperty("candidate_execution_token_base64")
            .GetString()!;
        var previousTokenRaw = Convert.FromBase64String(previousToken);
        var candidateTokenRaw = Convert.FromBase64String(candidateToken);
        Assert.Equal(32, previousTokenRaw.Length);
        Assert.Equal(32, candidateTokenRaw.Length);
        Assert.Equal(
            previousToken,
            Convert.ToBase64String(previousTokenRaw));
        Assert.Equal(
            candidateToken,
            Convert.ToBase64String(candidateTokenRaw));
        Assert.NotEqual(previousTokenRaw, candidateTokenRaw);

        using var previousSigned = JsonDocument.Parse(previousSignedBytes);
        using var previousStable = JsonDocument.Parse(previousStableBytes);
        using var candidate = JsonDocument.Parse(candidateBytes);
        Assert.Equal(
            "SIGNED",
            previousSigned.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "STABLE",
            previousStable.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "SIGNED",
            candidate.RootElement.GetProperty("status").GetString());
        var signedProperties = previousSigned.RootElement
            .EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value);
        var stableProperties = previousStable.RootElement
            .EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value);
        Assert.Equal(
            ["signature", "status"],
            signedProperties.Keys
                .Where(name => !JsonElement.DeepEquals(
                    signedProperties[name], stableProperties[name]))
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(previousTokenRaw)),
            previousSigned.RootElement
                .GetProperty("activation_token_sha256")
                .GetString());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(candidateTokenRaw)),
            candidate.RootElement
                .GetProperty("activation_token_sha256")
                .GetString());
        Assert.Equal(
            previousStable.RootElement.GetProperty("bom_id").GetString(),
            candidate.RootElement
                .GetProperty("previous_stable_bom")
                .GetString());
        Assert.Equal(
            Sha256Hex(previousStableBytes),
            candidate.RootElement
                .GetProperty("previous_stable_bom_sha256")
                .GetString());

        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = new ActiveReleaseBindingAuthority(
            [trustKey],
            store,
            () => Now);
        var bootstrapReceipt =
            authority.Activate(Device, previousSignedBytes, previousToken);
        var candidateReceipt = authority.Activate(
            Device,
            candidateBytes,
            previousStableBytes,
            candidateToken);

        Assert.Equal("activation", bootstrapReceipt.ReceiptKind);
        Assert.Null(bootstrapReceipt.From);
        Assert.Equal(1, bootstrapReceipt.To.Generation);
        Assert.Equal("activation", candidateReceipt.ReceiptKind);
        Assert.Equal(2, candidateReceipt.Sequence);
        Assert.Equal(2, candidateReceipt.To.Generation);
        Assert.True(authority.TryReadActive(Device, out var binding));
        Assert.NotNull(binding);
        Assert.Equal(Sha256Hex(candidateBytes), binding!.ReleaseBomSha256);
        Assert.Equal(2, binding.Generation);
        Assert.Equal(2, binding.ReleaseBomGeneration);
        Assert.Equal(candidateToken, binding.ExecutionTokenBase64);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(candidateTokenRaw)),
            binding.ActivationTokenSha256);
        Assert.Equal(trustKey.Identity, binding.SignerIdentity);
        Assert.Equal(trustKey.KeyId, binding.SignerKeyId);

        var journal = store.LoadAll();
        Assert.Equal(2, journal.Count);
        Assert.Equal(previousSignedBytes, journal[0].SignedBomBytes);
        Assert.Null(journal[0].PreviousStableBomBytes);
        Assert.Equal(candidateBytes, journal[1].SignedBomBytes);
        Assert.Equal(previousStableBytes, journal[1].PreviousStableBomBytes);
        var previousBinding = Assert.IsType<ActiveReleaseBindingV1>(
            journal[1].PreviousBinding);
        Assert.Equal(
            Sha256Hex(previousSignedBytes),
            previousBinding.ReleaseBomSha256);
        Assert.Equal(previousToken, previousBinding.ExecutionTokenBase64);
        Assert.Equal(candidateReceipt, journal[1].Receipt);
    }

    private static void AssertCompatCorpusFile(
        JsonElement metadata,
        string relativePath,
        byte[] bytes)
    {
        var entry = metadata.GetProperty("files")
            .EnumerateArray()
            .Single(item => string.Equals(
                item.GetProperty("path").GetString(),
                relativePath,
                StringComparison.Ordinal));
        Assert.Equal(entry.GetProperty("size_bytes").GetInt64(), bytes.LongLength);
        Assert.Equal(
            entry.GetProperty("sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static byte[] LoadEmbeddedResource(string resource)
    {
        using var stream = typeof(ActiveReleaseBindingAuthorityTests).Assembly
            .GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"embedded contract resource is missing: {resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact, Trait("Category", "Unit")]
    public void CanonicalNumberAndBomSizeLimitsMatchCandidateValidator()
    {
        var accepted = "1" + new string('0', 4_299);
        Assert.Equal("{\"n\":" + accepted + "}", CanonicalizeNumber(accepted));

        var rejected = "1" + new string('0', 4_300);
        Assert.Throws<ActiveReleaseBindingException>(
            () => CanonicalizeNumber(rejected));

        using var signer = new TestSigner();
        var authority = Authority(signer);
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(Device, ReadOnlySpan<byte>.Empty, Token("empty")));
        Assert.Throws<ActiveReleaseBindingException>(
            () => authority.Activate(
                Device, new byte[4 * 1024 * 1024 + 1], Token("oversized")));
    }

    private static string CanonicalizeNumber(string wire)
    {
        using var document = JsonDocument.Parse("{\"n\":" + wire + "}");
        return Encoding.UTF8.GetString(
            ReleaseBomCanonicalJson.Serialize(document.RootElement));
    }

    [Fact, Trait("Category", "Unit")]
    public void PolicyFactsSourceFailsClosed()
    {
        // Absent binding reads false.
        var emptySource = new PolicyBoundReleaseBomFactsSource(new FixedBindingReader(null));
        Assert.False(emptySource.TryReadActiveFacts(Device, out _, out _));

        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var authority = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        authority.Activate(Device, bom, token);
        Assert.True(authority.TryReadActive(Device, out var binding));

        // Non-active binding throws instead of being served.
        var demoted = new PolicyBoundReleaseBomFactsSource(
            new FixedBindingReader(binding! with { Status = "previous" }));
        Assert.Throws<ActiveReleaseBindingException>(
            () => demoted.TryReadActiveFacts(Device, out _, out _));

        // Foreign-device binding throws.
        var foreign = new PolicyBoundReleaseBomFactsSource(new FixedBindingReader(binding));
        Assert.Throws<ActiveReleaseBindingException>(
            () => foreign.TryReadActiveFacts(OtherDevice, out _, out _));

        // The composition-fixed path against the real authority serves the
        // runtime activation ordinal.
        var source = new PolicyBoundReleaseBomFactsSource(authority);
        Assert.True(source.TryReadActiveFacts(Device, out var sha, out var generation));
        Assert.Equal(binding!.ReleaseBomSha256, sha);
        Assert.Equal(binding.Generation, generation);
    }

    // ----- multi-instance revision-aware reads: resync-on-read -----

    /// <summary>
    /// In-memory store twin whose per-device freshness reads can be severed
    /// or poisoned on demand, driving the resync fail-closed edges without
    /// corrupting the durable journal content itself.
    /// </summary>
    private sealed class ResyncTestStore
        : IReleaseBindingTruthStore,
          IActiveReleaseBindingRecoveryCoordinator
    {
        private readonly InMemoryReleaseBindingTruthStore _inner =
            InMemoryReleaseBindingTruthStore.CreateTestOnly();

        public bool FreshnessReadsFail { get; set; }
        public Func<ReleaseBindingJournalSnapshot, ReleaseBindingJournalSnapshot>?
            SnapshotOverride { get; set; }
        public Action? BeforeSnapshot { get; set; }

        public void Append(ReleaseBindingTruthRecord record) => _inner.Append(record);

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAll() => _inner.LoadAll();

        public long LoadDeviceHeadSequence(string deviceBindingId)
        {
            if (FreshnessReadsFail)
            {
                throw new InvalidOperationException("truth store freshness read refused");
            }
            return _inner.LoadDeviceHeadSequence(deviceBindingId);
        }

        public IReadOnlyList<ReleaseBindingTruthRecord> LoadAfter(
            string deviceBindingId,
            long afterSequence)
        {
            if (FreshnessReadsFail)
            {
                throw new InvalidOperationException("truth store freshness read refused");
            }
            return _inner.LoadAfter(deviceBindingId, afterSequence);
        }

        public ReleaseBindingJournalSnapshot LoadSnapshotAfter(
            string deviceBindingId,
            long afterSequence)
        {
            if (FreshnessReadsFail)
            {
                throw new InvalidOperationException("truth store freshness read refused");
            }
            var beforeSnapshot = BeforeSnapshot;
            BeforeSnapshot = null;
            beforeSnapshot?.Invoke();
            var snapshot = _inner.LoadSnapshotAfter(deviceBindingId, afterSequence);
            return SnapshotOverride?.Invoke(snapshot) ?? snapshot;
        }

        public ValueTask<IActiveReleaseBindingRecoveryScope> AcquireAsync(
            string deviceBindingId,
            CancellationToken cancellationToken)
            => ((IActiveReleaseBindingRecoveryCoordinator)_inner).AcquireAsync(
                deviceBindingId, cancellationToken);
    }

    [Fact, Trait("Category", "Unit")]
    public void FreshReaderServesTheCacheAndForeignDeviceProgressDoesNotDisturbIt()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var writer = Authority(signer, store);
        var (bom, token) = MakeBom(signer, "bom-1", 1, null);
        writer.Activate(Device, bom, token);
        var reader = Authority(signer, store);

        // The cached view at the journal head is served repeatedly with no
        // state change (the empty-delta path).
        Assert.True(reader.TryReadActive(Device, out var first));
        Assert.True(reader.TryReadActive(Device, out var second));
        Assert.Equal(first, second);
        Assert.Equal(Sha256Hex(bom), first!.ReleaseBomSha256);
        Assert.Single(reader.ReadReceipts(Device));

        // Journal growth on OTHER devices does not touch this device's
        // freshness: the head check is scoped per device_binding_id.
        var (foreignBom, foreignToken) = MakeBom(signer, "bom-foreign", 1, null);
        writer.Activate(OtherDevice, foreignBom, foreignToken);
        Assert.True(reader.TryReadActive(Device, out var undisturbed));
        Assert.Equal(first, undisturbed);
        Assert.Single(reader.ReadReceipts(Device));
        // The foreign device's own read resyncs from the journal on demand.
        Assert.True(reader.TryReadActive(OtherDevice, out var foreign));
        Assert.Equal(Sha256Hex(foreignBom), foreign!.ReleaseBomSha256);
        Assert.True(reader.TryReadActive(Device, out var stillUndisturbed));
        Assert.Equal(first, stillUndisturbed);
    }

    [Fact, Trait("Category", "Unit")]
    public void LaggingReaderReplaysTheWholeDeltaThroughTheRecoveryPipeline()
    {
        using var signer = new TestSigner();
        var store = InMemoryReleaseBindingTruthStore.CreateTestOnly();
        var writer = Authority(signer, store);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        writer.Activate(Device, first, firstToken);
        var reader = Authority(signer, store);

        // Another instance lands three transitions of every kind while the
        // reader's cache sleeps: activation, revocation, rollback.
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(writer, signer, Device, second, first, secondToken);
        writer.Revoke(Device, 2);
        writer.Rollback(Device, firstToken);

        // The reader never serves the stale bom-1 cache: one read replays
        // the whole multi-record, multi-kind delta and serves the exact
        // rolled-back truth (bom-1 digest and token, runtime generation 3).
        Assert.True(reader.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(first), binding!.ReleaseBomSha256);
        Assert.Equal(3, binding.Generation);
        Assert.Equal(1, binding.ReleaseBomGeneration);
        Assert.Equal(firstToken, binding.ExecutionTokenBase64);
        Assert.Equal(writer.ReadReceipts(Device), reader.ReadReceipts(Device));
        Assert.Equal(4, reader.ReadReceipts(Device).Count);
    }

    [Fact, Trait("Category", "Unit")]
    public void InvalidActivationRequestDigestLeavesNoReaderCacheResidue()
    {
        using var signer = new TestSigner();
        var store = new ResyncTestStore();
        var writer = new ActiveReleaseBindingAuthority(
            [signer.TrustKey],
            store,
            () => Now);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        writer.Activate(Device, first, firstToken);
        var reader = new ActiveReleaseBindingAuthority(
            [signer.TrustKey],
            store,
            () => Now);
        Assert.True(reader.TryReadActive(Device, out var cached));
        Assert.Equal(Sha256Hex(first), cached!.ReleaseBomSha256);

        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(writer, signer, Device, second, first, secondToken);
        store.SnapshotOverride = static snapshot => snapshot with
        {
            Records =
            [
                snapshot.Records[0] with
                {
                    RequestSha256 =
                        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
                }
            ]
        };

        Assert.False(reader.TryReadActive(Device, out var rejected));
        Assert.Null(rejected);

        // The failed ApplyRecord did not mutate byte slots or advance the
        // cached sequence. With the honest snapshot restored, this same
        // reader can replay the exact record and reach the durable head.
        store.SnapshotOverride = null;
        Assert.True(reader.TryReadActive(Device, out var advanced));
        Assert.Equal(Sha256Hex(second), advanced!.ReleaseBomSha256);
        Assert.Equal(2, advanced.Generation);
    }

    [Fact, Trait("Category", "Unit")]
    public void InvalidOrUnreachableDeltaFailsClosedAndNeverServesTheStaleCache()
    {
        using var signer = new TestSigner();
        var store = new ResyncTestStore();
        var writer = new ActiveReleaseBindingAuthority([signer.TrustKey], store, () => Now);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        writer.Activate(Device, first, firstToken);
        var reader = new ActiveReleaseBindingAuthority([signer.TrustKey], store, () => Now);
        Assert.True(reader.TryReadActive(Device, out var cached));
        Assert.Equal(Sha256Hex(first), cached!.ReleaseBomSha256);
        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        ActivateNext(writer, signer, Device, second, first, secondToken);

        // A delta record that fails the recovery pipeline (its recorded
        // signed BOM bytes are truncated): the read fails closed and must
        // NOT fall back to the cached bom-1 view.
        store.SnapshotOverride = static snapshot => snapshot with
        {
            Records = [snapshot.Records[0] with
            {
                SignedBomBytes = snapshot.Records[0].SignedBomBytes![1..]
            }]
        };
        Assert.False(reader.TryReadActive(Device, out var poisoned));
        Assert.Null(poisoned);
        Assert.Throws<ActiveReleaseBindingException>(() => reader.ReadReceipts(Device));

        // The store cannot be consulted at all: the same fail-closed
        // posture, still never the stale cache.
        store.SnapshotOverride = null;
        store.FreshnessReadsFail = true;
        Assert.False(reader.TryReadActive(Device, out _));
        Assert.Throws<ActiveReleaseBindingException>(() => reader.ReadReceipts(Device));

        // Freshness restored: the same instance resyncs from the journal
        // and serves the advanced truth — the failed reads left no
        // poisoned residue behind.
        store.FreshnessReadsFail = false;
        Assert.True(reader.TryReadActive(Device, out var binding));
        Assert.Equal(Sha256Hex(second), binding!.ReleaseBomSha256);
        Assert.Equal(2, binding.Generation);
        Assert.Equal(writer.ReadReceipts(Device), reader.ReadReceipts(Device));
    }

    [Fact, Trait("Category", "Unit")]
    public void AtomicSnapshotClosesHeadThenDeltaInterleavingWindow()
    {
        using var signer = new TestSigner();
        var store = new ResyncTestStore();
        var writer = new ActiveReleaseBindingAuthority([signer.TrustKey], store, () => Now);
        var (first, firstToken) = MakeBom(signer, "bom-1", 1, null);
        writer.Activate(Device, first, firstToken);
        var reader = new ActiveReleaseBindingAuthority([signer.TrustKey], store, () => Now);
        Assert.True(reader.TryReadActive(Device, out var cached));
        Assert.Equal(Sha256Hex(first), cached!.ReleaseBomSha256);

        var (second, secondToken) = MakeBom(signer, "bom-2", 2, first);
        store.BeforeSnapshot = () =>
            ActivateNext(writer, signer, Device, second, first, secondToken);

        // The append happens at the exact old head-check/delta-read seam.
        // One atomic store snapshot must include the new head and its record,
        // so this read cannot return the cached bom-1 binding.
        Assert.True(reader.TryReadActive(Device, out var advanced));
        Assert.Equal(Sha256Hex(second), advanced!.ReleaseBomSha256);
        Assert.Equal(2, advanced.Generation);
        Assert.Equal(2, reader.ReadReceipts(Device).Count);
    }
}
