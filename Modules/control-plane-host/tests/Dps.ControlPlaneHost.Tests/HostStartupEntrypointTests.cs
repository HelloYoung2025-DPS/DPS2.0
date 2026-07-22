using Xunit;

namespace Dps.ControlPlaneHost.Tests;

// F3 close-out (R0-C): this suite pins the required negative verification
// that no production composition root exists, so the in-memory release
// binding truth store (InMemoryReleaseBindingTruthStore.CreateTestOnly) is
// unreachable from any production entrypoint. Per RebuildPlan §4.3 (PR#6 v2)
// M4 boundary, this module stays releaseEligible=false with no production
// composition entrypoint until milestone M4; HostStartup.Run is the only
// process entrypoint (Program.Main forwards to it) and it must fail closed
// with exit code 64 — constructing nothing — for every invocation other
// than exactly "--self-check".
public sealed class HostStartupEntrypointTests
{
    [Fact, Trait("Category", "Unit")]
    public void SelfCheckIsTheOnlyAcceptedInvocation()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = HostStartup.Run(["--self-check"], output, error);

        Assert.Equal(0, exit);
        Assert.Contains("self-check PASS", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact, Trait("Category", "Unit")]
    public void MissingUnknownAndExtraArgumentsFailClosedWithExit64()
    {
        string[][] rejected =
        [
            [],                              // no production entrypoint exists at all
            ["serve"],                       // no production mode is wired
            ["--run"],
            ["--Self-Check"],                // the accepted argument match is exact
            ["--self-check", "extra"],       // exactly one argument is required
            ["--self-check", "--self-check"],
            ["extra", "--self-check"],
        ];

        foreach (var args in rejected)
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exit = HostStartup.Run(args, output, error);

            Assert.Equal(64, exit);
            Assert.Empty(output.ToString());
            Assert.Contains("accepts only --self-check", error.ToString());
        }
    }

    [Fact, Trait("Category", "Unit")]
    public void NullArgumentsOrWritersFailClosed()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HostStartup.Run(null!, TextWriter.Null, TextWriter.Null));
        Assert.Throws<ArgumentNullException>(() =>
            HostStartup.Run(["--self-check"], null!, TextWriter.Null));
        Assert.Throws<ArgumentNullException>(() =>
            HostStartup.Run(["--self-check"], TextWriter.Null, null!));
    }
}
