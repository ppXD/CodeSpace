using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 High fidelity: the real <see cref="SupervisorAcceptanceGrader"/> on the real <see cref="LocalProcessRunner"/>
/// under real bubblewrap, with only the evidence store in memory. The grader runs an acceptance setup step whose
/// network comes from the producing run's posture, and the setup tries to reach a listener on the host's loopback.
/// A network-off producer's setup is refused by the kernel, while a network-granting producer's setup connects.
/// Before this, every setup step shared the host network whatever the tier: it ran manifests the agent wrote, with
/// egress the agent never had. The argv half of the same proof runs on any host (<c>AcceptanceGradingPostureTests</c>).
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class AcceptanceGradingPostureE2ETests
{
    private const int ConnectionRefused = 7;

    [KernelTheory]
    [InlineData(AgentAutonomyLevel.Confined, false)]
    [InlineData(AgentAutonomyLevel.Standard, false)]
    [InlineData(AgentAutonomyLevel.Trusted, true)]
    [InlineData(AgentAutonomyLevel.Unleashed, true)]
    public async Task A_grades_setup_reaches_a_host_listener_only_when_its_producing_run_could(AgentAutonomyLevel tier, bool reaches)
    {
        BubblewrapSandbox.Available.ShouldNotBeNull("the kernel suite must execute confinement, never silently degrade");

        using var context = new GradeContext();
        var connect = $"import socket,sys\ntry:\n s=socket.create_connection(('127.0.0.1',{context.Port}),timeout=2); s.close()\nexcept OSError:\n sys.exit({ConnectionRefused})";
        var spec = new SupervisorAcceptanceSpec { Command = ["/bin/sh", "-c", "exit 0"], SetupCommand = ["/usr/bin/python3", "-c", connect] };
        var posture = AcceptanceGradingPosturePolicy.Derive(tier, AgentAutonomyPolicy.Derive(tier), AgentAutonomyLevel.Unleashed, hostMemoryBudgetMb: null);

        var grade = await context.Grader.GradeDirectoryAsync(new DirectoryAcceptanceGradeRequest { Directory = context.Workspace, Spec = spec, TeamId = Guid.NewGuid(), TimeoutSeconds = 30, Posture = posture }, CancellationToken.None);

        grade.Passed.ShouldBe(reaches, $"{tier}: {grade.Detail} — the setup's connect to 127.0.0.1:{context.Port} should {(reaches ? "succeed on the shared host network" : "be refused inside a fresh network namespace")}; diagnose with `bwrap --unshare-net python3 -c ...` by hand");

        if (reaches) return;

        grade.Detail.ShouldStartWith(AgentAcceptanceContract.SetupSeveredDetailPrefix, customMessage: "the refused setup is an Environment failure the posture decided, never a verdict on the work");
        grade.Class.ShouldBe(GradeFailureClass.Environment);
        AgentAcceptanceContract.IsInfraFailure(grade.Detail, workPresent: true).ShouldBeTrue("no revise round is spent on it");
        AgentAcceptanceContract.IsDecidedByGradePosture(grade.Detail).ShouldBeTrue("and no respawn: the same stored task severs it again");
        context.Artifacts.Texts.ShouldHaveSingleItem().ShouldStartWith(AcceptanceGradingPosturePolicy.SetupNetworkNoticePrefix + "off", customMessage: "the evidence says the sandbox severed the setup, so an operator knows why it could not download");
    }

    [KernelFact]
    public void A_confining_host_reports_the_severance_it_actually_enforces()
    {
        // The grade claims a setup was severed only when the runner says it enforces it: on this host bwrap does.
        BubblewrapSandbox.Available.ShouldNotBeNull("the kernel suite must execute confinement, never silently degrade");

        new LocalProcessRunner().EnforcedEgress(new SandboxSpec { Command = "npm", AllowNetwork = false }).ShouldBe(SandboxEgressMode.None, "bwrap gives a network-off spec a fresh, empty namespace");
        new LocalProcessRunner().EnforcedEgress(new SandboxSpec { Command = "npm", AllowNetwork = true }).ShouldBe(SandboxEgressMode.Full);
    }

    /// <summary>A loopback listener on a kernel-picked port, a GUID-named workspace, and a grader over the real runner; torn down on dispose.</summary>
    private sealed class GradeContext : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public GradeContext()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Directory.CreateDirectory(Workspace);
            Grader = new SupervisorAcceptanceGrader(null!, null!, new SandboxRunnerRegistry([new LocalProcessRunner()]), new BenchmarkGraderRegistry([new TestsPassGrader()]), null!, Artifacts, null!, NullLogger<SupervisorAcceptanceGrader>.Instance);
        }

        public int Port { get; }
        public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "cs-grade-posture-kernel-" + Guid.NewGuid().ToString("N"));
        public MemoryArtifacts Artifacts { get; } = new();
        public SupervisorAcceptanceGrader Grader { get; }

        public void Dispose()
        {
            _listener.Stop();
            try { Directory.Delete(Workspace, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The evidence store, in memory: the grader stores each grade's evidence through it, and the test reads it back.</summary>
    private sealed class MemoryArtifacts : IArtifactStore
    {
        public List<string> Texts { get; } = new();

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            Texts.Add(System.Text.Encoding.UTF8.GetString(bytes.Span));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactBytes?>(null);
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactMetadata?>(null);
    }

    private sealed class KernelTheoryAttribute : TheoryAttribute
    {
        public KernelTheoryAttribute()
        {
            if (BubblewrapSandbox.Available is null && !BubblewrapSandbox.IsRequired)
                Skip = "Requires real Linux bubblewrap; the privileged GitHub Actions lane is authoritative.";
        }
    }

    private sealed class KernelFactAttribute : FactAttribute
    {
        public KernelFactAttribute()
        {
            if (BubblewrapSandbox.Available is null && !BubblewrapSandbox.IsRequired)
                Skip = "Requires real Linux bubblewrap; the privileged GitHub Actions lane is authoritative.";
        }
    }
}
