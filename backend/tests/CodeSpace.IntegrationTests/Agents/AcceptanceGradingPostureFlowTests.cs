using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (real Postgres, the production executor, admission and local acceptance lane, the real
/// <see cref="SupervisorAcceptanceGrader"/> on the real <see cref="LocalProcessRunner"/>; only the agent harness is
/// scripted): an acceptance setup step reaches exactly the network its PRODUCING run had. The setup is an operator
/// command (<c>npm ci</c>, <c>pip install</c>) that executes manifests the agent wrote, and it runs after the agent's
/// sandbox is gone. So a Standard run's setup must not reach a loopback sink the Standard agent itself could not
/// reach, while an Unleashed run's setup still can.
///
/// <para>Two proofs per run. The specs the grader hands its runner are recorded and fed through
/// <see cref="LocalProcessRunner.ChildCommand"/> on a stand-in bwrap path: the argv a confining Linux host launches,
/// provable on any host. The REAL reachability of the sink is asserted wherever this host confines (bubblewrap
/// present). A macOS development host does not confine, so there it reaches the sink whatever the posture. The
/// sandbox lane asserts the same posture against the real kernel (<c>AcceptanceGradingPostureE2ETests</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AcceptanceGradingPostureFlowTests(PostgresFixture fixture)
{
    private const string FakeBwrap = "/usr/bin/bwrap";

    [Theory]
    [InlineData(AgentAutonomyLevel.Standard, false)]
    [InlineData(AgentAutonomyLevel.Unleashed, true)]
    public async Task An_acceptance_setup_reaches_only_the_network_its_producing_run_had(AgentAutonomyLevel tier, bool runHasNetwork)
    {
        using var context = new SinkContext();

        var script = $"printf ok > report.txt; (echo agent >/dev/tcp/127.0.0.1/{context.Port}) 2>/dev/null; echo produced";
        var acceptance = new SupervisorAcceptanceSpec { Command = ["/bin/sh", "-c", "test -f report.txt"], SetupCommand = ["/bin/bash", "-c", $"echo setup >/dev/tcp/127.0.0.1/{context.Port}"] };
        var task = new AgentTask { Goal = "write the report", Harness = PostureHarness.HarnessKind, Model = "test-model", WorkspaceDirectory = context.Workspace, Autonomy = tier, Permissions = AgentAutonomyPolicy.Derive(tier), MaxReviseRounds = 0, TimeoutSeconds = 30, Acceptance = acceptance };

        var (stored, result, evidence) = await ExecuteAsync(task, new PostureHarness(script), context.Recorded);

        stored.Permissions.Network.ShouldBe(runHasNetwork ? AgentNetworkAccess.On : AgentNetworkAccess.Off, "fixture check: admission kept the tier's network grant — a lowered Sandbox:MaxAutonomy in this environment would void the Unleashed half");

        var (setup, check) = RecordedSteps(context.Recorded, result);
        var expected = AcceptanceGradingPosturePolicy.For(stored);

        setup.AllowNetwork.ShouldBe(runHasNetwork, "the setup asked for the network and got exactly what the producing run had");
        Argv(setup).Contains("--unshare-net").ShouldBe(!runHasNetwork, "on a confining host the Standard setup is severed and the Unleashed one shares the host network");
        check.AllowNetwork.ShouldBeFalse("the check's network cut is unchanged");
        setup.MaxMemoryMb.ShouldBe(expected.MaxMemoryMb, "the setup runs under the producing tier's memory ceiling");
        check.MaxMemoryMb.ShouldBe(expected.MaxMemoryMb, "and so does the check");
        check.MaxCpuPercent.ShouldBe(expected.MaxCpuPercent);

        var severed = !runHasNetwork && BubblewrapSandbox.Available is not null;
        evidence.ShouldNotBeNull("both outcomes bind evidence: a TestsPass check's output, or a severed setup's failure");
        evidence.StartsWith(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix, StringComparison.Ordinal).ShouldBe(severed, "a setup the sandbox severed records one notice saying so, at the head of the evidence — and a host that did not confine claims nothing it did not do");

        var hits = context.DrainHits();
        if (runHasNetwork) hits.ShouldContain("setup", customMessage: $"the Unleashed setup should have reached the loopback sink on port {context.Port} — check that /bin/bash supports /dev/tcp here");

        if (BubblewrapSandbox.Available is null) return;   // this host does not confine: below is the confined-host answer

        hits.Contains("agent").ShouldBe(runHasNetwork, "fixture check: the agent itself reaches the sink only when its tier grants the network");
        hits.Contains("setup").ShouldBe(runHasNetwork, "the setup never reaches a sink its producing agent could not");
        result.AcceptancePassed.ShouldBe(runHasNetwork, result.AcceptanceDetail);
        if (!runHasNetwork) result.AcceptanceDetail.ShouldNotBeNull().ShouldStartWith(AgentAcceptanceContract.SetupSeveredDetailPrefix, customMessage: "the severed setup's failure is decided by the posture, so an authored retry does not re-buy the run");
    }

    private async Task<(AgentTask Stored, AgentRunResult Result, string? Evidence)> ExecuteAsync(AgentTask task, PostureHarness harness, List<SandboxSpec> recorded)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        Guid runId;
        using (var admission = fixture.BeginScopeAs(userId, teamId))
            runId = (await admission.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, cancellationToken: CancellationToken.None)).Id;

        using (var execution = fixture.BeginScope(builder => RegisterRecordingGrader(builder, harness, recorded)))
            await execution.Resolve<AgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

        using var scope = fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        var stored = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;
        var bytes = result.AcceptanceEvidenceId is { } id ? await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, id, CancellationToken.None) : null;

        return (stored, result, bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes.Bytes));
    }

    /// <summary>
    /// The scripted harness, and the PRODUCTION grader built over a runner registry that records every spec it is
    /// handed before running it for real. The executor reaches the local lane's grader through a child scope it opens
    /// itself, and the container's own scope factory opens those under the root. So the scope factory is replaced too,
    /// and the executor's child scopes open under this test's scope, where the recording grader is registered.
    /// </summary>
    private static void RegisterRecordingGrader(ContainerBuilder builder, PostureHarness harness, List<SandboxSpec> recorded)
    {
        builder.RegisterInstance(new AgentHarnessRegistry([harness])).As<IAgentHarnessRegistry>();
        builder.Register(c => new TestScopeFactory(c.Resolve<ILifetimeScope>())).As<IServiceScopeFactory>().InstancePerLifetimeScope();
        builder.Register(c => new SupervisorAcceptanceGrader(c.Resolve<IAgentWorkspaceResolver>(), c.Resolve<IWorkspaceProviderRegistry>(), new RecordingRunners(c.Resolve<ISandboxRunnerRegistry>(), recorded), c.Resolve<IBenchmarkGraderRegistry>(), c.Resolve<IArtifactOffloader>(), c.Resolve<IArtifactStore>(), c.Resolve<IArtifactManifestStore>(), c.Resolve<ILogger<SupervisorAcceptanceGrader>>())).As<ISupervisorAcceptanceGrader>().InstancePerLifetimeScope();
    }

    private static (SandboxSpec Setup, SandboxSpec Check) RecordedSteps(List<SandboxSpec> recorded, AgentRunResult result)
    {
        recorded.Select(s => s.Command).ShouldBe(new[] { "/bin/bash", "/bin/sh" }, $"fixture check: the grade ran its setup step and then its check through the recording registry (run {result.Status}, acceptance {result.AcceptancePassed}: {result.AcceptanceDetail}; error: {result.Error})");

        return (recorded[0], recorded[1]);
    }

    private static IReadOnlyList<string> Argv(SandboxSpec spec) =>
        LocalProcessRunner.ChildCommand(new LocalProcessRunner.CommandIsolationContext(spec, null, null, Array.Empty<string>(), Array.Empty<string>()), FakeBwrap, prlimit: null);

    /// <summary>A loopback sink on a port the kernel picked, the workspace the agent writes into, and the specs the grader ran; all torn down on dispose.</summary>
    private sealed class SinkContext : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public SinkContext()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Directory.CreateDirectory(Workspace);
        }

        public int Port { get; }
        public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "cs-grade-posture-" + Guid.NewGuid().ToString("N"));
        public List<SandboxSpec> Recorded { get; } = new();

        /// <summary>Every marker a client wrote before the run ended. Each client closed its connection before exiting, so the run has finished every handshake by now, and what is pending is all there is.</summary>
        public IReadOnlyList<string> DrainHits()
        {
            var hits = new List<string>();

            while (_listener.Pending())
            {
                using var client = _listener.AcceptTcpClient();
                using var reader = new StreamReader(client.GetStream());
                hits.Add(reader.ReadLine() ?? "");
            }

            return hits;
        }

        public void Dispose()
        {
            _listener.Stop();
            try { Directory.Delete(Workspace, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class TestScopeFactory(ILifetimeScope parent) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(parent.BeginLifetimeScope());
    }

    private sealed class TestScope(ILifetimeScope scope) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new AutofacServiceProvider(scope);
        public void Dispose() => scope.Dispose();
    }

    private sealed class RecordingRunners(ISandboxRunnerRegistry inner, List<SandboxSpec> recorded) : ISandboxRunnerRegistry
    {
        public IReadOnlyList<ISandboxRunner> All => inner.All;
        public ISandboxRunner Resolve(string kind) => new RecordingRunner(inner.Resolve(kind), recorded);
    }

    private sealed class RecordingRunner(ISandboxRunner inner, List<SandboxSpec> recorded) : ISandboxRunner, ISandboxEgressEnforcement
    {
        public string Kind => inner.Kind;

        /// <summary>The real runner's own answer about what it enforces on this host — recording a spec must not change what the grade can claim about it.</summary>
        public SandboxEgressMode EnforcedEgress(SandboxSpec spec) => ((ISandboxEgressEnforcement)inner).EnforcedEgress(spec);

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            recorded.Add(spec);
            return inner.RunAsync(spec, cancellationToken);
        }
    }

    /// <summary>A scripted agent whose sandbox asks for the network exactly as the shipped harnesses do: from the task's admitted permissions.</summary>
    private sealed class PostureHarness(string script) : IAgentHarness
    {
        public const string HarnessKind = "grading-posture-test";
        public string Kind => HarnessKind;
        public string Version => "test";
        public IReadOnlyList<string> Models => ["test-model"];
        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/bash", Args = ["-c", script], WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = 30, AllowNetwork = task.Permissions.Network == AgentNetworkAccess.On };
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => [new() { Kind = AgentEventKind.AssistantMessage, Text = rawLine }];
        public IAgentEventFolder CreateFolder() => ScriptedFolders.Result();
    }
}
