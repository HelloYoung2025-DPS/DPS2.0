using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dps.ControlPlaneHost.Contracts;
using Dps.PolicyApproval.Contracts;
using Npgsql;

namespace Dps.PolicyApproval;

public sealed record PostgresPolicyApprovalSubmissionExecutorOptions(
    string ExecutorConnectionString,
    string SchemaName,
    string ExpectedExecutorRoleName)
{
    public void Validate()
    {
        PolicyApprovalSubmissionCompositionGuard.ValidateOptions(
            ExecutorConnectionString,
            SchemaName,
            ExpectedExecutorRoleName,
            nameof(ExecutorConnectionString),
            nameof(ExpectedExecutorRoleName));
        if (new NpgsqlConnectionStringBuilder(ExecutorConnectionString).Pooling)
            throw new ArgumentException(
                "ExecutorConnectionString must explicitly set Pooling=false because session advisory guards cannot enter a connection pool.",
                nameof(ExecutorConnectionString));
    }

    internal PolicyApprovalSubmissionAuthorityRuntime ToAuthorityRuntime()
    {
        Validate();
        return new PolicyApprovalSubmissionAuthorityRuntime(
            PolicyApprovalSubmissionDatabaseRole.Executor,
            PolicyApprovalSubmissionCompositionGuard.BuildBoundedConnectionString(ExecutorConnectionString),
            SchemaName,
            ExpectedExecutorRoleName);
    }

    internal PostgresPolicyApprovalOptions ToPolicyRuntimeOptions()
    {
        Validate();
        return new PostgresPolicyApprovalOptions(
            ExecutorConnectionString,
            SchemaName,
            ExpectedExecutorRoleName);
    }

    public override string ToString()
        => $"PostgresPolicyApprovalSubmissionExecutorOptions {{ ExecutorConnectionString = [REDACTED], SchemaName = {SchemaName}, ExpectedExecutorRoleName = {ExpectedExecutorRoleName} }}";
}

public sealed record PostgresPolicyApprovalSubmissionReconciliationOptions(
    string ReconciliationConnectionString,
    string SchemaName,
    string ExpectedReconciliationRoleName)
{
    public void Validate()
        => PolicyApprovalSubmissionCompositionGuard.ValidateOptions(
            ReconciliationConnectionString,
            SchemaName,
            ExpectedReconciliationRoleName,
            nameof(ReconciliationConnectionString),
            nameof(ExpectedReconciliationRoleName));

    internal PolicyApprovalSubmissionAuthorityRuntime ToAuthorityRuntime()
    {
        Validate();
        return new PolicyApprovalSubmissionAuthorityRuntime(
            PolicyApprovalSubmissionDatabaseRole.Reconciliation,
            PolicyApprovalSubmissionCompositionGuard.BuildBoundedConnectionString(ReconciliationConnectionString),
            SchemaName,
            ExpectedReconciliationRoleName);
    }

    public override string ToString()
        => $"PostgresPolicyApprovalSubmissionReconciliationOptions {{ ReconciliationConnectionString = [REDACTED], SchemaName = {SchemaName}, ExpectedReconciliationRoleName = {ExpectedReconciliationRoleName} }}";
}

public sealed record PostgresPolicyApprovalSubmissionRecoveryOptions(
    string RecoveryConnectionString,
    string SchemaName,
    string ExpectedRecoveryRoleName)
{
    public void Validate()
        => PolicyApprovalSubmissionCompositionGuard.ValidateOptions(
            RecoveryConnectionString,
            SchemaName,
            ExpectedRecoveryRoleName,
            nameof(RecoveryConnectionString),
            nameof(ExpectedRecoveryRoleName));

    internal PolicyApprovalSubmissionAuthorityRuntime ToAuthorityRuntime()
    {
        Validate();
        return new PolicyApprovalSubmissionAuthorityRuntime(
            PolicyApprovalSubmissionDatabaseRole.Recovery,
            PolicyApprovalSubmissionCompositionGuard.BuildBoundedConnectionString(RecoveryConnectionString),
            SchemaName,
            ExpectedRecoveryRoleName);
    }

    public override string ToString()
        => $"PostgresPolicyApprovalSubmissionRecoveryOptions {{ RecoveryConnectionString = [REDACTED], SchemaName = {SchemaName}, ExpectedRecoveryRoleName = {ExpectedRecoveryRoleName} }}";
}

