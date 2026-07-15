using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dps.GBrainProjector.Contracts;

public sealed record GBrainSourceBindingV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("contract_id")] string ContractId,
    [property: JsonPropertyName("producer_module")] string ProducerModule,
    [property: JsonPropertyName("soul_id")] string SoulId,
    [property: JsonPropertyName("device_binding_id")] string? DeviceBindingId,
    [property: JsonPropertyName("platform_account_id")] string? PlatformAccountId,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey,
    [property: JsonPropertyName("occurred_at"), JsonConverter(typeof(ProjectionCanonicalUtcJsonConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("privacy_class")] string PrivacyClass,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("nonce")] long Nonce,
    [property: JsonPropertyName("soul_hash")] string SoulHash,
    [property: JsonPropertyName("allocated_at"), JsonConverter(typeof(ProjectionCanonicalUtcJsonConverter))] DateTimeOffset AllocatedAt,
    [property: JsonPropertyName("binding_revision")] string BindingRevision,
    [property: JsonPropertyName("binding_checksum")] string BindingChecksum)
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string CurrentContractId = "gbrain.source.binding/v1";
    public const string CurrentProducerModule = "gbrain-projector";
    public const string CurrentPrivacyClass = "personal";
    public const string CurrentAlgorithm = "dps.gbrain-source-binding.sha256-nonce/v1";
    public const long MaximumNonce = 1023;
    public const int SourceIdLength = 32;
    public const int SourceDigestHexLength = 28;

    public static GBrainSourceBindingV1 Create(
        string soulId,
        string sourceId,
        long nonce,
        DateTimeOffset allocatedAt)
    {
        var withoutProof = new GBrainSourceBindingV1(
            CurrentSchemaVersion,
            CurrentContractId,
            CurrentProducerModule,
            soulId,
            null,
            null,
            null,
            null,
            allocatedAt,
            CurrentPrivacyClass,
            sourceId,
            CurrentAlgorithm,
            nonce,
            SourceBindingContractValidation.SoulHash(soulId),
            allocatedAt,
            new string('0', 64),
            new string('0', 64));
        var revision = GBrainSourceBindingCanonicalizer.ComputeRevision(withoutProof);
        var withRevision = withoutProof with { BindingRevision = revision };
        return withRevision with
        {
            BindingChecksum = GBrainSourceBindingCanonicalizer.ComputeChecksum(withRevision)
        };
    }

    public void Validate()
    {
        ValidateContent();
        SourceBindingContractValidation.RequireSha256(BindingRevision, nameof(BindingRevision));
        SourceBindingContractValidation.RequireSha256(BindingChecksum, nameof(BindingChecksum));
        var expectedRevision = GBrainSourceBindingCanonicalizer.ComputeRevision(this);
        var expectedChecksum = GBrainSourceBindingCanonicalizer.ComputeChecksum(this);
        if (!SourceBindingContractValidation.FixedTimeEquals(BindingRevision, expectedRevision) ||
            !SourceBindingContractValidation.FixedTimeEquals(BindingChecksum, expectedChecksum))
        {
            throw new InvalidOperationException("Source binding revision or checksum does not match canonical binding content.");
        }
    }

    internal void ValidateContent()
    {
        ProjectionContractValidation.RequireExact(SchemaVersion, CurrentSchemaVersion, nameof(SchemaVersion));
        ProjectionContractValidation.RequireExact(ContractId, CurrentContractId, nameof(ContractId));
        ProjectionContractValidation.RequireExact(ProducerModule, CurrentProducerModule, nameof(ProducerModule));
        ProjectionContractValidation.RequireSoulId(SoulId, nameof(SoulId));
        if (DeviceBindingId is not null || PlatformAccountId is not null || TraceId is not null || IdempotencyKey is not null)
        {
            throw new InvalidOperationException("Soul-owned Source binding must not pretend to be device, account, trace, or delivery scoped.");
        }

        ProjectionContractValidation.RequireUtc(OccurredAt, nameof(OccurredAt));
        ProjectionContractValidation.RequireExact(PrivacyClass, CurrentPrivacyClass, nameof(PrivacyClass));
        SourceBindingContractValidation.RequireSourceId(SourceId, nameof(SourceId));
        ProjectionContractValidation.RequireExact(Algorithm, CurrentAlgorithm, nameof(Algorithm));
        if (Nonce is < 0 or > MaximumNonce)
        {
            throw new ArgumentOutOfRangeException(nameof(Nonce), $"Source binding nonce must be between zero and {MaximumNonce}.");
        }

        ProjectionContractValidation.RequireSha256(SoulHash, nameof(SoulHash));
        var expectedSoulHash = SourceBindingContractValidation.SoulHash(SoulId);
        if (!SourceBindingContractValidation.FixedTimeEquals(SoulHash, expectedSoulHash))
        {
            throw new InvalidOperationException("Source binding must retain the complete 256-bit Soul hash.");
        }

        ProjectionContractValidation.RequireUtc(AllocatedAt, nameof(AllocatedAt));
        if (OccurredAt != AllocatedAt)
        {
            throw new InvalidOperationException("Source binding occurred_at must equal allocated_at.");
        }

        var expectedSourceId = GBrainSourceIdCandidates.Compute(SoulId, Nonce);
        if (!string.Equals(SourceId, expectedSourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Source binding Source identifier does not match the fixed Soul-and-nonce derivation.");
        }
    }
}

