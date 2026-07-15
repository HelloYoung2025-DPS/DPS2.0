using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dps.WindowsEdgeSupervisor.Contracts;
using Xunit;

namespace Dps.WindowsEdgeSupervisor.Tests;

public sealed class ContractPackTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void Drain_directive_golden_vector_is_produced_by_the_public_codec()
    {
        var path = Path.Combine(
            Fixture.RepositoryRoot(),
            "Modules/windows-edge-supervisor/contracts/provided/edge.worker.drain.directive.v1.golden.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var input = root.GetProperty("input");
        var claims = new DrainDirectiveClaimsV1(
            input.GetProperty("schema_version").GetString()!,
            input.GetProperty("contract_id").GetString()!,
            input.GetProperty("producer_module").GetString()!,
            input.GetProperty("soul_id").GetString()!,
            input.GetProperty("device_binding_id").GetString()!,
            input.GetProperty("platform_account_id").GetString()!,
            input.GetProperty("trace_id").GetString()!,
            input.GetProperty("idempotency_key").GetString()!,
            input.GetProperty("occurred_at").GetString()!,
            input.GetProperty("privacy_class").GetString()!,
            input.GetProperty("drain_id").GetString()!,
            input.GetProperty("slot").GetString()!,
            input.GetProperty("worker_version").GetString()!,
            input.GetProperty("worker_artifact_sha256").GetString()!,
            input.GetProperty("journal_artifact_sha256").GetString()!,
            input.GetProperty("release_bom_sha256").GetString()!,
            input.GetProperty("protected_policy_sha256").GetString()!,
            input.GetProperty("routing_epoch").GetInt64(),
            input.GetProperty("issued_at").GetString()!,
            input.GetProperty("not_before").GetString()!,
            input.GetProperty("expires_at").GetString()!,
            input.GetProperty("supervisor_key_id").GetString()!,
            input.GetProperty("signature_algorithm").GetString()!);
        var statement = DrainDirectiveV1Codec.CreateSigningStatement(claims);
        Assert.Equal(root.GetProperty("statement_byte_length").GetInt32(), statement.Length);
        Assert.Equal(root.GetProperty("statement_bytes_hex").GetString(), Convert.ToHexStringLower(statement));
        Assert.Equal(root.GetProperty("statement_sha256").GetString(), Sha256(statement));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Worker_receipt_golden_vector_is_produced_by_the_public_codec()
    {
        var path = Path.Combine(
            Fixture.RepositoryRoot(),
            "Modules/windows-edge-supervisor/contracts/provided/edge.worker.drain.receipt.v1.golden.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var input = root.GetProperty("input");
        var claims = new WorkerDrainReceiptClaimsV1(
            input.GetProperty("schema_version").GetString()!,
            input.GetProperty("contract_id").GetString()!,
            input.GetProperty("producer_module").GetString()!,
            input.GetProperty("soul_id").GetString()!,
            input.GetProperty("device_binding_id").GetString()!,
            input.GetProperty("platform_account_id").GetString()!,
            input.GetProperty("trace_id").GetString()!,
            input.GetProperty("idempotency_key").GetString()!,
            input.GetProperty("occurred_at").GetString()!,
            input.GetProperty("privacy_class").GetString()!,
            input.GetProperty("drain_id").GetString()!,
            input.GetProperty("slot").GetString()!,
            input.GetProperty("worker_version").GetString()!,
            input.GetProperty("worker_artifact_sha256").GetString()!,
            input.GetProperty("journal_artifact_sha256").GetString()!,
            input.GetProperty("release_bom_sha256").GetString()!,
            input.GetProperty("protected_policy_sha256").GetString()!,
            input.GetProperty("routing_epoch").GetInt64(),
            input.GetProperty("intake_stopped").GetBoolean(),
            input.GetProperty("worker_drained").GetBoolean(),
            input.GetProperty("remaining_in_flight").GetInt32(),
            input.GetProperty("issued_at").GetString()!,
            input.GetProperty("not_before").GetString()!,
            input.GetProperty("expires_at").GetString()!);
        var statement = WorkerDrainReceiptContractCodec.CreateSigningStatement(claims);
        Assert.Equal(root.GetProperty("statement_byte_length").GetInt32(), statement.Length);
        Assert.Equal(root.GetProperty("statement_bytes_hex").GetString(), Convert.ToHexStringLower(statement));
        Assert.Equal(root.GetProperty("statement_sha256").GetString(), Sha256(statement));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Signed_drain_directive_requires_the_pinned_supervisor_and_exact_wire()
    {
        using var supervisorKey = RSA.Create(2048);
        var claims = DirectiveClaims(supervisorKey);
        var statement = DrainDirectiveV1Codec.CreateSigningStatement(claims);
        var signature = supervisorKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        var directive = DrainDirectiveV1Codec.AttachSignature(
            claims,
            Convert.ToBase64String(signature));
        var wire = DrainDirectiveV1Codec.Serialize(directive);
        var expectation = DirectiveExpectation(claims);
        var verified = DrainDirectiveV1Codec.DecodeAndVerify(
            wire,
            expectation,
            supervisorKey,
            DateTimeOffset.Parse("2026-07-15T00:00:01.0000000+00:00"));

        Assert.Equal(directive, verified.Envelope);
        Assert.Equal(Sha256(wire), verified.WireSha256);
        Assert.Equal(Sha256(statement), verified.StatementSha256);
        Assert.Throws<InvalidDataException>(() => DrainDirectiveV1Codec.DecodeAndVerify(
            wire,
            expectation,
            supervisorKey,
            DateTimeOffset.Parse("2026-07-15T00:10:00.0000000+00:00")));
        Assert.Throws<InvalidDataException>(() => DrainDirectiveV1Codec.DecodeAndVerify(
            wire,
            expectation,
            supervisorKey,
            DateTimeOffset.Parse(claims.ExpiresAt),
            maximumAgeSeconds: 300,
            maximumClockSkewSeconds: 0));
        var durableContinuation = DrainDirectiveV1Codec.DecodeAndVerifyDurableContinuation(
            wire,
            expectation,
            supervisorKey);
        Assert.Equal(verified.WireSha256, durableContinuation.WireSha256);
        Assert.Throws<InvalidDataException>(() =>
            DrainDirectiveV1Codec.DecodeAndVerifyDurableContinuation(
                wire,
                expectation with { RoutingEpoch = expectation.RoutingEpoch + 1 },
                supervisorKey));
        Assert.Throws<InvalidDataException>(() => DrainDirectiveV1Codec.DecodeAndVerify(
            wire,
            expectation with { ReleaseBomSha256 = new string('9', 64) },
            supervisorKey,
            DateTimeOffset.Parse("2026-07-15T00:00:01.0000000+00:00")));
        using var wrongKey = RSA.Create(2048);
        Assert.Throws<CryptographicException>(() => DrainDirectiveV1Codec.DecodeAndVerify(
            wire,
            expectation,
            wrongKey,
            DateTimeOffset.Parse("2026-07-15T00:00:01.0000000+00:00")));

        var tampered = directive with { RoutingEpoch = directive.RoutingEpoch + 1 };
        Assert.Throws<CryptographicException>(() => DrainDirectiveV1Codec.DecodeAndVerify(
            DrainDirectiveV1Codec.Serialize(tampered),
            expectation with { RoutingEpoch = tampered.RoutingEpoch },
            supervisorKey,
            DateTimeOffset.Parse("2026-07-15T00:00:01.0000000+00:00")));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Worker_receipt_is_worker_only_and_journal_payload_binds_exact_raw_wire_digest()
    {
        using var workerKey = RSA.Create(2048);
        var claims = WorkerClaims();
        var statement = WorkerDrainReceiptContractCodec.CreateSigningStatement(claims);
        var signature = workerKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        var receipt = WorkerDrainReceiptContractCodec.AttachSignature(
            claims,
            WorkerDrainReceiptContractCodec.ComputeKeyId(workerKey),
            Convert.ToBase64String(signature));
        var wire = WorkerDrainReceiptContractCodec.Serialize(receipt);
        var expectation = new WorkerDrainReceiptExpectationV1(
            claims.DrainId,
            claims.Slot,
            claims.WorkerVersion,
            claims.WorkerArtifactSha256,
            claims.JournalArtifactSha256,
            claims.ReleaseBomSha256,
            claims.ProtectedPolicySha256,
            claims.RoutingEpoch,
            claims.SoulId,
            claims.DeviceBindingId,
            claims.PlatformAccountId,
            claims.TraceId,
            claims.IdempotencyKey,
            claims.OccurredAt);
        var verified = WorkerDrainReceiptContractCodec.DecodeAndVerify(
            wire,
            expectation,
            workerKey,
            DateTimeOffset.Parse("2026-07-15T00:00:01.0000000+00:00"));

        Assert.Equal(Sha256(wire), verified.WireSha256);
        Assert.Equal(receipt.WorkerStatementSha256, verified.StatementSha256);
        Assert.Throws<InvalidDataException>(() => WorkerDrainReceiptContractCodec.DecodeAndVerify(
            wire,
            expectation,
            workerKey,
            DateTimeOffset.Parse("2026-07-15T00:10:00.0000000+00:00")));
        Assert.Throws<InvalidDataException>(() => WorkerDrainReceiptContractCodec.DecodeAndVerify(
            wire,
            expectation,
            workerKey,
            DateTimeOffset.Parse(claims.ExpiresAt),
            maximumAgeSeconds: 300,
            maximumClockSkewSeconds: 0));
        Assert.Equal(
            verified.WireSha256,
            WorkerDrainReceiptContractCodec.DecodeAndVerifyDurableContinuation(
                wire,
                expectation,
                workerKey).WireSha256);
        Assert.Throws<InvalidDataException>(() =>
            WorkerDrainReceiptContractCodec.DecodeAndVerifyDurableContinuation(
                wire,
                expectation with { DrainId = "drain-" + new string('0', 64) },
                workerKey));
        var wireText = Encoding.UTF8.GetString(wire);
        Assert.DoesNotContain("journal_receipt", wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("journal_signature", wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("worker_receipt_wire_sha256", wireText, StringComparison.Ordinal);

        var payload = WorkerDrainReceiptContractCodec.CreateJournalPayload(receipt, verified.WireSha256);
        Assert.Equal(
            "{\"drain_id\":\"" + claims.DrainId +
            "\",\"intake_stopped\":true,\"journal_artifact_sha256\":\"" + claims.JournalArtifactSha256 +
            "\",\"protected_policy_sha256\":\"" + claims.ProtectedPolicySha256 +
            "\",\"release_bom_sha256\":\"" + claims.ReleaseBomSha256 +
            "\",\"remaining_in_flight\":0,\"routing_epoch\":4,\"schema_version\":\"1.0\",\"slot\":\"A\"" +
            ",\"worker_artifact_sha256\":\"" + claims.WorkerArtifactSha256 +
            "\",\"worker_drained\":true,\"worker_receipt_wire_sha256\":\"" + verified.WireSha256 +
            "\",\"worker_version\":\"0.3.0\"}",
            Encoding.UTF8.GetString(payload));

        var tampered = receipt with { WorkerArtifactSha256 = new string('9', 64) };
        Assert.Throws<InvalidDataException>(() => WorkerDrainReceiptContractCodec.Serialize(tampered));
    }

    private static DrainDirectiveClaimsV1 DirectiveClaims(RSA key) => new(
        DrainDirectiveV1Codec.SchemaVersion,
        DrainDirectiveV1Codec.ContractId,
        DrainDirectiveV1Codec.ProducerModule,
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        "idem_" + new string('e', 64),
        "2026-07-15T00:00:00.0000000+00:00",
        "internal",
        "drain-" + new string('f', 64),
        "A",
        "0.3.0",
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new string('4', 64),
        4,
        "2026-07-15T00:00:01.0000000+00:00",
        "2026-07-15T00:00:00.0000000+00:00",
        "2026-07-15T00:05:00.0000000+00:00",
        DrainDirectiveV1Codec.ComputeKeyId(key),
        DrainDirectiveV1Codec.SignatureAlgorithm);

    private static DrainDirectiveExpectationV1 DirectiveExpectation(
        DrainDirectiveClaimsV1 claims) => new(
        claims.DrainId,
        claims.Slot,
        claims.WorkerVersion,
        claims.WorkerArtifactSha256,
        claims.JournalArtifactSha256,
        claims.ReleaseBomSha256,
        claims.ProtectedPolicySha256,
        claims.RoutingEpoch,
        claims.SoulId,
        claims.DeviceBindingId,
        claims.PlatformAccountId,
        claims.TraceId,
        claims.IdempotencyKey,
        claims.OccurredAt);

    private static WorkerDrainReceiptClaimsV1 WorkerClaims() => new(
        WorkerDrainReceiptContractCodec.SchemaVersion,
        WorkerDrainReceiptContractCodec.ContractId,
        WorkerDrainReceiptContractCodec.ProducerModule,
        "soul_" + new string('a', 64),
        "db_" + new string('b', 32),
        "pa_" + new string('c', 32),
        "trace_" + new string('d', 32),
        "idem_" + new string('e', 64),
        "2026-07-15T00:00:00.0000000+00:00",
        "internal",
        "drain-" + new string('f', 64),
        "A",
        "0.3.0",
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new string('4', 64),
        4,
        true,
        true,
        0,
        "2026-07-15T00:00:01.0000000+00:00",
        "2026-07-15T00:00:00.0000000+00:00",
        "2026-07-15T00:05:00.0000000+00:00");

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
