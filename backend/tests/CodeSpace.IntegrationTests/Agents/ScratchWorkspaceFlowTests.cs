using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (real Postgres + real executor + real OS process + real filesystem): DC-4 slice 2 — the
/// REPO-LESS deliverable lane end to end. Before this, a repo-less run had no workspace at all: the agent's
/// report died with the process and any acceptance contract failed closed on "no-branch-or-repo". Now a
/// contract-bearing repo-less run gets a SCRATCH world: the harness writes into it, the declared-artifact
/// capture mints the typed manifest row from it, the oracle grades DIRECTLY against it, and the run lands
/// Succeeded — while a repo-less run without a contract keeps today's null-workspace path byte-identically.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ScratchWorkspaceFlowTests
{
    private readonly PostgresFixture _fixture;

    public ScratchWorkspaceFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_repo_less_declared_deliverable_survives_and_grades_to_success()
    {
        if (OperatingSystem.IsWindows()) return;   // the scripted harness is a /bin/sh invocation

        var teamId = await SeedTeamAsync();
        var runId = await CreateScratchRunAsync(teamId, declaredPath: "report.md");

        await ExecuteAsync(runId, new ScriptedHarness("printf '# findings\\n' > report.md"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the repo-less contract now has a world to grade against — error: {run.ResultJson}");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.AcceptancePassed.ShouldBe(true, "the ArtifactPresent oracle graded the scratch directory itself");
        result.CapturedArtifactCount.ShouldBe(1, "the declared report was captured as a typed artifact before grading");

        var manifest = (await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        manifest.LogicalPath.ShouldBe("report.md");
        manifest.Kind.ShouldBe(ArtifactManifestKind.Document);

        var bytes = await scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactStore>().GetBytesAsync(teamId, manifest.ContentArtifactId, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(bytes!.Bytes).ShouldBe("# findings\n", "the deliverable's exact bytes outlive the scratch directory");

        Directory.Exists(Path.Combine(Path.GetTempPath(), $"codespace-scratch-{runId:N}")).ShouldBeFalse("the scratch world is torn down with the run — the durable residue is the typed artifact");
    }

    [Fact]
    public async Task A_repo_less_run_whose_declared_deliverable_is_missing_fails_the_oracle()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScratchRunAsync(teamId, declaredPath: "report.md");

        await ExecuteAsync(runId, new ScriptedHarness("printf 'wrote the wrong file' > other.txt"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Failed, "the oracle still has teeth in the scratch world — a missing declared deliverable fails closed");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.AcceptancePassed.ShouldBe(false);
    }

    // ─── C2: the UNDECLARED walk, and the contract-less run that used to get no world at all ─────────────

    /// <summary>
    /// The loss C2 closes: a run only knows what it DECLARED, and the deliverable worth keeping was routinely the
    /// one nobody declared. Here the agent writes a report it never promised plus a stray binary — the walk keeps
    /// the report (durably, in the CAS, after the scratch world is deleted) and refuses the binary, and BOTH numbers
    /// land on the result so an over-limit capture could never be silent.
    /// </summary>
    [Fact]
    public async Task An_undeclared_report_is_captured_by_the_bounded_walk_and_a_binary_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScratchRunAsync(teamId, declaredPath: "report.md");

        await ExecuteAsync(runId, new ScriptedHarness("printf '# declared\\n' > report.md; printf '# extra\\n' > notes.md; printf 'BINARY' > tool.bin"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

        result.CapturedArtifactCount.ShouldBe(1, "the declared path keeps today's semantics exactly");
        result.UndeclaredArtifactCount.ShouldBe(1, "the report nobody declared is no longer lost with the scratch directory");
        result.UncapturedScratchFileCount.ShouldBe(1, "the binary was SEEN and refused — the shortfall half of the pair");

        var manifests = await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None);
        manifests.Select(m => m.LogicalPath).OrderBy(p => p, StringComparer.Ordinal).ShouldBe(new[] { "notes.md", "report.md" });

        var undeclared = manifests.Single(m => m.LogicalPath == "notes.md");
        var bytes = await scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactStore>().GetBytesAsync(teamId, undeclared.ContentArtifactId, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(bytes!.Bytes).ShouldBe("# extra\n", "the undeclared deliverable's exact bytes outlive the scratch directory too");
    }

    /// <summary>
    /// A repo-less run with NO contract used to get a null workspace, so the harness ran with a null working
    /// directory and anything it wrote was unreachable and unaccountable. It now gets a world, and the walk keeps
    /// what it finds — with no acceptance to grade, so the run's verdict is untouched.
    /// </summary>
    [Fact]
    public async Task A_repo_less_run_with_no_contract_still_gets_a_world_and_its_files_are_captured()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScratchRunAsync(teamId, declaredPath: null);

        await ExecuteAsync(runId, new ScriptedHarness("printf '# findings\\n' > findings.md"));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"no contract ⇒ nothing to grade — error: {run.ResultJson}");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.AcceptancePassed.ShouldBeNull("there is no acceptance contract to reach a verdict on");
        result.UndeclaredArtifactCount.ShouldBe(1, "before C2 this file died with the process — there was no working directory to write it into");

        var manifest = (await scope.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        manifest.LogicalPath.ShouldBe("findings.md");
    }

    // ─── Seeding / plumbing (the executor recipe, minus any repository) ───

    private async Task<Guid> SeedTeamAsync()
    {
        var (teamId, _) = await Workflows.Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return teamId;
    }

    private async Task<Guid> CreateScratchRunAsync(Guid teamId, string? declaredPath)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask
            {
                Goal = "write the findings report", Harness = "scripted", Model = "test-model",
                Acceptance = declaredPath is null ? null : new SupervisorAcceptanceSpec { Command = new[] { declaredPath }, Kind = BenchmarkGradingKind.ArtifactPresent },
            },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<IPublishManifestStore>(), scope.Resolve<IArtifactManifestStore>(), scope.Resolve<Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }
}