public static class GBrainSourceIdCandidates
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("dps.gbrain-source-binding/source-id/v1\0");

    public static string Compute(string soulId, long nonce)
    {
        ProjectionContractValidation.RequireSoulId(soulId, nameof(soulId));
        if (nonce is < 0 or > GBrainSourceBindingV1.MaximumNonce)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nonce),
                $"Source binding nonce must be between zero and {GBrainSourceBindingV1.MaximumNonce}.");
        }

        var soulBytes = Encoding.ASCII.GetBytes(soulId);
        Span<byte> nonceBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(nonceBytes, nonce);
        var input = new byte[Domain.Length + soulBytes.Length + 1 + nonceBytes.Length];
        Domain.CopyTo(input, 0);
        soulBytes.CopyTo(input, Domain.Length);
        input[Domain.Length + soulBytes.Length] = 0;
        nonceBytes.CopyTo(input.AsSpan(Domain.Length + soulBytes.Length + 1));
        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        return "dps-" + digest[..GBrainSourceBindingV1.SourceDigestHexLength];
    }
}

public static class GBrainSourceBindingCanonicalizer
{
    public static string Serialize(GBrainSourceBindingV1 binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        return Write(binding, includeRevision: true, includeChecksum: true);
    }

    public static string ComputeRevision(GBrainSourceBindingV1 binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.ValidateContent();
        return Sha256(Write(binding, includeRevision: false, includeChecksum: false));
    }

    public static string ComputeChecksum(GBrainSourceBindingV1 binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.ValidateContent();
        SourceBindingContractValidation.RequireSha256(binding.BindingRevision, nameof(binding.BindingRevision));
        return Sha256(Write(binding, includeRevision: true, includeChecksum: false));
    }

    private static string Write(GBrainSourceBindingV1 binding, bool includeRevision, bool includeChecksum)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", binding.SchemaVersion);
            writer.WriteString("contract_id", binding.ContractId);
            writer.WriteString("producer_module", binding.ProducerModule);
            writer.WriteString("soul_id", binding.SoulId);
            writer.WriteNull("device_binding_id");
            writer.WriteNull("platform_account_id");
            writer.WriteNull("trace_id");
            writer.WriteNull("idempotency_key");
            writer.WriteString("occurred_at", ProjectionCanonicalUtcJsonConverter.Format(binding.OccurredAt));
            writer.WriteString("privacy_class", binding.PrivacyClass);
            writer.WriteString("source_id", binding.SourceId);
            writer.WriteString("algorithm", binding.Algorithm);
            writer.WriteNumber("nonce", binding.Nonce);
            writer.WriteString("soul_hash", binding.SoulHash);
            writer.WriteString("allocated_at", ProjectionCanonicalUtcJsonConverter.Format(binding.AllocatedAt));
            if (includeRevision)
            {
                writer.WriteString("binding_revision", binding.BindingRevision);
            }

            if (includeChecksum)
            {
                writer.WriteString("binding_checksum", binding.BindingChecksum);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal static class SourceBindingContractValidation
{
    internal static string SoulHash(string soulId)
    {
        ProjectionContractValidation.RequireSoulId(soulId, nameof(soulId));
        return soulId[5..];
    }

    internal static void RequireSourceId(string value, string parameterName)
    {
        if (value is null || value.Length != GBrainSourceBindingV1.SourceIdLength ||
            !value.StartsWith("dps-", StringComparison.Ordinal) ||
            value.AsSpan(4).ContainsAnyExcept("0123456789abcdef"))
        {
            throw new ArgumentException($"{parameterName} must be a canonical 32-character lowercase Source identifier.", parameterName);
        }
    }

    internal static void RequireSha256(string value, string parameterName) =>
        ProjectionContractValidation.RequireSha256(value, parameterName);

    internal static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}
