using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High-fidelity (Rule 12): P19's Launch-mode benchmark instrument end to end — the REAL
/// <see cref="ITaskLaunchBenchmarkCellRunner"/> drives a REAL <c>ITaskLaunchService</c> (seed → route → project →
/// run) through a REAL <c>IWorkflowEngine</c> → REAL <c>IAgentRunExecutor</c> → REAL <c>LocalProcessRunner</c>
/// (spawning a real fake-CLI process) → REAL <see cref="TestsPassGrader"/> re-running the fixture's own check
/// script, over real Postgres. The ONLY fake is the CLI's intelligence (a no-op /bin/sh script, mirroring
/// <see cref="BenchmarkRunnerFlowTests"/>'s own honesty posture): it never edits the workspace, so the grade is
/// driven purely by the fixture's start-state — proving the pipeline connects, not agent quality. Because the
/// no-op CLI emits no model line, <see cref="BenchmarkResult.ObservedModel"/> comes back genuinely null here,
/// which is itself the "unknown stays unknown" proof (never fabricated from what was requested).
///
/// <para>POSIX-only (Rule 12.1): the fake CLI + the check are /bin/sh scripts the runner spawns.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TaskLaunchBenchmarkCellRunnerFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskLaunchBenchmarkCellRunnerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_solved_fixture_launched_through_the_real_TaskLaunch_entry_grades_pass_and_carries_the_route_census()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI + check are /bin/sh scripts the runner spawns

        using var cli = new FakeBenchmarkCli();
        using var workspace = Fixture.Stage(checkExitCode: 0);

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var result = await RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, workspace.Directory, teamId);

        result.Grade.Passed.ShouldBeTrue($"the fixture's own check.sh already exits 0 and the no-op CLI never touches it — grade detail: {result.Grade.Detail}");
        result.RunStatus.ShouldBe(Messages.Enums.AgentRunStatus.Succeeded);
        result.RouteEffortMode.ShouldBe(TaskEffortModes.Quick, "an explicit Quick arm routes to the quick effort — no classifier confirm card");
        result.RouteProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent, "quick effort projects single-agent");
        result.ObservedModel.ShouldBeNull("the no-op fake CLI emits no model line — unknown stays unknown, never fabricated from what was requested");
        result.AgentRunId.ShouldNotBeNull();

        await AssertRealLaunchedRunAsync(result.AgentRunId!.Value, teamId);
    }

    [Fact]
    public async Task A_failing_fixture_launched_through_the_real_TaskLaunch_entry_grades_fail_even_though_the_run_succeeded()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        using var workspace = Fixture.Stage(checkExitCode: 1);

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var result = await RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, workspace.Directory, teamId);

        result.RunStatus.ShouldBe(Messages.Enums.AgentRunStatus.Succeeded, "the run still completes — the no-op CLI finished");
        result.Grade.Passed.ShouldBeFalse("but the post-run check still fails → the honest oracle grades it UNSOLVED, never by the run status");
    }

    [Fact]
    public async Task BenchmarkRunner_itself_dispatches_a_TaskLaunch_arm_to_the_real_cell_runner_never_the_direct_AgentTask_path()
    {
        // Proves the DISPATCH line in BenchmarkRunner.RunAsync, not just the cell runner in isolation: resolving
        // the shared IBenchmarkRunner (the SAME instance the corpus loop injects) and driving it directly must
        // reach TaskLaunchService — never silently fall through to BuildAgentTask/IAgentRunService, which would
        // mint DirectAgentHarness evidence under a receipt that claims TaskLaunch.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        using var workspace = Fixture.Stage(checkExitCode: 0);

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var runner = scope.Resolve<IBenchmarkRunner>();
        var context = new BenchmarkExecutionContext { WorkspaceDirectory = workspace.Directory, TeamId = teamId, Selection = null };

        var result = await runner.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None);

        result.RouteEffortMode.ShouldBe(TaskEffortModes.Quick, "only the real Launch dispatch stamps a resolved route — the direct path leaves this null");
        result.RouteProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
        result.Grade.Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_solved_fixture_launched_at_Standard_effort_fans_the_plan_map_out_over_real_agent_runs_and_grades_pass()
    {
        // Coverage gap this closes: only TaskLaunchQuick (single-agent) had ANY integration coverage before this —
        // Standard's plan-map-synth fan-out is exercised here through the SAME drive loop (DriveOnePendingWaveAsync
        // claims it covers "a plan-map fan-out's parallel wave" via the shared AgentRun wait kind; this proves it).
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        using var workspace = Fixture.Stage(checkExitCode: 0);

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var result = await RunAsync(TestsPassTask(BenchmarkMode.TaskLaunchStandard), BenchmarkMode.TaskLaunchStandard, workspace.Directory, teamId);

        result.Grade.Passed.ShouldBeTrue($"the fixture's own check.sh already exits 0 and the no-op CLI never touches any branch's workspace — grade detail: {result.Grade.Detail}");
        result.RunStatus.ShouldBe(Messages.Enums.AgentRunStatus.Succeeded);
        result.RouteEffortMode.ShouldBe(TaskEffortModes.Standard, "an explicit Standard arm routes to the standard effort — no classifier confirm card");
        result.RouteProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "standard effort projects the plan-map-synth fan-out");
        result.AgentRunId.ShouldNotBeNull();

        // The team's seeded in-process model pool has 6 structured-capable fakes and no pin, so the null-pin
        // selector's total order (default desc, tier desc, THEN model-id ordinal asc) picks whichever sorts first:
        // "TestCoordinator-model" (DeterministicCoordinatedLlmClient), whose plan half emits exactly 2 subtasks
        // (Draft/Review) — 2 real branches.
        await AssertBranchesSucceededAsync(result.AgentRunId!.Value, teamId, expectedBranches: 2);
    }

    [Fact]
    public async Task A_solved_fixture_launched_at_Deep_effort_drives_a_real_supervisor_spawn_barrier_to_stop_and_grades_pass()
    {
        // Coverage gap this closes: Deep's supervisor spawn + K-wait barrier + self-advance had ZERO integration
        // coverage before this. ScriptedSupervisorDecider.PlanSpawnStop is the SAME deterministic decider
        // SupervisorSpawnFlowTests proves against a simulated agent completion; here the cell runner's OWN drive
        // loop executes both spawned agents through a REAL local sandboxed process (FakeBenchmarkCli).
        if (OperatingSystem.IsWindows()) return;

        SupervisorDecisionScript script;
        using (var scope = _fixture.BeginScope()) script = scope.Resolve<SupervisorDecisionScript>();

        script.PlanSpawnStop();   // turn0 plan(2) → turn1 spawn(both) → turn2 stop
        try
        {
            using var cli = new FakeBenchmarkCli();
            using var workspace = Fixture.Stage(checkExitCode: 0);

            var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            var result = await RunAsync(TestsPassTask(BenchmarkMode.TaskLaunchDeep), BenchmarkMode.TaskLaunchDeep, workspace.Directory, teamId);

            result.Grade.Passed.ShouldBeTrue($"the fixture's own check.sh already exits 0 and the no-op CLI never touches either spawned agent's workspace — grade detail: {result.Grade.Detail}");
            result.RouteEffortMode.ShouldBe(TaskEffortModes.Deep, "an explicit Deep arm routes to the deep effort — no classifier confirm card");
            result.RouteProjectionKind.ShouldBe(TaskProjectionKinds.Supervisor, "deep effort projects the supervisor lane");
            result.AgentRunId.ShouldNotBeNull();

            var workflowRunId = await AssertBranchesSucceededAsync(result.AgentRunId!.Value, teamId, expectedBranches: 2);

            (await LedgerKindsAsync(workflowRunId, teamId)).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Stop },
                "turn 0's self-advance and turn 1's spawn(both) K-wait barrier both resolved through the cell runner's OWN drive loop — a real turn loop ran, not a stub");
        }
        finally
        {
            script.PlanThenStop();   // restore the default for sibling tests sharing this Postgres-collection fixture
        }
    }

    [Fact]
    public async Task A_completed_Deep_cell_leaves_the_teams_provider_instance_list_and_the_owners_open_sessions_unchanged()
    {
        // The data leak this closes: every cell created a real ProviderInstance (a junk "codespace-qualification"
        // GitHub connection permanently at the top of the team's real Integrations list) and launched AS the team's
        // real Owner, opening a real WorkSession — a Conversation too, for Deep — in that person's own history, none
        // of it ever retired. Count-before == count-after proves the retirement actually removes the cell's
        // footprint from what the team/owner would see, not merely that SOME cleanup step ran without throwing.
        if (OperatingSystem.IsWindows()) return;

        SupervisorDecisionScript script;
        using (var scope = _fixture.BeginScope()) script = scope.Resolve<SupervisorDecisionScript>();

        script.PlanSpawnStop();   // Deep (not Quick) so the Conversation-archival half of the fix is exercised too
        try
        {
            using var cli = new FakeBenchmarkCli();
            using var workspace = Fixture.Stage(checkExitCode: 0);

            var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            var before = await LeakSurfaceAsync(teamId, ownerId);

            var result = await RunAsync(TestsPassTask(BenchmarkMode.TaskLaunchDeep), BenchmarkMode.TaskLaunchDeep, workspace.Directory, teamId);
            result.Grade.Passed.ShouldBeTrue();

            var after = await LeakSurfaceAsync(teamId, ownerId);

            after.ProviderInstances.ShouldBe(before.ProviderInstances, "the ad-hoc qualification GitHub connection must be retired — never a permanent addition to the team's real Integrations list");
            after.OpenSessions.ShouldBe(before.OpenSessions, "the launch's WorkSession must be archived — never a live thread left in the borrowed Owner's own session history");
            after.OpenConversations.ShouldBe(before.OpenConversations, "the Deep launch's chat surface (Conversation) must be archived alongside its session — never a live channel left behind");
        }
        finally
        {
            script.PlanThenStop();
        }
    }

    [Fact]
    public async Task A_Deep_run_parked_on_an_unadvanceable_ask_human_wait_fails_fast_instead_of_burning_the_drive_budget()
    {
        // A Deep supervisor can park on ask_human (WorkflowWaitKinds.Action) — a wait kind DriveOnePendingWaveAsync
        // has no seam for. Before this fix the cell burned its whole drive budget polling before TimeoutException;
        // now FailFastOnUnadvanceableWaitAsync detects it within one poll cycle and names the wait kind.
        if (OperatingSystem.IsWindows()) return;

        SupervisorDecisionScript script;
        using (var scope = _fixture.BeginScope()) script = scope.Resolve<SupervisorDecisionScript>();

        script.AskHumanStop();   // turn0 ask_human(AskQuestion) — never resolved, so the run parks there forever
        try
        {
            using var cli = new FakeBenchmarkCli();
            using var workspace = Fixture.Stage(checkExitCode: 0);

            var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
            // A short timeout so a REGRESSION (the fail-fast not firing) fails this test in seconds, not minutes.
            var task = TestsPassTask(BenchmarkMode.TaskLaunchDeep) with { TimeoutSeconds = 5 };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(task, BenchmarkMode.TaskLaunchDeep, workspace.Directory, teamId));

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the fail-fast path must detect the parked ask_human wait almost immediately, never burn the drive budget out to the deadline");
            ex.Message.ShouldContain("Action", customMessage: $"the exception must name the wait kind the drive loop cannot advance — got: {ex.Message}");
        }
        finally
        {
            script.PlanThenStop();
        }
    }

    // ─── plumbing ────────────────────────────────────────────────────────────────

    private async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<ITaskLaunchBenchmarkCellRunner>();

        var context = new BenchmarkExecutionContext { WorkspaceDirectory = workspaceDirectory, TeamId = teamId, Selection = null };

        return await sut.RunAsync(task, mode, context, CancellationToken.None);
    }

    /// <summary>The cell's AgentRun is a real node of a real snapshot WorkflowRun — never a repository-less standalone AgentRun the direct instrument creates — with the route decision stamped on it exactly like every other Launch.</summary>
    private async Task AssertRealLaunchedRunAsync(Guid agentRunId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId);
        run.WorkflowRunId.ShouldNotBeNull("a TaskLaunch cell's agent run is a real node of a real WorkflowRun");
        run.TeamId.ShouldBe(teamId);

        var workflowRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == run.WorkflowRunId);
        workflowRun.RoutePlanJson.ShouldNotBeNullOrEmpty("the route decision is stamped on the run exactly like every other Launch");
        workflowRun.WorkflowId.ShouldBeNull("a launched task run is a snapshot run — not a child of any workflow");
    }

    /// <summary>Every branch a plan-map fan-out or a supervisor spawn staged is a real AgentRun sharing the cell's WorkflowRunId — the SAME AgentRun wait kind the drive loop polls whichever arm produced it. Returns the shared WorkflowRunId so a caller can assert further (e.g. the supervisor's own decision ledger).</summary>
    private async Task<Guid> AssertBranchesSucceededAsync(Guid agentRunId, Guid teamId, int expectedBranches)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId);
        run.WorkflowRunId.ShouldNotBeNull("a TaskLaunch cell's agent run is a real node of a real WorkflowRun");

        var branches = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == run.WorkflowRunId).ToListAsync();
        branches.Count.ShouldBe(expectedBranches, "every branch the fan-out/spawn staged drove through the SAME AgentRun wait kind the drive loop polls");
        branches.ShouldAllBe(b => b.TeamId == teamId && b.Status == Messages.Enums.AgentRunStatus.Succeeded);

        return run.WorkflowRunId!.Value;
    }

    private async Task<IReadOnlyList<string>> LedgerKindsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).Select(d => d.DecisionKind).ToListAsync();
    }

    /// <summary>The exact counts a real operator would see: <c>ProviderInstanceService.ListAsync</c>'s own (team, not-deleted) predicate, and the borrowed Owner's own OPEN (not-yet-archived) sessions/conversations — the surface a leaked ad-hoc qualification resource would inflate.</summary>
    private async Task<(int ProviderInstances, int OpenSessions, int OpenConversations)> LeakSurfaceAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var providerInstances = await db.ProviderInstance.AsNoTracking().CountAsync(p => p.TeamId == teamId && p.DeletedDate == null);
        var openSessions = await db.WorkSession.AsNoTracking().CountAsync(s => s.TeamId == teamId && s.CreatedBy == ownerId && s.Status == Messages.Enums.WorkSessionStatus.Open);
        var openConversations = await db.Conversation.AsNoTracking().CountAsync(c => c.TeamId == teamId && !c.Archived);

        return (providerInstances, openSessions, openConversations);
    }

    private static BenchmarkTask TestsPassTask(BenchmarkMode mode = BenchmarkMode.TaskLaunchQuick) => new()
    {
        Id = $"p19-{ArmSlug(mode)}-fixture",
        Description = "a pre-staged fixture this test stages directly (not via IBenchmarkFixtureStager)",
        FixtureRef = "p19-inline-fixture",
        Goal = "Make the check script pass.",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = "codex-cli",
        Modes = new[] { mode },
    };

    private static string ArmSlug(BenchmarkMode mode) => mode.ToString().Replace("TaskLaunch", "").ToLowerInvariant();

    /// <summary>Mirrors <c>BenchmarkRunnerFlowTests.BenchmarkFixture</c> — a minimal staged workspace whose only content is a check script, so the objective grade is driven purely by its exit code.</summary>
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; }

        private Fixture(int checkExitCode)
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-launch-cell-fx-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);

            var check = System.IO.Path.Combine(Directory, "check.sh");
            File.WriteAllText(check, $"#!/bin/sh\nexit {checkExitCode}\n");
            File.SetUnixFileMode(check, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        public static Fixture Stage(int checkExitCode) => new(checkExitCode);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
