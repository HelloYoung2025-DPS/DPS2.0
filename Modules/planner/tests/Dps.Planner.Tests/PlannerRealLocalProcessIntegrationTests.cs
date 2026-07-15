using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dps.Planner.Contracts;
using Xunit;

namespace Dps.Planner.Tests;

public sealed class PlannerRealLocalProcessIntegrationTests
{
    private const int MaximumFixtureWireBytes = 32 * 1024;
    private const string FixtureProtocol = "dps.planner-local-process-fixture/v1";
    private const string EndpointMethod =
        "Dps.Planner.Tests.PlannerLocalProcessFixtureEndpoint.RunProductionPlannerBoundary";
    private const string SelectorA = "selector_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ValueC = "value_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string EvidenceD = "evidence_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    [Trait("Category", "Integration")]
    public void RealProcessRoundTripUsesProductionPlannerAndStrictContractWithoutSideEffects()
    {
        var request = CanonicalRequest() with
        {
            ActionKind = "fixture.type",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selector_ref"] = SelectorA,
                ["value_ref"] = ValueC
            }
        };
        var sentinel = Encoding.UTF8.GetBytes("must-remain-unchanged");

        using var fixture = new RunningFixture(SerializeRequest(request), "propose");
        fixture.WriteSentinel(sentinel);
        fixture.Start();
        var outcome = fixture.Complete();

        Assert.Equal(0, outcome.ExitCode);
        Assert.NotNull(outcome.Output);
        Assert.Equal(sentinel, fixture.ReadSentinel());
        var proposal = ActionProposalV2Json.Deserialize(outcome.Output);
        Assert.Equal(request.SoulId, proposal.SoulId);
        Assert.Equal(request.DeviceBindingId, proposal.DeviceBindingId);
        Assert.Equal(request.PlatformAccountId, proposal.PlatformAccountId);
        Assert.Equal("fixture.type", proposal.ActionKind);
        Assert.True(proposal.IsSideEffect);
        Assert.True(proposal.ShadowOnly);

        using var document = JsonDocument.Parse(outcome.Output);
        var names = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("approval_id", names);
        Assert.DoesNotContain("command_id", names);
        Assert.DoesNotContain("result", names);
        Assert.DoesNotContain("success", names);
        Assert.DoesNotContain("lease", names);

