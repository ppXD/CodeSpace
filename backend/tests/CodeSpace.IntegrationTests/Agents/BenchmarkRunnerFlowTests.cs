using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The CI PLUMBING PROOF for the benchmark instrument (Rule 12 — high tier on the spine; honest fakes at the
/// boundary). It drives the REAL <see cref="BenchmarkRunner"/> → REAL <c>IAgentRunExecutor</c> → REAL
/// <c>LocalProcessRunner</c> (spawning a REAL fake-CLI process) → REAL <see cref="TestsPassGrader"/> (re-running a
/// REAL check script in the post-run workspace) → REAL <see cref="BenchmarkScorecard"/> against real Postgres. The
/// ONLY fake is the CLI's intelligence (a /bin/sh script standing in for codex, so no key/network is needed) —
/// exactly the production execution pipeline otherwise.
///
/// <para><b>Why this is plumbing, not a quality claim:</b> the fake CLI does NOT edit code. So the grade is driven
/// PURELY by the workspace's start-state — a fixture already in its solved state grades PASS, one in its failing
/// state grades FAIL — even though BOTH runs land <see cref="AgentRunStatus.Succeeded"/> via the fake CLI. That is
/// the whole honesty point made executable: the objective grade is the repo's tests, NOT the agent's self-report,
/// and CI never claims to measure real agent quality (a real-model run on demand produces those numbers).</para>
///
/// <para>POSIX-only (Rule 12.1): the fake CLI + the check are /bin/sh scripts the runner spawns.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BenchmarkRunnerFlowTests
{
    private readonly PostgresFixture _fixture;

    public BenchmarkRunnerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_solved_workspace_grades_pass_and_lands_as_a_per_mode_scorecard_row()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI + check are /bin/sh scripts the runner spawns

        using var cli = new FakeBenchmarkCli();   // a no-op agent: succeeds without touching the workspace
        using var workspace = BenchmarkFixture.StageSolved();   // the check already exits 0

        var teamId = await SeedTeamAsync();
        var task = TestsPassTask();

        var result = await RunAsync(task, BenchmarkMode.HarnessCli, workspace.Directory, teamId);

        result.RunStatus.ShouldBe(AgentRunStatus.Succeeded, "the fake CLI exits 0, so the run completes");
        result.Grade.Passed.ShouldBeTrue("the post-run check exits 0 → the objective oracle grades it solved");
        result.Grade.Detail.ShouldBe("tests-passed");

        await AssertRealRunRecordedAsync(result, teamId);

        // The result lands as a comparable per-mode row on the SAME scorecard shape PR-A serves.
        var card = BenchmarkScorecard.Compute(new[] { result });
        var row = card.Harnesses.Single();
        row.Harness.ShouldBe("bench:cli");
        row.SuccessRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task A_failing_workspace_grades_fail_even_though_the_run_succeeded()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        using var workspace = BenchmarkFixture.StageFailing();   // the check exits 1; the no-op agent doesn't fix it

        var teamId = await SeedTeamAsync();

        var result = await RunAsync(TestsPassTask(), BenchmarkMode.HarnessCli, workspace.Directory, teamId);

        result.RunStatus.ShouldBe(AgentRunStatus.Succeeded, "the run still completes — the agent finished");
        result.Grade.Passed.ShouldBeFalse("but the post-run check still fails → the honest oracle grades it UNSOLVED, not by the run status");
        result.Grade.Detail.ShouldBe("tests-failed-exit-1");

        // The honest twist on the scorecard: a Succeeded-but-unsolved run scores a 0 solve rate.
        var card = BenchmarkScorecard.Compute(new[] { result });
        card.Harnesses.Single().SuccessRate.ShouldBe(0.0);
    }

    [Fact]
    public async Task The_same_task_through_both_cli_modes_differs_observably_on_the_mcp_fabric_not_just_the_label()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        var teamId = await SeedTeamAsync();

        // The SAME task, the SAME staged start-state, run through both harness-CLI modes in the SAME process. The only
        // intended difference is whether the run-scoped MCP tool fabric is reachable — driven per-run, not by a process-
        // wide flag the runner can't thread. So the two modes must NOT be byte-identical-but-relabelled.
        using var ws1 = BenchmarkFixture.StageSolved();
        using var ws2 = BenchmarkFixture.StageSolved();

        var cliRun = await RunAsync(TestsPassTask(), BenchmarkMode.HarnessCli, ws1.Directory, teamId);
        var mcpRun = await RunAsync(TestsPassTask(), BenchmarkMode.HarnessCliWithMcp, ws2.Directory, teamId);

        // The load-bearing distinction: the cli-mcp run actually opened the run-scoped MCP endpoint (the executor's
        // resolved per-run gate), the bare cli run did not — recorded on the result, so the two rows can never be
        // mislabeled-identical the way a label-only difference would be.
        cliRun.McpEndpointEnabled.ShouldBeFalse("the bare CLI mode runs with NO tool fabric — the baseline");
        mcpRun.McpEndpointEnabled.ShouldBeTrue("the cli-mcp mode opens the run-scoped MCP endpoint in the SAME process — the fabric is genuinely reachable, not just a different label");
        mcpRun.McpEndpointEnabled.ShouldNotBe(cliRun.McpEndpointEnabled, "two modes that execute byte-identically and differ only in their scorecard label would be a mislabeled comparison; these differ on the fabric");

        // Everything else is held equal so the comparison is honest: same objective grade, both runs complete.
        cliRun.RunStatus.ShouldBe(AgentRunStatus.Succeeded);
        mcpRun.RunStatus.ShouldBe(AgentRunStatus.Succeeded);
        cliRun.Grade.Passed.ShouldBe(mcpRun.Grade.Passed, "the same staged start-state grades the same under the objective oracle");

        // And they land as the two distinct comparable rows the scorecard exists to lay side by side.
        var card = BenchmarkScorecard.Compute(new[] { cliRun, mcpRun });
        card.Harnesses.Select(h => h.Harness).ShouldBe(new[] { "bench:cli", "bench:cli-mcp" });
    }

    [Fact]
    public async Task Two_modes_over_a_solved_and_a_failing_task_compare_side_by_side()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli();
        var teamId = await SeedTeamAsync();

        // Same mode, two tasks with opposite start-states → a 0.5 solve rate the scorecard reports as a number.
        using var solved = BenchmarkFixture.StageSolved();
        using var failing = BenchmarkFixture.StageFailing();

        var r1 = await RunAsync(TestsPassTask(), BenchmarkMode.HarnessCli, solved.Directory, teamId);
        var r2 = await RunAsync(TestsPassTask(), BenchmarkMode.HarnessCli, failing.Directory, teamId);

        var card = BenchmarkScorecard.Compute(new[] { r1, r2 });

        var row = card.Harnesses.Single(h => h.Harness == "bench:cli");
        row.Total.ShouldBe(2);
        row.Succeeded.ShouldBe(1, "one of the two tasks was actually solved — measured by tests, not self-report");
        row.SuccessRate.ShouldBe(0.5);
    }

    // ─── Helpers ───

    private async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDir, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IBenchmarkRunner>().RunAsync(task, mode, workspaceDir, teamId, CancellationToken.None);
    }

    private async Task AssertRealRunRecordedAsync(BenchmarkResult result, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        result.AgentRunId.ShouldNotBeNull("the runner records the real agent run it drove");

        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == result.AgentRunId!.Value);
        run.TeamId.ShouldBe(teamId, "the benchmark run is team-scoped like any agent run");
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    private static BenchmarkTask TestsPassTask() => new()
    {
        Id = "ci-plumbing-proof",
        Description = "prove task → mode → grade → scorecard plumbing deterministically",
        FixtureRef = "inline",
        Goal = "make the check pass",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = CodexHarness.HarnessKind,
        Modes = new[] { BenchmarkMode.HarnessCli },
        TimeoutSeconds = 60,
    };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"bench-{userId:N}@test.local", Name = $"bench-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"bench-{teamId:N}", Name = "Bench Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>
    /// A no-op fake codex CLI: emits a minimal codex-shaped event stream and exits 0 WITHOUT touching the
    /// workspace. The real <c>AgentRunExecutor</c> + <c>LocalProcessRunner</c> drive it; because it never edits
    /// code, the grade is determined PURELY by the fixture's start-state — which is exactly what makes this a
    /// deterministic PLUMBING proof, not a real-quality claim. Restores the env var + deletes the dir on dispose.
    /// </summary>
    private sealed class FakeBenchmarkCli : IDisposable
    {
        private readonly string? _original;
        private readonly string _dir;

        public FakeBenchmarkCli()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cs-bench-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var script = Path.Combine(_dir, "fake-codex.sh");
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                "printf '{\"type\":\"agent_message\",\"message\":\"done (no-op benchmark CLI)\"}\\n'\n" +
                "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
                "exit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _original = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar);
            Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _original);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Stages a benchmark fixture as a self-contained, offline local workspace — a dir with a <c>check.sh</c>
    /// whose exit code is the fixture's start-state. <see cref="StageSolved"/> already passes (exit 0);
    /// <see cref="StageFailing"/> fails (exit 1). The runner runs the agent here; the grader re-runs the check
    /// here. Disposing removes the dir.
    /// </summary>
    private sealed class BenchmarkFixture : IDisposable
    {
        public string Directory { get; }

        private BenchmarkFixture(int checkExitCode)
        {
            Directory = Path.Combine(Path.GetTempPath(), "cs-bench-fx-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);

            var check = Path.Combine(Directory, "check.sh");
            File.WriteAllText(check, $"#!/bin/sh\nexit {checkExitCode}\n");
            File.SetUnixFileMode(check, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        public static BenchmarkFixture StageSolved() => new(checkExitCode: 0);
        public static BenchmarkFixture StageFailing() => new(checkExitCode: 1);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