public sealed record PolicyApprovalSubmissionAuthorityTopology(
    string EvaluationPublicKeySha256,
    string PromotionPublicKeySha256,
    string RevocationPublicKeySha256,
    string FenceAuthorizationPublicKeySha256,
    string ExecutorSubmissionPublicKeySha256,
    string ReconciliationPublicKeySha256,
    string RecoveryPublicKeySha256,
    string PolicyStateSigningPublicKeySha256)
{
    public static PolicyApprovalSubmissionAuthorityTopology Create(
        ReadOnlySpan<byte> evaluationPublicKey,
        ReadOnlySpan<byte> promotionPublicKey,
        ReadOnlySpan<byte> revocationPublicKey,
        ReadOnlySpan<byte> fenceAuthorizationPublicKey,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> reconciliationPublicKey,
        ReadOnlySpan<byte> recoveryPublicKey,
        ReadOnlySpan<byte> policyStateSigningPublicKey)
    {
        return new PolicyApprovalSubmissionAuthorityTopology(
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                evaluationPublicKey,
                nameof(evaluationPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                promotionPublicKey,
                nameof(promotionPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                revocationPublicKey,
                nameof(revocationPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                fenceAuthorizationPublicKey,
                nameof(fenceAuthorizationPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                executorSubmissionPublicKey,
                nameof(executorSubmissionPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                reconciliationPublicKey,
                nameof(reconciliationPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                recoveryPublicKey,
                nameof(recoveryPublicKey)),
            PolicyApprovalSubmissionCompositionGuard.ComputePublicKeySha256(
                policyStateSigningPublicKey,
                nameof(policyStateSigningPublicKey))).Validated();
    }

    public void Validate()
    {
        var identities = new[]
        {
            EvaluationPublicKeySha256,
            PromotionPublicKeySha256,
            RevocationPublicKeySha256,
            FenceAuthorizationPublicKeySha256,
            ExecutorSubmissionPublicKeySha256,
            ReconciliationPublicKeySha256,
            RecoveryPublicKeySha256,
            PolicyStateSigningPublicKeySha256
        };
        foreach (var identity in identities)
            ApprovalContractGuard.RequireSha256(identity, nameof(identity));
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new ArgumentException("Evaluation, promotion, revocation, fence, executor, reconciliation, recovery, and policy-state signing authorities must be pairwise distinct.");
    }

    private PolicyApprovalSubmissionAuthorityTopology Validated()
    {
        Validate();
        return this;
    }
}

public sealed class PolicyApprovalSubmissionReconcilerClient : IDisposable
{
    private readonly PostgresPolicyApprovalSubmissionAuthority _authority;

    private PolicyApprovalSubmissionReconcilerClient(
        PostgresPolicyApprovalSubmissionReconciliationOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> reconciliationPublicKey,
        ReadOnlySpan<byte> policyStateSigningPrivateKeyPkcs8)
    {
        _authority = PostgresPolicyApprovalSubmissionAuthority.CreateReconciliation(
            options.ToAuthorityRuntime(),
            authorityTopology,
            executorSubmissionPublicKey,
            reconciliationPublicKey,
            policyStateSigningPrivateKeyPkcs8);
    }

    public static PolicyApprovalSubmissionReconcilerClient CreateProduction(
        PostgresPolicyApprovalSubmissionReconciliationOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> reconciliationPublicKey,
        ReadOnlySpan<byte> policyStateSigningPrivateKeyPkcs8)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorityTopology);
        options.Validate();
        authorityTopology.Validate();
        return new PolicyApprovalSubmissionReconcilerClient(
            options,
            authorityTopology,
            executorSubmissionPublicKey,
            reconciliationPublicKey,
            policyStateSigningPrivateKeyPkcs8);
    }

    public Task<PolicyApprovalSubmissionSnapshot> ReadSubmissionAsync(
        Guid submissionAttemptId,
        CancellationToken cancellationToken = default)
        => _authority.ReadSubmissionAsync(submissionAttemptId, cancellationToken);

    public Task<ApprovalSubmissionStateV1> ReconcileSubmissionAsync(
        ApprovalSubmissionReconciliationV1 reconciliation,
        CancellationToken cancellationToken = default)
        => _authority.ReconcileAsync(reconciliation, cancellationToken);

    public void Dispose() => _authority.Dispose();
}

public sealed class PolicyApprovalSubmissionRecoveryClient : IDisposable
{
    private readonly PostgresPolicyApprovalSubmissionAuthority _authority;

    private PolicyApprovalSubmissionRecoveryClient(
        PostgresPolicyApprovalSubmissionRecoveryOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> recoveryPublicKey,
        ReadOnlySpan<byte> policyStateSigningPrivateKeyPkcs8,
        IActiveReleaseBindingRecoveryCoordinator releaseBindingRecoveryCoordinator)
    {
        _authority = PostgresPolicyApprovalSubmissionAuthority.CreateRecovery(
            options.ToAuthorityRuntime(),
            authorityTopology,
            executorSubmissionPublicKey,
            recoveryPublicKey,
            policyStateSigningPrivateKeyPkcs8,
            releaseBindingRecoveryCoordinator);
    }

    public static PolicyApprovalSubmissionRecoveryClient CreateProduction(
        PostgresPolicyApprovalSubmissionRecoveryOptions options,
        PolicyApprovalSubmissionAuthorityTopology authorityTopology,
        ReadOnlySpan<byte> executorSubmissionPublicKey,
        ReadOnlySpan<byte> recoveryPublicKey,
        ReadOnlySpan<byte> policyStateSigningPrivateKeyPkcs8,
        IActiveReleaseBindingRecoveryCoordinator releaseBindingRecoveryCoordinator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorityTopology);
        ArgumentNullException.ThrowIfNull(releaseBindingRecoveryCoordinator);
        options.Validate();
        authorityTopology.Validate();
        return new PolicyApprovalSubmissionRecoveryClient(
            options,
            authorityTopology,
            executorSubmissionPublicKey,
            recoveryPublicKey,
            policyStateSigningPrivateKeyPkcs8,
            releaseBindingRecoveryCoordinator);
    }

    public Task<PolicyApprovalSubmissionSnapshot> ReadSubmissionAsync(
        Guid submissionAttemptId,
        CancellationToken cancellationToken = default)
        => _authority.ReadSubmissionAsync(submissionAttemptId, cancellationToken);

    public Task<ApprovalSubmissionStateV1> AuthorizeSubmissionRecoveryAsync(
        ApprovalSubmissionRecoveryV1 recovery,
        CancellationToken cancellationToken = default)
        => _authority.RecoverAsync(recovery, cancellationToken);

    public void Dispose() => _authority.Dispose();
}

internal sealed record PolicyApprovalSubmissionAuthorityRuntime(
    PolicyApprovalSubmissionDatabaseRole Role,
    string ConnectionString,
    string SchemaName,
    string ExpectedRoleName);

internal static class PolicyApprovalSubmissionCompositionGuard
{
    private static readonly Regex SafeIdentifier = new(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void ValidateOptions(
        string connectionString,
        string schemaName,
        string expectedRoleName,
        string connectionParameterName,
        string roleParameterName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A role-specific least-privilege PostgreSQL connection is required.", connectionParameterName);
        if (string.IsNullOrWhiteSpace(schemaName) || !SafeIdentifier.IsMatch(schemaName))
            throw new ArgumentException("SchemaName is not an allowlisted PostgreSQL identifier.", nameof(schemaName));
        if (string.IsNullOrWhiteSpace(expectedRoleName) || !SafeIdentifier.IsMatch(expectedRoleName))
            throw new ArgumentException("The expected PostgreSQL role is not an allowlisted identifier.", roleParameterName);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Port == 55434 || string.Equals(builder.Database, "dps_gbrain_company", StringComparison.Ordinal))
            throw new InvalidOperationException($"Policy Approval refuses the dedicated GBrain Company PostgreSQL service for {connectionParameterName}.");
    }

    internal static string BuildBoundedConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 5
        };
        return builder.ConnectionString;
    }

    internal static string ComputePublicKeySha256(
        ReadOnlySpan<byte> publicKey,
        string parameterName)
    {
        using var key = ECDsa.Create();
        PolicyEcdsaGuard.ImportP256SubjectPublicKeyInfo(key, publicKey, parameterName);
        var canonical = key.ExportSubjectPublicKeyInfo();
        try { return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    internal static string ComputePublicKeySha256(ECDsa key)
    {
        var canonical = key.ExportSubjectPublicKeyInfo();
        try { return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    internal static void RequireExpectedPublicKey(
        ECDsa key,
        string expectedSha256,
        string authorityName)
    {
        var actual = ComputePublicKeySha256(key);
        if (!PostgresPolicyApprovalSubmissionAuthority.FixedDigestEquals(actual, expectedSha256))
            throw new UnauthorizedAccessException($"{authorityName} does not match the authority topology bound to the Release BOM.");
    }
}
