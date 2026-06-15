using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The dynamic-workflows PROJECTION layer end-to-end, on top of the PR1 snapshot substrate. A
/// <see cref="TaskBuildContext"/> routed at a projection kind flows through the REAL
/// <see cref="ITaskRunSnapshotFactory"/> → the registry resolves a builder by the open kind string → the
/// built (always-valid) definition is frozen + dispatched by <c>IRunFromSnapshotStarter</c> → the REAL engine
/// walks it. No <c>workflow</c> / <c>workflow_version</c> row is created (it is a snapshot run, PR1's promise).
///
/// <para>Two tiers of proof:
///   (a) 🟢 HIGH — the <c>single-agent</c> projection's <c>agent.code</c> step ACTUALLY EXECUTES through the
///       real <see cref="AgentRunExecutor"/> → real <c>LocalProcessRunner</c> → a fake-CLI process → real
///       result fold → natural resume → run Success, proving the projected definition runs identically to an
///       authored agent.code node;
///   (b) the ZERO-CORE-EDIT genericity contract — a FAKE <see cref="IWorkflowDefinitionBuilder"/>
///       (<c>"fake-projection"</c>, emitting a trivial manual→terminal def) registered ONLY in a test DI scope
///       is resolved + built + run to Success by the SAME factory, with no production-core edit. Dispatch is
///       purely <c>registry.Resolve(openString)</c>, so the factory never names a concrete projection kind.</para>
///
/// <para><b>Fidelity (Rule 12):</b> tier (a) is HIGH — real factory, real starter (DefinitionValidator +
/// DefinitionHash + dispatcher CAS), real engine over real Postgres, real executor + LocalProcessRunner
/// SPAWNING A REAL OS PROCESS, the harness's real ParseEvent/BuildResult. Only the CLI's intelligence is faked
/// at the binary (<see cref="SubtaskAwareFakeCli"/>), the same honest boundary HeadlineFlowE2ETests uses.
/// POSIX-only for tier (a) — the fake CLI is a /bin/sh script (Rule 12.1); tier (b) needs no agent and runs
/// everywhere.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TaskProjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskProjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Single_agent_projection_runs_a_real_agent_through_the_snapshot_engine_to_success()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // The CLI's intelligence is faked at the binary; the executor/runner/harness driving it is all real.
        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend dispatches the REAL AgentRunExecutor + real runner + fake CLI

        var workflowCountBefore = await CountWorkflowsAsync(teamId);
        var versionCountBefore = await CountWorkflowVersionsAsync();

        // A single-agent task: a goal + a codex-cli, read-only, no-repo profile (analysis-only, no workspace
        // needed). The route names the single-agent projection kind — the ONLY thing dispatch keys off.
        var context = SingleAgentContext(teamId, goal: "Work on the auth refactor", harness: "codex-cli");

        // ── Pass 1: the factory projects + starts a snapshot run; the engine walks start → agent.code, which
        //    parks + dispatches its real executor job; the run suspends. ──
        var handle = await CreateAndRunAsync(context, teamId, userId);
        await RunEngineAsync(handle.RunId);

        // ── Drain the deferred chain: the real executor spawns the fake CLI through the real runner, completes
        //    for real, the completion notifier resumes, the node advances → terminal → Success. ──
        await jobClient.WaitForPendingAsync();

        handle.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == handle.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the projected single-agent definition must walk start → agent.code → terminal to Success through the real engine + executor + fake CLI; if not, inspect the failed WorkflowRunNode rows + the AgentRun.Error for this run");

        // It is a SNAPSHOT run — PR1's promise holds for a projected definition too.
        run.WorkflowId.ShouldBeNull("a projected task run is a snapshot run — not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull("a projected task run has no pinned version");
        run.DefinitionSnapshotJson.ShouldNotBeNull("the projected definition is frozen inline on the run");
        (await CountWorkflowsAsync(teamId)).ShouldBe(workflowCountBefore, "no workflow row is created for a projected snapshot run");
        (await CountWorkflowVersionsAsync()).ShouldBe(versionCountBefore, "no workflow_version row is created for a projected snapshot run");

        // EVIDENCE the agent REALLY ran: one real AgentRun for the projected agent.code node, Succeeded, with a
        // folded result whose summary is the fake-CLI transform of the projected goal — proving the profile→goal
        // mapping reached the real CLI.
        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == handle.RunId);
        agentRun.Status.ShouldBe(AgentRunStatus.Succeeded, "the projected agent.code executed to Succeeded via the real executor + runner");
        agentRun.NodeId.ShouldBe("agent", "the run links back to the projected agent.code node");

        var task = JsonSerializer.Deserialize<Messages.Agents.AgentTask>(agentRun.TaskJson, AgentJson.Options)!;
        task.Goal.ShouldBe("Work on the auth refactor", "the seed goal mapped onto the projected agent.code goal config");
        task.Harness.ShouldBe("codex-cli", "the profile harness mapped onto the projected agent.code harness config");

        var result = JsonSerializer.Deserialize<Messages.Agents.AgentRunResult>(agentRun.ResultJson!, AgentJson.Options)!;
        result.Summary.ShouldBe(SubtaskAwareFakeCli.ExpectedSummaryFor("Work on the auth refactor"),
            customMessage: "the real folded summary must be the fake-CLI transform of the projected goal — a mismatch means the goal never reached the real CLI");

        // The run's outputs are the agent result the terminal surfaced.
        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.GetProperty("summary").GetString().ShouldBe(result.Summary, "the terminal surfaced the agent's summary as the run output");
    }

    [Fact]
    public async Task A_fake_projection_kind_resolves_builds_and_runs_with_zero_core_edit()
    {
        // THE zero-core-edit proof. A brand-new projection strategy is registered ONLY here (a fake builder for
        // an open kind string the production core has never heard of). The SAME ITaskRunSnapshotFactory resolves
        // it through the registry, builds its definition, freezes + dispatches it, and the real engine runs it
        // to Success — without one line changed in the factory / registry / starter. A new strategy is purely
        // "register a builder", exactly like adding an IAgentHarness.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A child DI scope that ADDS the fake builder to the projection variant axis. Re-registering the
        // registry + factory in the scope rebuilds them over the AUGMENTED IEnumerable<IWorkflowDefinitionBuilder>
        // (the root SingleAgentDefinitionBuilder + this fake) — the production registration is untouched.
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new FakeProjectionBuilder()).As<IWorkflowDefinitionBuilder>();
            b.RegisterType<TaskProjectionRegistry>().As<ITaskProjectionRegistry>().InstancePerLifetimeScope();
            b.RegisterType<TaskRunSnapshotFactory>().As<ITaskRunSnapshotFactory>().InstancePerLifetimeScope();
        });

        // The registry sees BOTH the production single-agent builder AND the test-only fake — proving a new
        // strategy is additive, not a replacement.
        var registry = scope.Resolve<ITaskProjectionRegistry>();
        registry.Kinds.ShouldContain(TaskProjectionKinds.SingleAgent, "the production builder is still registered");
        registry.Kinds.ShouldContain(FakeProjectionBuilder.Kind, "the test-only fake builder joined the same variant axis");

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "anything", SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { ProjectionKind = FakeProjectionBuilder.Kind },
        };

        var handle = await scope.Resolve<ITaskRunSnapshotFactory>().CreateAndRunAsync(context, teamId, userId, CancellationToken.None);

        handle.ProjectionKind.ShouldBe(FakeProjectionBuilder.Kind);

        await RunEngineAsync(handle.RunId);

        var run = await LoadRunAsync(handle.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the factory resolved + built + ran a projection kind unknown to the production core — the dispatch is registry.Resolve(openString) with zero per-kind branching");
        run.WorkflowId.ShouldBeNull("the fake projection ran as a snapshot run too");
    }

    /// <summary>A test-only projection strategy proving zero-core-edit extensibility: emits a trivial valid manual→terminal definition for an open kind the production core never names.</summary>
    private sealed class FakeProjectionBuilder : IWorkflowDefinitionBuilder
    {
        public const string Kind = "fake-projection";

        public string ProjectionKind => Kind;

        public WorkflowDefinition Build(TaskBuildContext context) => new()
        {
            SchemaVersion = WorkflowDefinition.CurrentSchemaVersion,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "done", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "done" } },
        };
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>A single-agent route + a codex-cli, read-only, no-repo profile (analysis-only — no workspace clone needed for the fake CLI).</summary>
    private static TaskBuildContext SingleAgentContext(Guid teamId, string goal, string harness) => new()
    {
        Seed = new TaskLaunchSeed { Goal = goal, SurfaceKind = "test", TeamId = teamId },
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
        AgentProfile = new ResolvedAgentProfile { Harness = harness, RunnerKind = "local", AutonomyLevel = "Confined" },
    };

    private async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskRunSnapshotFactory>().CreateAndRunAsync(context, teamId, userId, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task<int> CountWorkflowsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId);
    }

    private async Task<int> CountWorkflowVersionsAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking().CountAsync();
    }
}