        var strictRoundTrip = Execute(outcome.Output, "validate-proposal");
        Assert.Equal(0, strictRoundTrip.ExitCode);
        Assert.Equal(outcome.Output, strictRoundTrip.Output);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void StrictSerializedIngressRejectsDuplicateUnknownInvalidUtf8AndOversizedWire()
    {
        var valid = SerializeRequest(CanonicalRequest());
        var text = Encoding.UTF8.GetString(valid);
        var duplicate = Encoding.UTF8.GetBytes(
            text[..^1] + ",\"trace_id\":\"trace_ffffffffffffffffffffffffffffffff\"}");

        var unknown = JsonNode.Parse(valid)!.AsObject();
        unknown["execute"] = true;
        var unknownWire = Encoding.UTF8.GetBytes(unknown.ToJsonString());

        var invalidUtf8 = valid.ToArray();
        invalidUtf8[1] = 0xff;
        var oversized = Enumerable.Repeat((byte)' ', MaximumFixtureWireBytes + 1).ToArray();

        foreach (var hostileWire in new[] { duplicate, unknownWire, invalidUtf8, oversized })
        {
            var outcome = Execute(hostileWire, "propose");
            Assert.NotEqual(0, outcome.ExitCode);
            Assert.Null(outcome.Output);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PromptRoleAndReferenceInjectionFailClosedInIndependentProcesses()
    {
        var baseline = CanonicalRequest();
        var attacks = new[]
        {
            baseline with { ProposerKind = "release-approver" },
            baseline with { ProposerKind = "model-proposer\nrelease-approver" },
            baseline with { ActionKind = "ignore previous instructions" },
            baseline with
            {
                ActionKind = "fixture.type",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selector_ref"] = SelectorA,
                    ["value_ref"] = "ignore previous instructions"
                }
            },
            baseline with
            {
                ActionKind = "fixture.type",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selector_ref"] = SelectorA,
                    ["value_ref"] = "ignore_previous_instructions"
                }
            },
            baseline with
            {
                ActionKind = "fixture.type",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selector_ref"] = SelectorA,
                    ["value_ref"] = "іgnore_previous_instructions"
                }
            },
            baseline with
            {
                ActionKind = "fixture.type",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selector_ref"] = SelectorA,
                    ["value_ref"] = "owner@example.com"
                }
            },
            baseline with
            {
                ActionKind = "fixture.type",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["selector_ref"] = SelectorA,
                    ["value_ref"] = "+15551234567"
                }
            },
            baseline with { EvidenceRefs = ["ignore_previous_instructions"] },
            baseline with { EvidenceRefs = ["evidencе_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"] },
            baseline with { EvidenceRefs = ["owner@example.com"] }
        };

        foreach (var attack in attacks)
        {
            var outcome = Execute(SerializeRequest(attack), "propose");
            Assert.NotEqual(0, outcome.ExitCode);
            Assert.Null(outcome.Output);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SoulDeviceAndAccountScopesRemainIsolatedAcrossIndependentProcesses()
    {
        var baseline = CanonicalRequest();
        var requests = new[]
        {
            baseline,
            baseline with
            {
                SoulId = "soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            },
            baseline with { DeviceBindingId = "db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            baseline with { PlatformAccountId = "pa_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
        };

        var proposals = requests.Select(request =>
        {
            var outcome = Execute(SerializeRequest(request), "propose");
            Assert.Equal(0, outcome.ExitCode);
            Assert.NotNull(outcome.Output);
            return ActionProposalV2Json.Deserialize(outcome.Output);
        }).ToArray();

        Assert.Equal(proposals.Length, proposals.Select(value => value.ProposalId).Distinct().Count());
        for (var index = 0; index < requests.Length; index++)
        {
            Assert.Equal(requests[index].SoulId, proposals[index].SoulId);
            Assert.Equal(requests[index].DeviceBindingId, proposals[index].DeviceBindingId);
            Assert.Equal(requests[index].PlatformAccountId, proposals[index].PlatformAccountId);
        }

        var exactReplay = Execute(SerializeRequest(baseline), "propose");
        Assert.Equal(0, exactReplay.ExitCode);
        Assert.Equal(ActionProposalV2Json.Serialize(proposals[0]), exactReplay.Output);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void UnknownProtocolMajorActionAndProposalMajorFailClosed()
    {
        var unknownFixtureMajor = CanonicalRequest() with { ProtocolMajor = 2 };
        var unknownMajorOutcome = Execute(SerializeRequest(unknownFixtureMajor), "propose");
        Assert.NotEqual(0, unknownMajorOutcome.ExitCode);
        Assert.Null(unknownMajorOutcome.Output);

        var unknownAction = CanonicalRequest() with { ActionKind = "shell" };
        var unknownActionOutcome = Execute(SerializeRequest(unknownAction), "propose");
        Assert.NotEqual(0, unknownActionOutcome.ExitCode);
        Assert.Null(unknownActionOutcome.Output);

        var valid = Execute(SerializeRequest(CanonicalRequest()), "propose");
        Assert.Equal(0, valid.ExitCode);
        Assert.NotNull(valid.Output);

        var futureMajor = JsonNode.Parse(valid.Output)!.AsObject();
        futureMajor["schema_version"] = "3.0.0";
        var futureMajorOutcome = Execute(
            Encoding.UTF8.GetBytes(futureMajor.ToJsonString()),
            "validate-proposal");
        Assert.NotEqual(0, futureMajorOutcome.ExitCode);
        Assert.Null(futureMajorOutcome.Output);

        var futureContract = JsonNode.Parse(valid.Output)!.AsObject();
        futureContract["contract_id"] = "action.proposal/v3";
        var futureContractOutcome = Execute(
            Encoding.UTF8.GetBytes(futureContract.ToJsonString()),
            "validate-proposal");
        Assert.NotEqual(0, futureContractOutcome.ExitCode);
        Assert.Null(futureContractOutcome.Output);

        var deprecatedV1Outcome = Execute(ActionProposalV1Json.Serialize(LegacyV1Proposal()), "validate-proposal");
        Assert.NotEqual(0, deprecatedV1Outcome.ExitCode);
        Assert.Null(deprecatedV1Outcome.Output);

        var futureAction = JsonNode.Parse(valid.Output)!.AsObject();
        futureAction["action_kind"] = "coordinate.tap";
        var futureActionOutcome = Execute(
            Encoding.UTF8.GetBytes(futureAction.ToJsonString()),
            "validate-proposal");
        Assert.NotEqual(0, futureActionOutcome.ExitCode);
        Assert.Null(futureActionOutcome.Output);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TamperedProposalScopeIdentityAndShadowAuthorityFailClosedAcrossProcesses()
    {
        var valid = Execute(SerializeRequest(CanonicalRequest()), "propose");
        Assert.Equal(0, valid.ExitCode);
        Assert.NotNull(valid.Output);

        var attacks = new[]
        {
            Tamper(
                valid.Output,
                "soul_id",
                JsonValue.Create("soul_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            Tamper(
                valid.Output,
                "device_binding_id",
                JsonValue.Create("db_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            Tamper(
                valid.Output,
                "platform_account_id",
                JsonValue.Create("pa_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            Tamper(
                valid.Output,
                "proposal_id",
                JsonValue.Create("00000000-0000-8000-8000-000000000001")),
            Tamper(valid.Output, "shadow_only", JsonValue.Create(false)),
            Tamper(valid.Output, "producer_module", JsonValue.Create("policy-approval"))
        };

        foreach (var attack in attacks)
        {
            var outcome = Execute(attack, "validate-proposal");
            Assert.NotEqual(0, outcome.ExitCode);
            Assert.Null(outcome.Output);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void KilledAfterPlanningBeforePublicationRestartsWithByteIdenticalReplay()
    {
        var request = SerializeRequest(CanonicalRequest() with
        {
            ActionKind = "wait",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["duration_ms"] = "1000"
            }
        });
        var baseline = Execute(request, "propose");
        Assert.Equal(0, baseline.ExitCode);
        Assert.NotNull(baseline.Output);

        FixtureReadyProof readyProof;
        using (var killed = new RunningFixture(request, "propose", delayBeforePublishMs: 30_000, emitReady: true))
        {
            killed.Start();
            readyProof = killed.WaitForReady();
            Assert.Equal(killed.ProcessId, readyProof.ChildProcessId);
            Assert.False(killed.OutputExists);
            var killedOutcome = killed.KillAndComplete();
            Assert.True(killedOutcome.ExitObserved);
            Assert.Equal(readyProof.ChildProcessId, killedOutcome.ProcessId);
            Assert.NotEqual(0, killedOutcome.ExitCode);
            Assert.Null(killedOutcome.Output);
        }

        var replay = Execute(request, "propose");
        Assert.Equal(0, replay.ExitCode);
        Assert.Equal(baseline.Output, replay.Output);
        Assert.Equal(readyProof.ProposalSha256, Sha256(replay.Output!));

        var secondReplay = Execute(request, "propose");
        Assert.Equal(0, secondReplay.ExitCode);
        Assert.Equal(replay.Output, secondReplay.Output);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RequiredRealLocalProcessCasesArePresentAndClassified()
    {
        var inventoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "required-real-local-process-tests.v1.json");
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(inventoryPath));
        Assert.Equal(
            "dps.planner-real-local-process-tests/v1",
            inventory.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            "REAL_LOCAL_PROCESS",
            inventory.RootElement.GetProperty("environment").GetString());
        var expected = inventory.RootElement.GetProperty("requiredTestIds")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var observed = typeof(PlannerRealLocalProcessIntegrationTests)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Where(HasIntegrationTrait)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, observed);
        Assert.Equal(7, observed.Length);
    }

    private static bool HasIntegrationTrait(MethodInfo method)
        => method.CustomAttributes.Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Count == 2
            && string.Equals(attribute.ConstructorArguments[0].Value as string, "Category", StringComparison.Ordinal)
            && string.Equals(attribute.ConstructorArguments[1].Value as string, "Integration", StringComparison.Ordinal));

    private static FixtureOutcome Execute(byte[] input, string operation)
    {
        using var fixture = new RunningFixture(input, operation);
        fixture.Start();
        return fixture.Complete();
    }

    private static FixturePlanningRequest CanonicalRequest() => new(
        FixtureProtocol,
        1,
        "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "db_11111111111111111111111111111111",
        "pa_22222222222222222222222222222222",
        "trace_33333333333333333333333333333333",
        "idem_4444444444444444444444444444444444444444444444444444444444444444",
        "2026-01-01T00:00:00Z",
        "model-proposer",
        "observe",
        new Dictionary<string, string>(StringComparer.Ordinal),
        [EvidenceD]);

    private static ActionProposalV1 LegacyV1Proposal() => new(
        ActionProposalV1.CurrentSchemaVersion,
        ActionProposalV1.CurrentContractId,
        ActionProposalV1.CurrentProducerModule,
        ActionProposalIdentity.Create(
            "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "db_11111111111111111111111111111111",
            "pa_22222222222222222222222222222222",
            "idem_4444444444444444444444444444444444444444444444444444444444444444"),
        "soul_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "db_11111111111111111111111111111111",
        "pa_22222222222222222222222222222222",
        "trace_33333333333333333333333333333333",
        "idem_4444444444444444444444444444444444444444444444444444444444444444",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        "internal",
        "observe",
        false,
        true,
        new Dictionary<string, string>(),
        ["evidence:legacy-shadow"]);

    private static byte[] SerializeRequest(FixturePlanningRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(request, FixtureWire.JsonOptions);

    private static byte[] Tamper(byte[] wire, string propertyName, JsonNode? value)
    {
        var root = JsonNode.Parse(wire)!.AsObject();
        root[propertyName] = value;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string Sha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record FixtureOutcome(
        int ProcessId,
        bool ExitObserved,
        int ExitCode,
        byte[]? Output,
        string StandardOutput,
        string StandardError);

    internal sealed record FixtureReadyProof(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("child_process_id")] int ChildProcessId,
        [property: JsonPropertyName("proposal_sha256")] string ProposalSha256);

    private sealed class RunningFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _inputPath;
        private readonly string _outputPath;
        private readonly string _readyPath;
        private readonly string _sentinelPath;
        private readonly string _operation;
        private readonly int _delayBeforePublishMs;
        private readonly bool _emitReady;
        private Process? _process;
        private Task<string>? _standardOutput;
        private Task<string>? _standardError;

        internal RunningFixture(
            byte[] input,
            string operation,
            int delayBeforePublishMs = 0,
            bool emitReady = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "dps-planner-real-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _inputPath = Path.Combine(_root, "request.json");
            _outputPath = Path.Combine(_root, "proposal.json");
            _readyPath = Path.Combine(_root, "planned.sha256");
            _sentinelPath = Path.Combine(_root, "side-effect-sentinel.txt");
            _operation = operation;
            _delayBeforePublishMs = delayBeforePublishMs;
            _emitReady = emitReady;
            WriteDurable(_inputPath, input);
        }

        internal bool OutputExists => File.Exists(_outputPath);
        internal int ProcessId
        {
            get
            {
                RequireStarted();
                return _process!.Id;
            }
        }
        internal void WriteSentinel(byte[] value) => WriteDurable(_sentinelPath, value);
        internal byte[] ReadSentinel() => File.ReadAllBytes(_sentinelPath);

        internal void Start()
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("Fixture process has already started.");
            }

            var dotnetRoot = CurrentDotnetRoot();
            var host = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(host))
            {
                throw new FileNotFoundException("The current pinned .NET host is unavailable.", host);
            }

            var start = new ProcessStartInfo
            {
                FileName = host,
                WorkingDirectory = _root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(PlannerRealLocalProcessIntegrationTests).Assembly.Location);
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add(EndpointMethod);
            start.ArgumentList.Add("-explicit");
            start.ArgumentList.Add("only");
            start.ArgumentList.Add("-parallel");
            start.ArgumentList.Add("none");
            start.ArgumentList.Add("-noColor");
            start.ArgumentList.Add("-noLogo");
            start.ArgumentList.Add("-noAutoReporters");
            start.ArgumentList.Add("-failSkips");

            start.Environment.Clear();
            start.Environment["DOTNET_ROOT"] = dotnetRoot;
            start.Environment["DOTNET_ROOT_ARM64"] = dotnetRoot;
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["HOME"] = _root;
            start.Environment["TMPDIR"] = _root;
            start.Environment["LANG"] = "C";
            start.Environment["LC_ALL"] = "C";
            start.Environment["DPS_PLANNER_FIXTURE_MODE"] = "real-local-process-v1";
            start.Environment["DPS_PLANNER_FIXTURE_OPERATION"] = _operation;
            start.Environment["DPS_PLANNER_FIXTURE_INPUT"] = _inputPath;
            start.Environment["DPS_PLANNER_FIXTURE_OUTPUT"] = _outputPath;
            start.Environment["DPS_PLANNER_FIXTURE_DELAY_MS"] = _delayBeforePublishMs.ToString(CultureInfo.InvariantCulture);
            start.Environment["DPS_PLANNER_FIXTURE_READY"] = _emitReady ? _readyPath : string.Empty;

            _process = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start the real local planner fixture process.");
            _standardOutput = _process.StandardOutput.ReadToEndAsync();
            _standardError = _process.StandardError.ReadToEndAsync();
        }

        internal FixtureReadyProof WaitForReady()
        {
            RequireStarted();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                try
                {
                    var proof = ReadReadyFromStableHandle();
                    if (proof.ChildProcessId != _process!.Id)
                    {
                        throw new InvalidDataException(
                            "Fixture ready proof is not bound to the child process handle.");
                    }
                    if (_process.HasExited)
                    {
                        throw new InvalidOperationException(
                            "Fixture exited before its ready proof could be accepted.");
                    }
                    return proof;
                }
                catch (FileNotFoundException)
                {
                    // The child has not atomically renamed the ready proof yet.
                }
                catch (IOException)
                {
                    // Bounded retry for transient sharing or visibility delays.
                }

                if (_process!.HasExited)
                {
                    throw new InvalidOperationException("Fixture exited before the post-planning crash window was reached.");
                }
                Thread.Sleep(20);
            }
            throw new TimeoutException("Fixture did not reach the post-planning crash window.");
        }

        private FixtureReadyProof ReadReadyFromStableHandle()
        {
            using var stream = new FileStream(
                _readyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 512,
                FileOptions.SequentialScan);
            if (stream.Length is < 1 or > 512)
            {
                throw new InvalidDataException("Fixture ready proof has an invalid size.");
            }
            var wire = new byte[checked((int)stream.Length)];
            stream.ReadExactly(wire);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("Fixture ready proof changed while held open.");
            }

            using var document = JsonDocument.Parse(wire, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Fixture ready proof must be an object.");
            }
            var required = new HashSet<string>(
                ["schema_version", "child_process_id", "proposal_sha256"],
                StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!required.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Fixture ready proof contains an unknown or duplicate property.");
                }
            }
            if (!seen.SetEquals(required))
            {
                throw new InvalidDataException("Fixture ready proof is missing a required property.");
            }

            var proof = JsonSerializer.Deserialize<FixtureReadyProof>(wire, FixtureWire.JsonOptions)
                ?? throw new InvalidDataException("Fixture ready proof cannot be null.");
            if (!string.Equals(
                    proof.SchemaVersion,
                    "dps.planner-fixture-ready/v1",
                    StringComparison.Ordinal)
                || proof.ChildProcessId <= 0
                || proof.ProposalSha256.Length != 64
                || proof.ProposalSha256.Any(
                    character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new InvalidDataException("Fixture ready proof is not canonical.");
            }
            return proof;
        }

        internal FixtureOutcome KillAndComplete()
        {
            RequireStarted();
            KillAndRequireExit("forced crash-window kill");
            return Complete();
        }

        internal FixtureOutcome Complete()
        {
            RequireStarted();
            var processId = _process!.Id;
            if (!_process!.WaitForExit(20_000))
            {
                try
                {
                    KillAndRequireExit("fixture deadline cleanup");
                }
                catch (Exception cleanupFailure)
                {
                    throw new TimeoutException(
                        "Real local planner fixture exceeded its 20 second deadline and cleanup did not prove process exit.",
                        cleanupFailure);
                }
                throw new TimeoutException("Real local planner fixture exceeded its 20 second deadline.");
            }

            var stdout = _standardOutput!.GetAwaiter().GetResult();
            var stderr = _standardError!.GetAwaiter().GetResult();
            var output = File.Exists(_outputPath) ? File.ReadAllBytes(_outputPath) : null;
            return new FixtureOutcome(
                processId,
                _process.HasExited,
                _process.ExitCode,
                output,
                stdout,
                stderr);
        }

        public void Dispose()
        {
            Exception? cleanupFailure = null;
            try
            {
                try
                {
                    KillAndRequireExit("fixture dispose cleanup");
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
            finally
            {
                _process?.Dispose();
                for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
                {
                    try
                    {
                        Directory.Delete(_root, recursive: true);
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(20);
                    }
                }
            }

            if (cleanupFailure is not null)
            {
                throw new InvalidOperationException(
                    "Real local planner fixture cleanup could not prove child process exit.",
                    cleanupFailure);
            }
        }

        private void KillAndRequireExit(string reason)
        {
            if (_process is null || _process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
            if (!_process.WaitForExit(5_000))
            {
                throw new InvalidOperationException(
                    $"The real local planner fixture did not exit within five seconds after {reason}.");
            }
        }

        private void RequireStarted()
        {
            if (_process is null || _standardOutput is null || _standardError is null)
            {
                throw new InvalidOperationException("Fixture process has not started.");
            }
        }

        private static string CurrentDotnetRoot()
        {
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var shared = runtime.Parent?.Parent;
            var root = shared?.Parent;
            return root?.FullName
                ?? throw new InvalidOperationException("Cannot derive the pinned .NET root from the active runtime.");
        }
    }

    internal static void WriteDurable(string path, byte[] value)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }

    internal static void WriteDurableAtomic(string path, byte[] value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Atomic fixture path has no directory.");
        var temporary = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteDurable(temporary, value);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal sealed record FixturePlanningRequest(
        [property: JsonPropertyName("fixture_protocol")] string FixtureProtocol,
        [property: JsonPropertyName("protocol_major")] int ProtocolMajor,
        [property: JsonPropertyName("soul_id")] string SoulId,
        [property: JsonPropertyName("device_binding_id")] string DeviceBindingId,
        [property: JsonPropertyName("platform_account_id")] string PlatformAccountId,
        [property: JsonPropertyName("trace_id")] string TraceId,
        [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
        [property: JsonPropertyName("occurred_at")] string OccurredAt,
        [property: JsonPropertyName("proposer_kind")] string ProposerKind,
        [property: JsonPropertyName("action_kind")] string ActionKind,
        [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, string> Parameters,
        [property: JsonPropertyName("evidence_refs")] IReadOnlyList<string> EvidenceRefs);

    internal static class FixtureWire
    {
        private static readonly HashSet<string> RequiredProperties = new(
            [
                "fixture_protocol", "protocol_major", "soul_id", "device_binding_id",
                "platform_account_id", "trace_id", "idempotency_key", "occurred_at",
                "proposer_kind", "action_kind", "parameters", "evidence_refs"
            ],
            StringComparer.Ordinal);

        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
        {
            AllowTrailingCommas = false,
            MaxDepth = 8,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };

        internal static PlanningRequest DeserializePlanningRequest(byte[] wire)
        {
            if (wire.Length is < 1 or > MaximumFixtureWireBytes)
            {
                throw new JsonException("Fixture request must be bounded non-empty UTF-8.");
            }

            using var document = JsonDocument.Parse(wire, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Fixture request must be an object.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RequiredProperties.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new JsonException("Fixture request contains an unknown or duplicate property.");
                }
            }
            if (!seen.SetEquals(RequiredProperties))
            {
                throw new JsonException("Fixture request is missing a required property.");
            }

            var parameters = document.RootElement.GetProperty("parameters");
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Fixture parameters must be an object.");
            }
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in parameters.EnumerateObject())
            {
                if (!parameterNames.Add(property.Name))
                {
                    throw new JsonException("Fixture parameters contain a duplicate property.");
                }
            }

            var value = JsonSerializer.Deserialize<FixturePlanningRequest>(wire, JsonOptions)
                ?? throw new JsonException("Fixture request cannot be null.");
            if (!string.Equals(value.FixtureProtocol, FixtureProtocol, StringComparison.Ordinal)
                || value.ProtocolMajor != 1)
            {
                throw new NotSupportedException("Unknown planner fixture protocol major.");
            }
            if (!DateTimeOffset.TryParseExact(
                    value.OccurredAt,
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var occurredAt)
                || occurredAt.Offset != TimeSpan.Zero)
            {
                throw new JsonException("Fixture occurred_at must be canonical UTC RFC 3339.");
            }

            return new PlanningRequest(
                value.SoulId,
                value.DeviceBindingId,
                value.PlatformAccountId,
                value.TraceId,
                value.IdempotencyKey,
                occurredAt,
                value.ProposerKind,
                value.ActionKind,
                value.Parameters,
                value.EvidenceRefs);
        }
    }
}

public sealed class PlannerLocalProcessFixtureEndpoint
{
    [Fact(Explicit = true)]
    [Trait("Category", "LocalProcessFixture")]
    public void RunProductionPlannerBoundary()
    {
        RequireEnvironment("DPS_PLANNER_FIXTURE_MODE", "real-local-process-v1");
        var operation = RequireEnvironment("DPS_PLANNER_FIXTURE_OPERATION");
        var inputPath = RequireSafePath("DPS_PLANNER_FIXTURE_INPUT");
        var outputPath = RequireSafePath("DPS_PLANNER_FIXTURE_OUTPUT", mustExist: false);
        RequireSameDirectory(inputPath, outputPath);
        var input = File.ReadAllBytes(inputPath);

        byte[] output = operation switch
        {
            "propose" => ActionProposalV2Json.Serialize(
                new ShadowActionPlanner().Propose(
                    PlannerRealLocalProcessIntegrationTests.FixtureWire.DeserializePlanningRequest(input))),
            "validate-proposal" => ActionProposalV2Json.Serialize(ActionProposalV2Json.Deserialize(input)),
            _ => throw new NotSupportedException("Unknown fixture operation.")
        };

        var readyValue = Environment.GetEnvironmentVariable("DPS_PLANNER_FIXTURE_READY");
        if (!string.IsNullOrEmpty(readyValue))
        {
            var readyPath = RequireSafePath("DPS_PLANNER_FIXTURE_READY", mustExist: false);
            RequireSameDirectory(inputPath, readyPath);
            var proof = new PlannerRealLocalProcessIntegrationTests.FixtureReadyProof(
                "dps.planner-fixture-ready/v1",
                Environment.ProcessId,
                Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant());
            PlannerRealLocalProcessIntegrationTests.WriteDurableAtomic(
                readyPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    proof,
                    PlannerRealLocalProcessIntegrationTests.FixtureWire.JsonOptions));
        }

        var delayText = RequireEnvironment("DPS_PLANNER_FIXTURE_DELAY_MS");
        if (!int.TryParse(delayText, NumberStyles.None, CultureInfo.InvariantCulture, out var delay)
            || delay is < 0 or > 60_000)
        {
            throw new InvalidOperationException("Fixture delay is invalid.");
        }
        if (delay > 0)
        {
            Thread.Sleep(delay);
        }

        PlannerRealLocalProcessIntegrationTests.WriteDurable(outputPath, output);
    }

    private static string RequireEnvironment(string name, string? exact = null)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value)
            || (exact is not null && !string.Equals(value, exact, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Required fixture environment is absent or invalid.");
        }
        return value;
    }

    private static string RequireSafePath(string name, bool mustExist = true)
    {
        var value = RequireEnvironment(name);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException("Fixture paths must be absolute.");
        }
        var full = Path.GetFullPath(value);
        if (mustExist && !File.Exists(full))
        {
            throw new FileNotFoundException("Fixture input is missing.", full);
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Fixture paths cannot be links.");
        }
        if (!mustExist && (File.Exists(full) || Directory.Exists(full)))
        {
            throw new InvalidOperationException("Fixture output paths must not exist.");
        }
        return full;
    }

    private static void RequireSameDirectory(string left, string right)
    {
        if (!string.Equals(
                Path.GetDirectoryName(left),
                Path.GetDirectoryName(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixture IPC paths must share one isolated directory.");
        }
    }
}
