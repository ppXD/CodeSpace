using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
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

    private static BenchmarkTask TestsPassTask() => new()
    {
        Id = "p19-quick-fixture",
        Description = "a pre-staged fixture this test stages directly (not via IBenchmarkFixtureStager)",
        FixtureRef = "p19-inline-fixture",
        Goal = "Make the check script pass.",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = "codex-cli",
        Modes = new[] { BenchmarkMode.TaskLaunchQuick },
    };

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
