using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;
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
    private readonly Dictionary<Guid, Guid> _operators = new();

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

        // P4-U5 (L6 — the benchmark joins the spine's evidence law): the verdict is auditable bytes in CAS.
        result.Grade.EvidenceArtifactId.ShouldNotBeNull("a benchmark verdict without evidence is prose, not a fact");
        result.Grade.EvidenceText.ShouldBeNull("the transient text never persists past capture — same posture as the acceptance funnel");
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
        cliRun.McpFullCatalog.ShouldBeFalse("the bare CLI mode runs with NO tool fabric — the baseline");
        mcpRun.McpFullCatalog.ShouldBeTrue("the cli-mcp mode opens the run-scoped MCP endpoint in the SAME process — the fabric is genuinely reachable, not just a different label");
        mcpRun.McpFullCatalog.ShouldNotBe(cliRun.McpFullCatalog, "two modes that execute byte-identically and differ only in their scorecard label would be a mislabeled comparison; these differ on the fabric");

        // Both runs complete; the bare-cli arm grades on the oracle alone. The cli-mcp arm now ALSO requires the
        // fabric to have actually connected (P0-B2): this fake CLI never speaks MCP, so its passing oracle is
        // overridden to an Environment-classed infra grade — a pass without the fabric would have measured the
        // cli arm under the cli-mcp label. This is the rule's integration-tier pin, through the REAL executor,
        // REAL endpoint, and a real non-connecting client.
        cliRun.RunStatus.ShouldBe(AgentRunStatus.Succeeded);
        mcpRun.RunStatus.ShouldBe(AgentRunStatus.Succeeded);
        cliRun.Grade.Passed.ShouldBeTrue("the staged-solved start-state passes the objective oracle on the bare-cli arm");
        mcpRun.Grade.Passed.ShouldBeFalse("the fabric never handshook — the cli-mcp cell measured nothing about the mcp arm");
        mcpRun.Grade.Class.ShouldBe(GradeFailureClass.Environment, "infra, never a model verdict");
        mcpRun.Grade.Detail.ShouldStartWith("mcp-required-no-handshake");

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

    // ─── Gateway format fault: the cell recovers once, then stays infra-dead honestly ───

    /// <summary>The verbatim text the gateway's Anthropic-compat layer kills the CLI with — the thing the whole repair exists for, so the test must fail on nothing less.</summary>
    private const string LiveGatewayFormatFault = "API Error: Content block is not a thinking block";

    /// <summary>A REAL seed-corpus fixture ref — the respawn RE-STAGES through <c>IBenchmarkFixtureStager</c>, so a cell that can respawn must name a fixture the stager actually knows (the inline <c>"inline"</c> ref the plumbing cases use cannot be re-staged, and must not be, since they never fault).</summary>
    private const string SeedFixtureRef = "failing-assertion";

    /// <summary>The documented one-line edit that makes <c>SeedFixtureRef</c>'s check exit 0 — what "the agent solved it" means for this fixture.</summary>
    private const string SolveTheSeedFixture = "printf 'REPORTED_SUM=5\\n' > solution.sh\n";

    /// <summary>
    /// A CLI that dies on a mangled wire, and solves the task once it gets a clean one. It tells the two attempts apart
    /// by the degrade ITSELF (<c>MAX_THINKING_TOKENS=0</c>) rather than a counter, so a mitigation that stopped at the
    /// durable envelope and never reached the process would leave this fixture failing forever — the second half of the
    /// repair is proven end to end here, not assumed.
    /// </summary>
    private static readonly string GatewayFaultThenSolveScript =
        $"if [ \"${AgentRetryCauses.MaxThinkingTokensEnvVar}\" = \"0\" ]; then\n" +
        SolveTheSeedFixture +
        "  printf '{\"type\":\"agent_message\",\"message\":\"solved once the wire was clean\"}\\n'\n" +
        "  printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "  exit 0\n" +
        "fi\n" +
        $"echo '{LiveGatewayFormatFault}' >&2\n" +
        "exit 1\n";

    /// <summary>A gateway that stays broken: every attempt dies the same way, mitigated or not.</summary>
    private static readonly string GatewayFaultAlwaysScript = $"echo '{LiveGatewayFormatFault}' >&2\nexit 1\n";

    /// <summary>
    /// The pollution shape: the FIRST attempt edits the workspace — it even forges the solve — and litters a file, THEN
    /// dies on the mangled wire; the mitigated respawn touches nothing. So the cell can only grade PASS if the oracle
    /// judged the dead attempt's leftovers instead of the respawn's own (empty) work.
    /// </summary>
    private static readonly string GatewayFaultAfterDirtyingScript =
        $"if [ \"${AgentRetryCauses.MaxThinkingTokensEnvVar}\" = \"0\" ]; then\n" +
        "  printf '{\"type\":\"agent_message\",\"message\":\"clean wire, but this attempt changes nothing\"}\\n'\n" +
        "  printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "  exit 0\n" +
        "fi\n" +
        SolveTheSeedFixture +
        "printf 'scratch\\n' > leftover.txt\n" +
        $"echo '{LiveGatewayFormatFault}' >&2\n" +
        "exit 1\n";

    [Fact]
    public async Task A_cell_the_gateway_mangled_is_respawned_once_and_gets_a_real_capability_verdict()
    {
        // The lost-instrument shape, on the production path: the CLI dies in seconds on a mangled wire before the model
        // gets a turn. Before this, the benchmark lane had no respawn at all — the cell died where it stood, and with
        // enough of them the M1a evaluator-health floor refused to grade the whole corpus (9/18 cells infra-dead for 7
        // consecutive main runs). Now the cell buys the shared repair once and produces the verdict it was there for.
        if (OperatingSystem.IsWindows()) return;   // the fake CLI + check are /bin/sh scripts the runner spawns

        using var cli = new FakeBenchmarkCli(GatewayFaultThenSolveScript);
        using var workspace = BenchmarkFixture.StageSeed();   // a REAL seed fixture: the check fails until an agent that GOT A TURN fixes it

        var teamId = await SeedTeamAsync();
        var task = SeedFixtureTask();

        var result = await RunAsync(task, BenchmarkMode.HarnessCli, workspace.Directory, teamId);

        result.FormatFaultRespawns.ShouldBe(1, "the cell reports how hard the instrument had to work — the gateway's health, next to the number it produced");
        result.RunStatus.ShouldBe(AgentRunStatus.Succeeded, "the GRADED attempt is the respawn, the one that actually got a turn — not the death that preceded it");
        result.Grade.Passed.ShouldBeTrue("the respawned agent fixed the check — a real solve, which a cell that died where it stood could never have reported");

        // Two REAL runs, each with its own row + event log — a respawn is a second attempt, never a re-labelled first.
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().CountAsync(r => r.TeamId == teamId))
                .ShouldBe(2, "the mitigated respawn is a real second agent run, and exactly one of them");

        CellStateOf(task, result).ShouldBe(CorpusCellState.Solved, "the cell counts toward capability instead of leaving a hole in the fixed denominator");
    }

    [Fact]
    public async Task A_second_format_fault_leaves_the_cell_infra_dead_with_the_repair_spent_exactly_once()
    {
        // The honest other half: a mitigated attempt that hits the SAME fault has proven the repair does not hold here,
        // so the cell stays infra-dead exactly as it does today rather than re-billing a broken gateway. The cli-mcp arm
        // is the live shape — a CLI that dies in seconds never handshakes the fabric, so the arm's own rule classes the
        // cell Environment (infra), which is precisely what the evaluator-health floor counts.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli(GatewayFaultAlwaysScript);
        using var workspace = BenchmarkFixture.StageSeed();

        var teamId = await SeedTeamAsync();
        var task = SeedFixtureTask() with { Modes = new[] { BenchmarkMode.HarnessCliWithMcp } };

        var result = await RunAsync(task, BenchmarkMode.HarnessCliWithMcp, workspace.Directory, teamId);

        result.FormatFaultRespawns.ShouldBe(1, "ONE repair, never a loop against a gateway that is simply down");
        result.RunStatus.ShouldBe(AgentRunStatus.Failed, "the mitigated attempt died too — the cell is honestly lost");
        result.Grade.Class.ShouldBe(GradeFailureClass.Environment, "infra, never a model verdict");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().CountAsync(r => r.TeamId == teamId))
                .ShouldBe(2, "the second fault buys nothing further — two attempts, not three");

        CellStateOf(task, result).ShouldBe(CorpusCellState.InfraUnknown, "infra-dead, exactly as before — the respawn never launders a broken gateway into a capability verdict");
    }

    [Fact]
    public async Task A_respawn_is_graded_over_a_freshly_staged_tree_not_the_dead_attempts_leftovers()
    {
        // The respawn's SOUNDNESS condition. The faulted attempt is DOCUMENTED as one where "the model never got a
        // turn" — but nothing enforced it: both attempts execute in the SAME directory and the oracle runs over that
        // directory AFTERWARDS, so anything the dead attempt wrote before the gateway killed it was graded as the
        // respawn's work. Here the first attempt forges the solve outright and litters a scratch file, then dies; the
        // respawn touches nothing. A PASS would mean the corpus scored the UNION of two attempts as one @1 cell.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli(GatewayFaultAfterDirtyingScript);
        using var workspace = BenchmarkFixture.StageSeed();

        var teamId = await SeedTeamAsync();
        var task = SeedFixtureTask();

        var result = await RunAsync(task, BenchmarkMode.HarnessCli, workspace.Directory, teamId);

        result.FormatFaultRespawns.ShouldBe(1, "the repair WAS bought — this is the respawned path, not the single-attempt one");
        result.RunStatus.ShouldBe(AgentRunStatus.Succeeded, "the respawn itself completed; the question is which tree it was graded over");

        result.Grade.Passed.ShouldBeFalse(
            "the dead attempt's forged solve must not count as the respawn's work — a pass here means the oracle graded the union of both attempts, i.e. the repair laundered a first attempt into a capability claim");
        result.Grade.Detail.ShouldBe("tests-failed-exit-1", "the fixture is back in its failing start-state, so the check fails exactly as it does on a cell nobody touched");

        File.Exists(Path.Combine(workspace.Directory, "leftover.txt"))
            .ShouldBeFalse("the re-stage WIPES the workspace, not just the files the fixture happens to own — a scratch file the dead attempt left behind is still first-attempt work sitting in the respawn's tree");

        (await File.ReadAllTextAsync(Path.Combine(workspace.Directory, SeedBenchmarkFixtures.SolutionFileName)))
            .ShouldContain("REPORTED_SUM=4", customMessage: "the editable file is the FIXTURE's failing start-state again, re-materialized by the production stager — not the dead attempt's edit");

        CellStateOf(task, result).ShouldBe(CorpusCellState.Unsolved, "an honest model verdict over a clean tree — never infra, and never a laundered solve");
    }

    [Fact]
    public async Task A_cell_whose_fixture_cannot_be_re_staged_fails_closed_instead_of_grading_a_wiped_tree()
    {
        // FAIL-CLOSED, the other half of the re-stage. The wipe happens BEFORE the stager runs, so a stager that
        // cannot resolve the ref leaves an EMPTY workspace — and an empty workspace grades tests-failed, which would
        // charge the MODEL for an infra fault. So the throw must PROPAGATE: the corpus loop records the pair as an
        // infra error (InfraUnknown, outside the solve denominator) instead of scoring a tree nothing vouches for.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeBenchmarkCli(GatewayFaultThenSolveScript);
        using var workspace = BenchmarkFixture.StageSeed();

        var teamId = await SeedTeamAsync();
        var task = SeedFixtureTask() with { FixtureRef = "no-such-fixture" };

        await Should.ThrowAsync<ArgumentException>(() => RunAsync(task, BenchmarkMode.HarnessCli, workspace.Directory, teamId));
    }

    // ─── Helpers ───

    /// <summary>The cell's M1a four-state verdict, through the REAL classifier over a one-cell manifest — what the evaluator-health floor actually counts, never a re-derivation of it here.</summary>
    private static CorpusCellState CellStateOf(BenchmarkTask task, BenchmarkResult result) =>
        EvalSuite.Classify(EvalSuite.ManifestFor(new[] { task }), new[] { result }, Array.Empty<CorpusBenchmarkError>()).Single().State;

    private async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDir, Guid teamId)
    {
        using var scope = _fixture.BeginScopeAs(_operators[teamId], teamId);
        return await scope.Resolve<IBenchmarkRunner>().RunAsync(task, mode, new BenchmarkExecutionContext { WorkspaceDirectory = workspaceDir, TeamId = teamId }, CancellationToken.None);
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

    /// <summary>
    /// A cell on a REAL seed-corpus fixture, for the cases that actually fault. The respawn re-stages through the
    /// production <c>IBenchmarkFixtureStager</c>, so a cell that can fault must name a ref that stager resolves — the
    /// plumbing cases' <c>"inline"</c> ref would throw there (correct fail-closed behaviour, but not what these
    /// measure). The test command is the seed corpus's OWN default, so the oracle here is the corpus's oracle.
    /// </summary>
    private static BenchmarkTask SeedFixtureTask() => TestsPassTask() with
    {
        Id = "gateway-fault-respawn-proof",
        Description = "prove the gateway-format-fault repair end to end over a real seed fixture",
        FixtureRef = SeedFixtureRef,
        TestCommand = SeedBenchmarkCorpus.DefaultTestCommand,
    };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"bench-{userId:N}@test.local", Name = $"bench-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"bench-{teamId:N}", Name = "Bench Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        _operators.Add(teamId, userId);
        return teamId;
    }

    /// <summary>
    /// Stages a benchmark fixture as a self-contained, offline local workspace — a dir with a <c>check.sh</c>
    /// whose exit code is the fixture's start-state. <see cref="StageSolved"/> already passes (exit 0);
    /// <see cref="StageFailing"/> fails (exit 1); <see cref="StageSeed"/> materializes a REAL seed fixture through
    /// production. The runner runs the agent here; the grader re-runs the check here. Disposing removes the dir.
    /// </summary>
    private sealed class BenchmarkFixture : IDisposable
    {
        public string Directory { get; }

        private BenchmarkFixture(int checkExitCode)
        {
            Directory = NewWorkspace();

            var check = Path.Combine(Directory, "check.sh");
            File.WriteAllText(check, $"#!/bin/sh\nexit {checkExitCode}\n");
            File.SetUnixFileMode(check, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        private BenchmarkFixture(string seedFixtureRef)
        {
            Directory = NewWorkspace();
            SeedBenchmarkFixtures.Stage(seedFixtureRef, Directory);
        }

        private static string NewWorkspace()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cs-bench-fx-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }

        public static BenchmarkFixture StageSolved() => new(checkExitCode: 0);
        public static BenchmarkFixture StageFailing() => new(checkExitCode: 1);

        /// <summary>Stage <see cref="SeedFixtureRef"/> through the PRODUCTION materialiser the corpus's stager delegates to (Rule 12.7) — so the start-state a respawn re-stages to is byte-identical to what the corpus loop staged, never a copy in this file that can drift from it.</summary>
        public static BenchmarkFixture StageSeed() => new(SeedFixtureRef);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
