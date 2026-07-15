using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed class PolicyApprovalSubmissionCompositionTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=dps_policy_test;Username=role;Password=do-not-print;Pooling=false";

    [Fact]
    [Trait("Category", "Contract")]
    public void PublicSubmissionClientsAndOptionsAreRoleSpecificAndNonInterchangeable()
    {
        AssertPublicProperties<PostgresPolicyApprovalOptions>(
            "RuntimeConnectionString",
            "SchemaName",
            "ExpectedRuntimeRoleName");
        AssertPublicProperties<PostgresPolicyApprovalSubmissionExecutorOptions>(
            "ExecutorConnectionString",
            "SchemaName",
            "ExpectedExecutorRoleName");
        AssertPublicProperties<PostgresPolicyApprovalSubmissionReconciliationOptions>(
            "ReconciliationConnectionString",
            "SchemaName",
            "ExpectedReconciliationRoleName");
        AssertPublicProperties<PostgresPolicyApprovalSubmissionRecoveryOptions>(
            "RecoveryConnectionString",
            "SchemaName",
            "ExpectedRecoveryRoleName");

        AssertPublicInstanceMethods<PolicyApprovalExecutionFenceClient>(
            "AcquireAsync",
            "Dispose",
            "ReadSubmissionAsync");
        AssertPublicInstanceMethods<PolicyApprovalSubmissionReconcilerClient>(
            "Dispose",
            "ReadSubmissionAsync",
            "ReconcileSubmissionAsync");
        AssertPublicInstanceMethods<PolicyApprovalSubmissionRecoveryClient>(
            "AuthorizeSubmissionRecoveryAsync",
            "Dispose",
            "ReadSubmissionAsync");
        AssertPublicInstanceMethodsExcludingAccessors<PolicyApprovalExecutionFenceLease>(
            "DisposeAsync",
            "RevalidateForNativeDispatchAsync",
            "SubmitNativeOnceAsync");
        Assert.DoesNotContain(
            typeof(PolicyApprovalExecutionFenceLease).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name is "BeginSubmissionAsync" or
                "AcknowledgeSubmissionAsync" or
                "QuarantineUnknownSubmissionAsync");

        AssertFactoryOptions<PolicyApprovalExecutionFenceClient,
            PostgresPolicyApprovalSubmissionExecutorOptions>();
        AssertFactoryOptions<PolicyApprovalSubmissionReconcilerClient,
            PostgresPolicyApprovalSubmissionReconciliationOptions>();
        AssertFactoryOptions<PolicyApprovalSubmissionRecoveryClient,
            PostgresPolicyApprovalSubmissionRecoveryOptions>();
        Assert.NotEqual(
            typeof(PostgresPolicyApprovalSubmissionExecutorOptions),
            typeof(PostgresPolicyApprovalSubmissionReconciliationOptions));
        Assert.NotEqual(
            typeof(PostgresPolicyApprovalSubmissionExecutorOptions),
            typeof(PostgresPolicyApprovalSubmissionRecoveryOptions));
        Assert.NotEqual(
            typeof(PostgresPolicyApprovalSubmissionReconciliationOptions),
            typeof(PostgresPolicyApprovalSubmissionRecoveryOptions));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RoleSpecificOptionsRedactCredentialsAndEightAuthorityTopologyFailsClosed()
    {
        var executorOptions = new PostgresPolicyApprovalSubmissionExecutorOptions(
            ConnectionString,
            "dps_policy_test",
            "submission_executor");
        var reconciliationOptions = new PostgresPolicyApprovalSubmissionReconciliationOptions(
            ConnectionString,
            "dps_policy_test",
            "submission_reconciler");
        var recoveryOptions = new PostgresPolicyApprovalSubmissionRecoveryOptions(
            ConnectionString,
            "dps_policy_test",
            "submission_recovery");
        executorOptions.Validate();
        reconciliationOptions.Validate();
        recoveryOptions.Validate();
        Assert.DoesNotContain("do-not-print", executorOptions.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-print", reconciliationOptions.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-print", recoveryOptions.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PostgresPolicyApprovalSubmissionExecutorOptions(
            "Host=localhost;Database=dps_policy_test;Username=role;Password=secret",
            "dps_policy_test",
            "submission_executor").Validate());
        using var evaluation = Key();
        using var promotion = Key();
        using var revocation = Key();
        using var fence = Key();
        using var executor = Key();
        using var reconciliation = Key();
        using var recovery = Key();
        using var state = Key();
        var valid = Topology(
            evaluation,
            promotion,
            revocation,
            fence,
            executor,
            reconciliation,
            recovery,
            state);
        valid.Validate();

        Assert.Throws<ArgumentException>(() => PolicyApprovalSubmissionAuthorityTopology.Create(
            evaluation.ExportSubjectPublicKeyInfo(),
            promotion.ExportSubjectPublicKeyInfo(),
            revocation.ExportSubjectPublicKeyInfo(),
            evaluation.ExportSubjectPublicKeyInfo(),
            executor.ExportSubjectPublicKeyInfo(),
            reconciliation.ExportSubjectPublicKeyInfo(),
            recovery.ExportSubjectPublicKeyInfo(),
            state.ExportSubjectPublicKeyInfo()));

        var statePrivate = state.ExportPkcs8PrivateKey();
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                PolicyApprovalSubmissionReconcilerClient.CreateProduction(
                    reconciliationOptions,
                    valid,
                    executor.ExportSubjectPublicKeyInfo(),
                    recovery.ExportSubjectPublicKeyInfo(),
                    statePrivate));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(statePrivate);
        }
    }

    private static PolicyApprovalSubmissionAuthorityTopology Topology(
        ECDsa evaluation,
        ECDsa promotion,
        ECDsa revocation,
        ECDsa fence,
        ECDsa executor,
        ECDsa reconciliation,
        ECDsa recovery,
        ECDsa state)
        => PolicyApprovalSubmissionAuthorityTopology.Create(
            evaluation.ExportSubjectPublicKeyInfo(),
            promotion.ExportSubjectPublicKeyInfo(),
            revocation.ExportSubjectPublicKeyInfo(),
            fence.ExportSubjectPublicKeyInfo(),
            executor.ExportSubjectPublicKeyInfo(),
            reconciliation.ExportSubjectPublicKeyInfo(),
            recovery.ExportSubjectPublicKeyInfo(),
            state.ExportSubjectPublicKeyInfo());

    private static ECDsa Key() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static void AssertPublicProperties<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static void AssertPublicInstanceMethods<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static void AssertPublicInstanceMethodsExcludingAccessors<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static void AssertFactoryOptions<TClient, TOptions>()
    {
        var factories = typeof(TClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => method.Name == "CreateProduction")
            .ToArray();
        var factory = Assert.Single(factories);
        Assert.Equal(typeof(TOptions), factory.GetParameters()[0].ParameterType);
    }
}
