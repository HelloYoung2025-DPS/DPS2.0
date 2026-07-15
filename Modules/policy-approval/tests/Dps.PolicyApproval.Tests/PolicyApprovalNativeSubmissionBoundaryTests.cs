using System.Reflection;
using Dps.PolicyApproval.Contracts;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed class PolicyApprovalNativeSubmissionBoundaryTests
{
    [Fact, Trait("Category", "Contract")]
    public void NativeTruthIsThreeStateAndTransportAcknowledgementIsNotSuccess()
    {
        Assert.Equal("CONFIRMED_SUCCESS", PolicyApprovalNativeResultTruth.ConfirmedSuccess);
        Assert.Equal("FAILED", PolicyApprovalNativeResultTruth.Failed);
        Assert.Equal("UNKNOWN_OUTCOME", PolicyApprovalNativeResultTruth.UnknownOutcome);
        Assert.DoesNotContain(
            ApprovalSubmissionStateV1.SubmissionAcknowledged,
            new[]
            {
                PolicyApprovalNativeResultTruth.ConfirmedSuccess,
                PolicyApprovalNativeResultTruth.Failed,
                PolicyApprovalNativeResultTruth.UnknownOutcome
            });
    }

    [Fact, Trait("Category", "Contract")]
    public void PolicyContractAssemblyDoesNotPublishNativeStopTypesOrResources()
    {
        var contracts = typeof(ApprovalDecisionV1).Assembly;
        Assert.DoesNotContain(
            contracts.GetExportedTypes(),
            static type => type.Name.Contains("NativeStop", StringComparison.Ordinal) ||
                type.Name.Contains("LegacyNativeStop", StringComparison.Ordinal));
        Assert.DoesNotContain(
            contracts.GetManifestResourceNames(),
            static name => name.Contains("native.stop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Contract")]
    public void PublicNativeDispatchHasNoCallbackOrCallerSelectedTrustSurface()
    {
        var dispatch = typeof(PolicyApprovalExecutionFenceLease)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(static method => method.Name == "SubmitNativeOnceAsync");
        Assert.Equal(typeof(Task), dispatch.ReturnType);
        var cancellation = Assert.Single(dispatch.GetParameters());
        Assert.Equal(typeof(CancellationToken), cancellation.ParameterType);

        var production = typeof(PolicyApprovalExecutionFenceClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single(static method => method.Name == "CreateProduction");
        Assert.Equal(7, production.GetParameters().Length);
        Assert.Null(typeof(PolicyApprovalExecutionFenceClient).GetMethod(
            "CreateTrustedComposition",
            BindingFlags.NonPublic | BindingFlags.Static));

        Assert.DoesNotContain(
            typeof(PolicyApprovalExecutionFenceClient).Assembly.GetExportedTypes(),
            static type => type.Name.Contains("NativeSubmissionCallback", StringComparison.Ordinal) ||
                type.Name.Contains("NativeStopTrust", StringComparison.Ordinal));
    }
}
