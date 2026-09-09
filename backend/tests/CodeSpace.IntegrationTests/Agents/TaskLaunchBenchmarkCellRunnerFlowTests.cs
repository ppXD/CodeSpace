using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Dtos.Workflows;
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
        var completion = new RecordingCompletionSink();

        var result = await RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, workspace.Directory, teamId, completion);

        result.Grade.Passed.ShouldBeTrue($"the fixture's own check.sh already exits 0 and the no-op CLI never touches it — grade detail: {result.Grade.Detail}");
        result.RunStatus.ShouldBe(Messages.Enums.AgentRunStatus.Succeeded);
        result.RouteEffortMode.ShouldBe(TaskEffortModes.Quick, "an explicit Quick arm routes to the quick effort — no classifier confirm card");
        result.RouteProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent, "quick effort projects single-agent");
        result.ObservedModel.ShouldBeNull("the no-op fake CLI emits no model line — unknown stays unknown, never fabricated from what was requested");
        // CompletionEnforcementMode.Shadow.ToString() — the STAMPED enum, not WorkflowDefinition.CompletionModeShadow's
        // lowercase wire string (that's the DEFINITION's opt-in vocabulary; this is what actually landed on the run).
        result.CompletionMode.ShouldBe(nameof(Messages.Contracts.CompletionEnforcementMode.Shadow), "read off the ACTUAL persisted WorkflowRun, not assumed — proves the Shadow override this cell always requests really did take");
        result.AgentRunId.ShouldNotBeNull();
        completion.Result.ShouldBe(result, "the real TaskLaunch cell must seal the exact terminal result before returning it to the corpus loop");
        completion.CancellationToken.CanBeCanceled.ShouldBeFalse("terminal settlement must survive cancellation of the outer long-running campaign");

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
    public async Task A_process_lost_after_Launch_commit_adopts_the_same_WorkflowRun_and_never_launches_a_second_run()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        using var firstWorkspace = Fixture.Stage(checkExitCode: 0);
        using var recoveryWorkspace = Fixture.Stage(checkExitCode: 0);
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var durability = new InterruptingDurableCellSink { BlockLaunchBinding = true };

        Exception firstFailure;
        using (var firstScope = _fixture.BeginScope())
        {
            var first = firstScope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            var context = new BenchmarkExecutionContext { WorkspaceDirectory = firstWorkspace.Directory, TeamId = teamId, Selection = null, CheckpointSink = durability, Completion = durability };
            firstFailure = await Should.ThrowAsync<InvalidOperationException>(() => first.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None));
        }

        durability.Checkpoints.ContainsKey(TaskLaunchBenchmarkCellRunner.FixtureCheckpointKind).ShouldBeTrue();
        durability.Checkpoints.ContainsKey(TaskLaunchBenchmarkCellRunner.LaunchStartedCheckpointKind).ShouldBeTrue(firstFailure.ToString());
        durability.Checkpoints.ContainsKey(TaskLaunchBenchmarkCellRunner.LaunchCheckpointKind).ShouldBeFalse("the simulated process vanished before acknowledging the committed run");
        durability.BlockLaunchBinding = false;
        firstWorkspace.Dispose();
        Directory.Exists(firstWorkspace.Directory).ShouldBeFalse("a real corpus process reclaims its temporary fixture before a later process recovers");

        BenchmarkResult result;
        using (var recoveryScope = _fixture.BeginScope())
        {
            var recovery = recoveryScope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            var context = new BenchmarkExecutionContext { WorkspaceDirectory = recoveryWorkspace.Directory, TeamId = teamId, Selection = null, Checkpoints = durability.Checkpoints, CheckpointSink = durability, Completion = durability };
            result = await recovery.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None);
        }

        result.Grade.Passed.ShouldBeTrue();
        durability.Result.ShouldBe(result);
        durability.Checkpoints.ContainsKey(TaskLaunchBenchmarkCellRunner.LaunchCheckpointKind).ShouldBeTrue("recovery must bind the run it found before driving it");
        using var readScope = _fixture.BeginScope();
        var runs = await readScope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(value => value.TeamId == teamId && value.Purpose == Messages.Constants.WorkflowRunPurposes.Qualification).ToListAsync();
        runs.Count.ShouldBe(1, "recovery must adopt the committed launch rather than create a second WorkflowRun");
        result.AgentRunId.ShouldNotBeNull();
        var agentRun = await readScope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(value => value.Id == result.AgentRunId);
        agentRun.WorkflowRunId.ShouldBe(runs.Single().Id);
    }

    [Fact]
    public async Task A_durable_launch_intent_without_an_attributable_run_parks_instead_of_replaying_the_launch()
    {
        if (OperatingSystem.IsWindows()) return;

        using var firstWorkspace = Fixture.Stage(checkExitCode: 0);
        using var recoveryWorkspace = Fixture.Stage(checkExitCode: 0);
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var durability = new InterruptingDurableCellSink { BlockAfterLaunchStarted = true };

        using (var firstScope = _fixture.BeginScope())
        {
            var first = firstScope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            var context = new BenchmarkExecutionContext { WorkspaceDirectory = firstWorkspace.Directory, TeamId = teamId, Selection = null, CheckpointSink = durability, Completion = durability };
            await Should.ThrowAsync<InvalidOperationException>(() => first.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None));
        }

        durability.Checkpoints.ContainsKey(TaskLaunchBenchmarkCellRunner.LaunchStartedCheckpointKind).ShouldBeTrue("intent must commit before entering the irreversible launch boundary");
        firstWorkspace.Dispose();
        durability.BlockAfterLaunchStarted = false;

        using (var recoveryScope = _fixture.BeginScope())
        {
            var recovery = recoveryScope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            var context = new BenchmarkExecutionContext { WorkspaceDirectory = recoveryWorkspace.Directory, TeamId = teamId, Selection = null, Checkpoints = durability.Checkpoints, CheckpointSink = durability, Completion = durability };
            var failure = await Should.ThrowAsync<DurableBenchmarkObservationException>(() => recovery.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None));
            failure.Message.ShouldContain("execution-indeterminate");
        }

        using var readScope = _fixture.BeginScope();
        (await readScope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(value => value.TeamId == teamId)).ShouldBe(0, "recovery cannot know whether the provider charged before local commit, so it must never replay automatically");
    }

    [Fact]
    public async Task A_paired_arm_cost_ceiling_reaches_the_real_TaskLaunch_request()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = Fixture.Stage(checkExitCode: 0);
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var launch = new CapturingFailTaskLaunchService();
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(launch).As<ITaskLaunchService>());
        var sut = scope.Resolve<ITaskLaunchBenchmarkCellRunner>();
        var context = new BenchmarkExecutionContext
        {
            WorkspaceDirectory = workspace.Directory,
            TeamId = teamId,
            Selection = new BenchmarkAgentSelection { MaxCostUsd = 2.75m },
        };

        await Should.ThrowAsync<InvalidOperationException>(() => sut.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None));

        launch.Request.ShouldNotBeNull("the production TaskLaunch entry must receive the paired arm");
        launch.Request.CapsOverride.ShouldNotBeNull().MaxCostUsd.ShouldBe(2.75m, "the paired cap must constrain the physical launch, not only the later statistics");
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

        // Pinned explicitly, not by alphabetical luck: the real Launch/route/project pipeline builds the plan-map
        // definition itself, so — unlike an authored-workflow test — nothing here can retarget a node's `provider`
        // config directly. Seeding ONLY DeterministicCoordinatedLlmClient's row (never the other 5 test fakes
        // SeedTeamAsync's default pool would add) is the one lever available: the null-pin selector then has
        // exactly one structured-eligible candidate, so it resolves to it BY CONSTRUCTION — not because
        // "TestCoordinator-model" happens to sort first among six. Its own doc explicitly covers both roles this
        // cell needs (the plan-emitting structured call AND the plain-text synthesizer call).
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, WorkflowsTestSeed.PoolModelIdFor(DeterministicCoordinatedLlmClient.ProviderTag), DeterministicCoordinatedLlmClient.ProviderTag);

        var result = await RunAsync(TestsPassTask(BenchmarkMode.TaskLaunchStandard), BenchmarkMode.TaskLaunchStandard, workspace.Directory, teamId);

        result.Grade.Passed.ShouldBeTrue($"the fixture's own check.sh already exits 0 and the no-op CLI never touches any branch's workspace — grade detail: {result.Grade.Detail}");
        result.RunStatus.ShouldBe(Messages.Enums.AgentRunStatus.Succeeded);
        result.RouteEffortMode.ShouldBe(TaskEffortModes.Standard, "an explicit Standard arm routes to the standard effort — no classifier confirm card");
        result.RouteProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "standard effort projects the plan-map-synth fan-out");
        result.AgentRunId.ShouldNotBeNull();

        // DeterministicCoordinatedLlmClient's plan half emits exactly 2 subtasks (Draft/Review) — 2 real branches.
        await AssertBranchesSucceededAsync(result.AgentRunId!.Value, teamId, expectedBranches: 2);
    }

    [Fact]
    public async Task ReconstructWorkspaceAsync_applies_every_attempts_own_real_patch_not_just_the_last()
    {
        // Every fan-out flow test's CLI is the pure no-op script — it never touches the workspace, so
        // "every attempt's captured diff, applied in dispatch order" had never actually been proven against TWO
        // branches carrying genuinely DIFFERENT real diffs. Driving that through the full launch/route/project
        // pipeline hits the production supervisor's OWN branch-integration step (git.integrate_run) — a real,
        // separate concern this test has no business depending on — so this drives ReconstructWorkspaceAsync
        // directly (InternalsVisibleTo) with two REAL git-diff-produced patches (captured off actual clones, never
        // hand-typed) and asserts both land on the fixture.
        if (OperatingSystem.IsWindows()) return;

        using var workspace = Fixture.Stage(checkExitCode: 0);
        await RunGitAsync(workspace.Directory, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(workspace.Directory, "check.sh"), "#!/bin/sh\nexit 0\n");
        await RunGitAsync(workspace.Directory, "-c", "user.email=t@t.local", "-c", "user.name=t", "add", "-A");
        await RunGitAsync(workspace.Directory, "-c", "user.email=t@t.local", "-c", "user.name=t", "commit", "-q", "-m", "fixture init");

        var patchA = await CaptureRealPatchAsync(workspace.Directory, "branch-a.txt", "from branch A\n");
        var patchB = await CaptureRealPatchAsync(workspace.Directory, "branch-b.txt", "from branch B\n");

        using var scope = _fixture.BeginScope();
        var sut = (TaskLaunchBenchmarkCellRunner)scope.Resolve<ITaskLaunchBenchmarkCellRunner>();

        await sut.ReconstructWorkspaceAsync(workspace.Directory, new[] { FakeAttempt(patchA), FakeAttempt(patchB) }, CancellationToken.None);

        File.Exists(Path.Combine(workspace.Directory, "branch-a.txt")).ShouldBeTrue("the FIRST attempt's own real patch must land — not overwritten or dropped by the second");
        File.Exists(Path.Combine(workspace.Directory, "branch-b.txt")).ShouldBeTrue("the SECOND (graded/last) attempt's own real patch must ALSO land — reconstruction must never apply only the graded attempt's diff and skip the other");
        (await File.ReadAllTextAsync(Path.Combine(workspace.Directory, "branch-a.txt"))).ShouldBe("from branch A\n");
        (await File.ReadAllTextAsync(Path.Combine(workspace.Directory, "branch-b.txt"))).ShouldBe("from branch B\n");
    }

    /// <summary>A REAL unified diff for a new file, captured off an actual git clone + <c>git diff --cached</c> — never hand-typed — so it applies onto <paramref name="repoDirectory"/> (its own clone parent) exactly like a real captured <c>AgentRunResult.Patch</c> would.</summary>
    private static async Task<string> CaptureRealPatchAsync(string repoDirectory, string fileName, string content)
    {
        var clonePath = Path.Combine(Path.GetTempPath(), "cs-launch-cell-patch-src-" + Guid.NewGuid().ToString("N"));

        try
        {
            await RunGitAsync(Path.GetTempPath(), "clone", "-q", repoDirectory, clonePath);
            await File.WriteAllTextAsync(Path.Combine(clonePath, fileName), content);
            await RunGitAsync(clonePath, "add", "-A");

            return await RunGitCaptureAsync(clonePath, "diff", "--cached");
        }
        finally
        {
            try { Directory.Delete(clonePath, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static AgentRun FakeAttempt(string patch) => new()
    {
        Id = Guid.NewGuid(),
        Status = Messages.Enums.AgentRunStatus.Succeeded,
        ResultJson = System.Text.Json.JsonSerializer.Serialize(new AgentRunResult { Status = Messages.Enums.AgentRunStatus.Succeeded, ExitReason = "completed", Patch = patch }, AgentJson.Options),
    };

    private static Task RunGitAsync(string workingDirectory, params string[] args) => RunGitCaptureAsync(workingDirectory, args);

    private static async Task<string> RunGitCaptureAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };

        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} (cwd {workingDirectory}) failed: exit {process.ExitCode}: {stderr}");

        return stdout;
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
    public async Task A_completed_Deep_cell_leaves_the_teams_provider_instances_runs_index_and_owners_conversations_unchanged()
    {
        // The data leak this closes: every cell created a real ProviderInstance (a junk "codespace-qualification"
        // GitHub connection permanently at the top of the team's real Integrations list), stamped a WorkflowRun the
        // team's own Runs index would list right alongside genuine launches, and launched AS the team's real Owner,
        // opening a real WorkSession — a Conversation too, for Deep — in that person's own history, none of it ever
        // retired. Before/after over the REAL production read surfaces (IWorkflowService.ListTeamRunsAsync,
        // ISessionReadService.ListAsync, IConversationService.ListForUserAsync) proves the retirement actually
        // removes the cell's footprint from what the team/owner would SEE, not merely that some cleanup step ran
        // without throwing against an ad-hoc predicate a real caller never uses.
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
            after.RunsIndexCount.ShouldBe(before.RunsIndexCount, "the qualification WorkflowRun's Purpose column must exclude it from the team's own Runs index — never a phantom entry there");
            after.OwnersConversationsListed.ShouldBe(before.OwnersConversationsListed, "the Deep launch's chat surface (Conversation) must be soft-deleted alongside archival — never a live channel left in the borrowed Owner's OWN conversation list");

            // KNOWN RESIDUAL (see this PR's Limitations): WorkSession has no soft-delete column, so
            // ISessionReadService.ListAsync (no status filter at all) still returns the archived qualification
            // session as a real row — archival only stops it from reading as an OPEN thread, it does not remove
            // the row from the team-wide list. Asserting the exact +1 (never +0, and never more) means a
            // regression that leaks a SECOND row, or a future soft-delete column that finally closes this gap
            // without updating this assertion, both fail this test loudly.
            after.AllTeamSessionsListed.ShouldBe(before.AllTeamSessionsListed + 1, "exactly one archived-but-still-listed qualification session is the disclosed residual — not zero (no fix shipped) and not more than one (no additional leak)");
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

    [Fact]
    public async Task RecoverOrphanedLaunchAsync_retires_the_newest_qualification_run_and_session_created_since_the_attempt_started()
    {
        // The failure shape this closes: ITaskLaunchService.LaunchAsync commits the WorkSession + WorkflowRun in a
        // SINGLE SaveChangesAsync (RunFromSnapshotStarter.StageAsync) with NO ambient transaction on this runner's
        // direct (non-Mediator) call path — so a step AFTER that commit throwing (route/purpose stamping, the
        // queued-run ledger write, or the dispatcher IPostCommitActions.RunAfterCommitAsync runs INLINE with no
        // open transaction) leaves both rows durably committed with no LaunchTaskResult ever returned to retire
        // them. There is no existing seam to inject that fault at its real call site, so this seeds the exact
        // committed end-state directly — Purpose still null, as it would be had the purpose stamp itself been the
        // step that failed — and drives the recovery method by its InternalsVisibleTo seam.
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var attemptStartedAt = DateTimeOffset.UtcNow;
        var repositoryId = Guid.NewGuid();

        var (runId, sessionId) = await SeedOrphanedQualificationLaunchAsync(teamId, ownerId, repositoryId, attemptStartedAt.AddSeconds(1));

        using (var scope = _fixture.BeginScope())
        {
            var sut = (TaskLaunchBenchmarkCellRunner)scope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            await sut.RecoverOrphanedLaunchAsync(teamId, ownerId, repositoryId, attemptStartedAt, CancellationToken.None);
        }

        using var assertScope = _fixture.BeginScope();
        var db = assertScope.Resolve<CodeSpaceDbContext>();

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        session.Status.ShouldBe(Messages.Enums.WorkSessionStatus.Archived, "the orphaned session must be archived exactly like a normally-retired cell — never left Open in the borrowed Owner's history");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Purpose.ShouldBe(Messages.Constants.WorkflowRunPurposes.Qualification, "the recovery must re-stamp Purpose when it never landed — otherwise the orphan would also leak into the team's Runs index");
    }

    [Fact]
    public async Task RecoverOrphanedLaunchAsync_ignores_a_run_created_before_the_attempt_started()
    {
        // The lower bound matters: a genuine run this same borrowed Owner launched moments earlier (in another
        // cell, or as real prior activity) must never be mistaken for THIS attempt's orphan and get its real
        // session archived.
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var attemptStartedAt = DateTimeOffset.UtcNow;
        var repositoryId = Guid.NewGuid();

        var (_, sessionId) = await SeedOrphanedQualificationLaunchAsync(teamId, ownerId, repositoryId, attemptStartedAt.AddSeconds(-30));

        using (var scope = _fixture.BeginScope())
        {
            var sut = (TaskLaunchBenchmarkCellRunner)scope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            await sut.RecoverOrphanedLaunchAsync(teamId, ownerId, repositoryId, attemptStartedAt, CancellationToken.None);
        }

        using var assertScope = _fixture.BeginScope();
        var db = assertScope.Resolve<CodeSpaceDbContext>();

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        session.Status.ShouldBe(Messages.Enums.WorkSessionStatus.Open, "a run created BEFORE this attempt started is never this attempt's own orphan — its session must be left untouched");
    }

    [Fact]
    public async Task RecoverOrphanedLaunchAsync_ignores_a_newer_genuine_run_by_the_same_actor_in_a_different_repository()
    {
        // BLOCKING fix this pins: the borrowed Owner is a REAL team member, so nothing stops them launching a
        // genuine task in this same team while a cell is mid-flight. Without the repository scope, "newest wins"
        // would recover the WRONG row here — the genuine run is created AFTER the orphan, so it (not the orphan)
        // would sort first under an ActorId+CreatedDate-only match. Only the row scoped to THIS cell's own fixture
        // repository may be recovered, regardless of which row is newer.
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var attemptStartedAt = DateTimeOffset.UtcNow;
        var fixtureRepositoryId = Guid.NewGuid();
        var genuineRepositoryId = Guid.NewGuid();

        var (orphanRunId, orphanSessionId) = await SeedOrphanedQualificationLaunchAsync(teamId, ownerId, fixtureRepositoryId, attemptStartedAt.AddSeconds(1));
        var (genuineRunId, genuineSessionId) = await SeedOrphanedQualificationLaunchAsync(teamId, ownerId, genuineRepositoryId, attemptStartedAt.AddSeconds(2));

        using (var scope = _fixture.BeginScope())
        {
            var sut = (TaskLaunchBenchmarkCellRunner)scope.Resolve<ITaskLaunchBenchmarkCellRunner>();
            await sut.RecoverOrphanedLaunchAsync(teamId, ownerId, fixtureRepositoryId, attemptStartedAt, CancellationToken.None);
        }

        using var assertScope = _fixture.BeginScope();
        var db = assertScope.Resolve<CodeSpaceDbContext>();

        var orphanSession = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == orphanSessionId);
        orphanSession.Status.ShouldBe(Messages.Enums.WorkSessionStatus.Archived, "the orphan scoped to THIS cell's own fixture repository must still be recovered even though it is the OLDER of the two");

        var orphanRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == orphanRunId);
        orphanRun.Purpose.ShouldBe(Messages.Constants.WorkflowRunPurposes.Qualification);

        var genuineSession = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == genuineSessionId);
        genuineSession.Status.ShouldBe(Messages.Enums.WorkSessionStatus.Open, "a genuine run in a DIFFERENT repository — even though newer — must never be mistaken for this cell's own orphan and have its real session archived");

        var genuineRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == genuineRunId);
        genuineRun.Purpose.ShouldBeNull("the genuine run's Purpose must never be stamped — that would hide a real operator launch from the team's own Runs index");
    }

    [Fact]
    public async Task RunAsync_recovers_the_orphaned_launch_when_the_launch_step_throws_after_committing()
    {
        // Pins the call-site wiring, not just RecoverOrphanedLaunchAsync in isolation: every test above drives that
        // method directly by its InternalsVisibleTo seam, so NONE of them would notice if RunAsync's own call were
        // ever reverted from LaunchOrRecoverAsync back to the plain, non-recovering LaunchAsync. This drives the SUT
        // through its REAL public entry point (RunAsync) instead, substituting ONLY ITaskLaunchService with a fake
        // that seeds the exact "committed, then a later step threw" row shape LaunchOrRecoverAsync guards against —
        // mirroring what RunFromSnapshotStarter.StageAsync's own single SaveChangesAsync would have left behind —
        // then throws. Recovery having actually run (Purpose re-stamped, session archived) is observable ONLY if
        // RunAsync reached LaunchOrRecoverAsync's catch block; reverting the call site leaves both assertions below
        // false (Purpose stays null, session stays Open), turning this test red.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        using var workspace = Fixture.Stage(checkExitCode: 0);

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope(b => b.RegisterType<OrphanAfterCommitTaskLaunchService>().As<ITaskLaunchService>());
        var sut = scope.Resolve<ITaskLaunchBenchmarkCellRunner>();
        var context = new BenchmarkExecutionContext { WorkspaceDirectory = workspace.Directory, TeamId = teamId, Selection = null };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => sut.RunAsync(TestsPassTask(), BenchmarkMode.TaskLaunchQuick, context, CancellationToken.None));
        ex.Message.ShouldContain("simulated post-commit failure", customMessage: "the ORIGINAL launch exception must still be the one that propagates — recovery is best-effort and never swallows it");

        using var assertScope = _fixture.BeginScope();
        var db = assertScope.Resolve<CodeSpaceDbContext>();

        var orphan = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.TeamId == teamId);
        orphan.Purpose.ShouldBe(Messages.Constants.WorkflowRunPurposes.Qualification, "RunAsync must reach LaunchOrRecoverAsync's catch block for the orphan to ever be re-stamped — LaunchAsync alone never recovers anything");

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == orphan.SessionId);
        session.Status.ShouldBe(Messages.Enums.WorkSessionStatus.Archived, "recovery must have actually archived the orphaned session — only reachable through RunAsync calling LaunchOrRecoverAsync");
    }

    /// <summary>
    /// Test-only <see cref="ITaskLaunchService"/> substitute for the call-site pin above. Seeds the EXACT row shape
    /// the real service's own single <c>SaveChangesAsync</c> (<c>RunFromSnapshotStarter.StageAsync</c>) would have
    /// left committed — an OPEN <c>WorkSession</c> plus a snapshot <c>WorkflowRun</c>, <c>Purpose</c> still null,
    /// <c>ScopeRepositoryIds</c> carrying the request's OWN <see cref="TaskLaunchRequest.RepositoryId"/> exactly like
    /// a real launch — then throws, standing in for a later step (route/purpose stamping, the ledger write, the
    /// post-commit dispatcher) failing after that commit. Registered ONLY for the test above (<c>BeginScope</c>
    /// override), so every other test in this class still exercises the real <c>ITaskLaunchService</c>.
    /// </summary>
    private sealed class OrphanAfterCommitTaskLaunchService : ITaskLaunchService
    {
        private readonly CodeSpaceDbContext _db;

        public OrphanAfterCommitTaskLaunchService(CodeSpaceDbContext db) { _db = db; }

        public async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
        {
            var createdDate = DateTimeOffset.UtcNow;
            var sessionId = Guid.NewGuid();

            _db.WorkSession.Add(new WorkSession
            {
                Id = sessionId, TeamId = request.TeamId, Title = "orphan-after-commit fixture", Kind = Messages.Enums.WorkSessionKind.Task,
                Status = Messages.Enums.WorkSessionStatus.Open, LastActivityAt = createdDate,
                CreatedDate = createdDate, CreatedBy = request.ActorUserId, LastModifiedBy = request.ActorUserId,
            });

            var requestId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            _db.WorkflowRunRequest.Add(new WorkflowRunRequest
            {
                Id = requestId, TeamId = request.TeamId, WorkflowId = null, SourceType = Messages.Constants.WorkflowRunSourceTypes.Snapshot,
                ActorType = Messages.Constants.WorkflowRunActorTypes.User, ActorId = request.ActorUserId, NormalizedPayloadJson = "{}",
                Status = Messages.Enums.WorkflowRunRequestStatus.Consumed, ReceivedAt = createdDate, VerifiedAt = createdDate, NormalizedAt = createdDate,
            });
            _db.WorkflowRun.Add(new WorkflowRun
            {
                Id = runId, WorkflowId = null, TeamId = request.TeamId, RunRequestId = requestId,
                SourceType = Messages.Constants.WorkflowRunSourceTypes.Snapshot, ActorId = request.ActorUserId, SessionId = sessionId, Purpose = null,
                ScopeRepositoryIds = request.RepositoryId is { } repositoryId ? new List<Guid> { repositoryId } : [],
                Status = Messages.Enums.WorkflowRunStatus.Pending, CreatedDate = createdDate, CreatedBy = request.ActorUserId, LastModifiedBy = request.ActorUserId,
            });

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("simulated post-commit failure — mirrors route/purpose stamping, the ledger write, or the post-commit dispatcher throwing after the real WorkSession+WorkflowRun commit");
        }
    }

    private sealed class CapturingFailTaskLaunchService : ITaskLaunchService
    {
        public TaskLaunchRequest? Request { get; private set; }

        public Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            throw new InvalidOperationException("captured the launch request");
        }
    }

    /// <summary>Seeds exactly the committed shape <c>RunFromSnapshotStarter.StageAsync</c> leaves behind: an OPEN <c>WorkSession</c> plus its <c>WorkflowRun</c> (snapshot-sourced, actored by the borrowed Owner, <c>Purpose</c> still null, scoped to <paramref name="repositoryId"/> exactly like a real launch's <c>ScopeRepositoryIds</c>), <c>createdDate</c> controlling which side of the attempt-start boundary it falls on.</summary>
    private async Task<(Guid RunId, Guid SessionId)> SeedOrphanedQualificationLaunchAsync(Guid teamId, Guid ownerId, Guid repositoryId, DateTimeOffset createdDate)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var sessionId = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession
        {
            Id = sessionId, TeamId = teamId, Title = "qualification orphan fixture", Kind = Messages.Enums.WorkSessionKind.Task,
            Status = Messages.Enums.WorkSessionStatus.Open, LastActivityAt = createdDate,
            CreatedDate = createdDate, CreatedBy = ownerId, LastModifiedBy = ownerId,
        });

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = Messages.Constants.WorkflowRunSourceTypes.Snapshot,
            ActorType = Messages.Constants.WorkflowRunActorTypes.User, ActorId = ownerId, NormalizedPayloadJson = "{}",
            Status = Messages.Enums.WorkflowRunRequestStatus.Consumed, ReceivedAt = createdDate, VerifiedAt = createdDate, NormalizedAt = createdDate,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = Messages.Constants.WorkflowRunSourceTypes.Snapshot, ActorId = ownerId, SessionId = sessionId, Purpose = null,
            ScopeRepositoryIds = [repositoryId],
            Status = Messages.Enums.WorkflowRunStatus.Pending, CreatedDate = createdDate, CreatedBy = ownerId, LastModifiedBy = ownerId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (runId, sessionId);
    }

    // ─── plumbing ────────────────────────────────────────────────────────────────

    private async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory, Guid teamId, IBenchmarkCellCompletionSink? completion = null)
    {
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<ITaskLaunchBenchmarkCellRunner>();

        var context = new BenchmarkExecutionContext { WorkspaceDirectory = workspaceDirectory, TeamId = teamId, Selection = null, Completion = completion };

        return await sut.RunAsync(task, mode, context, CancellationToken.None);
    }

    private sealed class RecordingCompletionSink : IBenchmarkCellCompletionSink
    {
        public BenchmarkResult? Result { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task CompleteAsync(BenchmarkResult result, CancellationToken cancellationToken)
        {
            Result = result;
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class InterruptingDurableCellSink : IBenchmarkCellCheckpointSink, IBenchmarkCellCompletionSink
    {
        private readonly Dictionary<string, BenchmarkExecutionCheckpoint> _checkpoints = new(StringComparer.Ordinal);
        public bool BlockLaunchBinding { get; set; }
        public bool BlockAfterLaunchStarted { get; set; }
        public BenchmarkResult? Result { get; private set; }
        public IReadOnlyDictionary<string, BenchmarkExecutionCheckpoint> Checkpoints => _checkpoints;

        public Task PutAsync(string kind, string payloadJson, CancellationToken cancellationToken)
        {
            if (kind == TaskLaunchBenchmarkCellRunner.LaunchCheckpointKind && BlockLaunchBinding) throw new InvalidOperationException("simulated process loss after WorkflowRun commit");
            if (_checkpoints.TryGetValue(kind, out var existing) && existing.PayloadJson != payloadJson) throw new InvalidOperationException($"checkpoint {kind} changed");
            _checkpoints[kind] = new BenchmarkExecutionCheckpoint(kind, payloadJson, DateTimeOffset.UtcNow);
            if (kind == TaskLaunchBenchmarkCellRunner.LaunchStartedCheckpointKind && BlockAfterLaunchStarted) throw new InvalidOperationException("simulated process loss after durable launch intent");
            return Task.CompletedTask;
        }

        public Task CompleteAsync(BenchmarkResult result, CancellationToken cancellationToken) { Result = result; return Task.CompletedTask; }
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

    /// <summary>
    /// The exact counts a real operator/owner would see, read through the SAME production entry points they use —
    /// never an ad-hoc predicate that could drift from what those services actually filter on:
    /// <c>ProviderInstanceService.ListAsync</c>'s own (team, not-deleted) predicate; <c>IWorkflowService.ListTeamRunsAsync</c>
    /// (the team's own Runs index); <c>ISessionReadService.ListAsync</c> (the team-wide sessions index — NO status
    /// filter, so an archived row still counts, the disclosed residual); and <c>IConversationService.ListForUserAsync</c>
    /// for the borrowed Owner (the exact list that person would see).
    /// </summary>
    private async Task<(int ProviderInstances, int RunsIndexCount, int AllTeamSessionsListed, int OwnersConversationsListed)> LeakSurfaceAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var providerInstances = await db.ProviderInstance.AsNoTracking().CountAsync(p => p.TeamId == teamId && p.DeletedDate == null);

        var runsPage = await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, RunListFilter.None, cursor: null, limit: 100, CancellationToken.None);
        var sessionsPage = await scope.Resolve<ISessionReadService>().ListAsync(teamId, cursor: null, limit: 100, CancellationToken.None);
        var ownersConversations = await scope.Resolve<IConversationService>().ListForUserAsync(teamId, ownerId, CancellationToken.None);

        return (providerInstances, runsPage.Items.Count, sessionsPage.Items.Count, ownersConversations.Count);
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
