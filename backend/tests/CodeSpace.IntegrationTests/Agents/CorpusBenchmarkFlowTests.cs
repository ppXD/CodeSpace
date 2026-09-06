using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The CI PLUMBING PROOF for the CORPUS orchestrator (Rule 12 — high tier on the spine; honest fake at the boundary).
/// It drives the REAL <see cref="ICorpusBenchmarkRunner"/> → REAL <see cref="SeedFixtureStager"/> (staging each seed
/// fixture in its FAILING start-state) → REAL <c>BenchmarkRunner</c> → REAL <c>IAgentRunExecutor</c> → REAL
/// <c>TestsPassGrader</c> → REAL <c>BenchmarkScorecard</c> against real Postgres, over the WHOLE seed corpus. The ONLY
/// fake is the CLI's intelligence (a no-op /bin/sh codex that exits 0 without editing), so the loop runs end to end
/// with no key/network.
///
/// <para><b>Why this is plumbing, not a quality claim:</b> the no-op CLI never fixes the seeded failing checks, so
/// EVERY pair grades UNSOLVED even though every run lands <see cref="AgentRunStatus.Succeeded"/> — the corpus solve
/// rate is an honest 0.0. That is the corpus-level honesty made executable: the score is the objective grade across
/// the whole corpus, not the agents' self-reports, and CI never claims to measure real agent quality (a real-model
/// run on demand produces those numbers).</para>
///
/// <para>POSIX-only (Rule 12.1): the fake CLI + the seed checks are /bin/sh scripts the runner spawns.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CorpusBenchmarkFlowTests
{
    private readonly PostgresFixture _fixture;

    public CorpusBenchmarkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_whole_seed_corpus_runs_every_pair_and_aggregates_an_honest_zero_solve_baseline()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI + seed checks are /bin/sh scripts the runner spawns

        // The proxy is what BuildMcpWiring fail-closed-checks for: without it NO declaration is written, both arms run
        // identically tool-less, and every cli-mcp cell records the mode it ASKED for while measuring nothing.
        var proxy = BuiltMcpProxy.ExecutablePathOrNull();
        proxy.ShouldNotBeNull("the codespace-mcp proxy must be built beside its dll (the build-only ProjectReference in CodeSpace.IntegrationTests.csproj) — without it the mcp arm degrades to a tool-less run and this suite measures half the matrix while reporting the mode it requested");

        using var cli = new NoopBenchmarkCli(proxy);   // a no-op agent: succeeds without editing the seeded failing fixtures
        var teamId = await SeedTeamAsync();

        var corpus = SeedBenchmarkCorpus.Tasks;
        var expectedPairs = corpus.Sum(t => t.Modes.Count);

        CorpusBenchmarkRun run;
        using (var scope = _fixture.BeginScope())
            run = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(corpus, teamId, selection: null, CancellationToken.None);

        // EVERY (task × mode) pair ran end to end — staged, executed, graded — with NO infra error.
        run.Errored.ShouldBeEmpty("the offline seed corpus stages + runs cleanly — no infra faults");
        run.Results.Count.ShouldBe(expectedPairs, "every (task × mode) pair in the seed corpus produced a graded result");

        // Each pair drove a REAL agent run that completed, yet the objective oracle graded it UNSOLVED (the no-op CLI
        // never fixed the failing check) — the run-completed-vs-actually-solved gap, made executable.
        run.Results.ShouldAllBe(r => r.RunStatus == AgentRunStatus.Succeeded);
        run.Results.ShouldAllBe(r => r.AgentRunId != null);
        run.Results.ShouldAllBe(r => r.Grade.Passed == false);

        // The corpus reduces to the per-mode rows the scorecard lays side by side, every one an honest 0.0 solve rate.
        run.Scorecard.Harnesses.Select(h => h.Harness).ShouldBe(new[] { "bench:cli", "bench:cli-mcp" }, ignoreOrder: true);
        run.Scorecard.Harnesses.ShouldAllBe(h => h.Total == corpus.Count && h.SuccessRate == 0.0);

        // Corpus A/B projection: BuildResult deserializes each run's ResultJson to carry the token / revise / exit-reason
        // fields a critic A/B reports. On this critic-OFF baseline every pair has zero revise rounds and no critic exit —
        // the byte-identical control arm, and the proof the projection actually populates (a fold regression would blank these).
        run.Results.ShouldAllBe(r => r.ReviseRounds == 0, "critic-off baseline ⇒ zero revise rounds on every pair (the control arm)");
        run.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.ExitReason), "the terminal ExitReason is projected from the run's ResultJson, never blank");
        run.Results.ShouldAllBe(r => r.ExitReason != "output-flagged", "no critic ran ⇒ no pair is critic-flagged");

        // M1a — the run names its suite and classifies EVERY cell over the FIXED denominator, and now over the WHOLE
        // matrix: the fake loads its declaration and speaks initialize, so an mcp-arm cell is a real measurement of a
        // REACHED fabric (an honest Unsolved — the no-op still fixes nothing) instead of InfraUnknown.
        //
        // This pin used to read 0.5, with the comment "the REAL-model corpus (a live CLI that does handshake) measures
        // the full matrix". The live corpus did NOT: the claude CLI was never pointed at the declaration the runner
        // wrote, so all 9 cli-mcp cells came back mcp-required-no-handshake on 8 consecutive real runs. The instrument
        // read half-healthy for the LIVE reason too, and the offline pin was where that stopped being visible.
        run.SuiteVersion.ShouldBe(EvalSuite.ManifestFor(corpus).Version, "every percentage claim names EXACTLY the suite it measured");
        run.Cells!.Count.ShouldBe(expectedPairs, "the FIXED denominator: every (task × mode) cell classified");
        run.Cells!.ShouldAllBe(c => c.State == CorpusCellState.Unsolved, "every cell is a real measurement now: the bare-cli arm ran, and the mcp arm's fabric served an initialize — nothing is infra-dead");
        EvalSuite.Score(run.Cells!).EvaluatorHealth.ShouldBe(1.0, "the offline rig exercises the WHOLE matrix once its CLI actually loads the declaration — a cli-mcp cell that classifies InfraUnknown means the fabric was never reached");
    }

    [Fact]
    public async Task An_errored_pair_still_occupies_its_cell_as_InfraUnknown_with_the_denominator_fixed()
    {
        if (OperatingSystem.IsWindows()) return;

        var proxy = BuiltMcpProxy.ExecutablePathOrNull();
        proxy.ShouldNotBeNull("the mcp-arm cell below is only a measurement while the proxy the declaration names exists — see the sibling test");

        using var cli = new NoopBenchmarkCli(proxy);
        var teamId = await SeedTeamAsync();

        // A one-task corpus whose fixture ref does not exist: staging throws → the pair is recorded errored.
        // M1a's point: that cell must STILL be counted — as InfraUnknown — never silently dropped from the divisor.
        var corpus = new[]
        {
            SeedBenchmarkCorpus.Tasks[0],
            SeedBenchmarkCorpus.Tasks[0] with { Id = "ghost-fixture", FixtureRef = "no-such-fixture-" + Guid.NewGuid().ToString("N")[..8] },
        };

        CorpusBenchmarkRun run;
        using (var scope = _fixture.BeginScope())
            run = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(corpus, teamId, selection: null, CancellationToken.None);

        var expectedCells = corpus.Sum(t => t.Modes.Count);

        run.Errored.ShouldNotBeEmpty("the ghost fixture cannot stage — an infra fault, loudly recorded");
        run.Cells!.Count.ShouldBe(expectedCells, "the denominator NEVER shrinks — an errored cell is a cell");

        var score = EvalSuite.Score(run.Cells!);
        score.InfraUnknown.ShouldBe(2, "the ghost task's two mode-cells — a fixture that cannot stage measures nothing; the good task's BOTH cells now do");
        score.Unsolved.ShouldBe(2, "the good task's two cells graded honestly Unsolved (the no-op CLI fixed nothing, on either arm)");
        score.Total.ShouldBe(expectedCells);
        score.EvaluatorHealth.ShouldBe(0.5, "a sick instrument is VISIBLE — half the cells carry no capability verdict — never silently healthy via a shrunken divisor");

        // The pre-M1a shape (scorecard over graded results only) reported the SAME rate with or without the ghost
        // task; the fixed-denominator score cannot — the infra-dead cells occupy their slots in the divisor.
        run.Scorecard.Overall.Total.ShouldBe(2, "the legacy scorecard still sees only the graded pairs — exactly the shrinking-divisor shape M1a's Cells replace for percentage claims");
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"corpus-{userId:N}@test.local", Name = $"corpus-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"corpus-{teamId:N}", Name = "Corpus Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>
    /// A no-op fake codex CLI: emits a minimal codex-shaped event stream and exits 0 WITHOUT touching the workspace, so
    /// the grade is driven purely by the fixture's start-state (failing) — the deterministic corpus plumbing proof.
    ///
    /// <para>It DOES do one real thing: when the run wired a tool fabric, it loads its MCP declaration out of
    /// <c>CODEX_HOME</c> (where the real codex CLI reads it), spawns the <c>codespace-mcp</c> proxy the declaration
    /// names, and speaks one <c>initialize</c> — so the <c>cli-mcp</c> arm's cells measure a REACHED fabric instead of
    /// classifying InfraUnknown on a fake that could never speak MCP at all. It also arms the proxy path, without which
    /// <c>BuildMcpWiring</c> fails closed and no declaration is written anywhere.</para>
    ///
    /// <para>Restores both env vars + deletes the dir on dispose.</para>
    /// </summary>
    private sealed class NoopBenchmarkCli : IDisposable
    {
        private readonly string? _original;
        private readonly string? _originalProxy;
        private readonly string _dir;

        public NoopBenchmarkCli(string? proxyBinaryPath)
        {
            _dir = Path.Combine(Path.GetTempPath(), "cs-corpus-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var script = Path.Combine(_dir, "fake-codex.sh");
            File.WriteAllText(script, ScriptBody);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _original = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar);
            Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);

            _originalProxy = Environment.GetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar);
            if (proxyBinaryPath is not null) Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, proxyBinaryPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _original);
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, _originalProxy);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }

        /// <summary>
        /// Read the run's declaration where the real codex CLI reads it (<c>$CODEX_HOME/config.toml</c>), take the
        /// server command + its arg + the socket/token env OUT OF THAT FILE — never from a constant here, so a change
        /// to what the harness renders reds this rather than passing against a stale mirror (Rule 12.5) — and pipe one
        /// JSON-RPC <c>initialize</c> through the proxy. <c>head -n 1</c> bounds the read; the trailing <c>sleep</c>
        /// holds stdin open long enough for the reply. A run with no fabric finds no file and does nothing.
        /// </summary>
        private static string ScriptBody =>
            "#!/bin/sh\n" +
            "cfg=\"$" + CodexHarness.ConfigHomeEnvVar + "/" + CodexHarness.McpDeclarationFile + "\"\n" +
            "if [ -f \"$cfg\" ]; then\n" +
            "  cmd=$(sed -n 's/^command = \"\\(.*\\)\"$/\\1/p' \"$cfg\" | head -1)\n" +
            "  arg=$(grep -o '\"--[a-z-]*\"' \"$cfg\" | head -1 | tr -d '\"')\n" +
            "  sock=$(sed -n 's/.*" + McpDeclarationWriter.SocketEnvVar + " = \"\\([^\"]*\\)\".*/\\1/p' \"$cfg\" | head -1)\n" +
            "  tok=$(sed -n 's/.*" + McpDeclarationWriter.TokenEnvVar + " = \"\\([^\"]*\\)\".*/\\1/p' \"$cfg\" | head -1)\n" +
            "  { printf '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\\n'; sleep 3; } | " +
            "env " + McpDeclarationWriter.SocketEnvVar + "=\"$sock\" " + McpDeclarationWriter.TokenEnvVar + "=\"$tok\" \"$cmd\" \"$arg\" 2>/dev/null | head -n 1 > /dev/null\n" +
            "fi\n" +
            "printf '{\"type\":\"agent_message\",\"message\":\"done (no-op corpus CLI)\"}\\n'\n" +
            "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
            "exit 0\n";
    }
}
