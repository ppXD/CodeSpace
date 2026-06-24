using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.IntegrationTests.Infrastructure;
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

        using var cli = new NoopBenchmarkCli();     // a no-op agent: succeeds without editing the seeded failing fixtures
        var teamId = await SeedTeamAsync();

        var corpus = SeedBenchmarkCorpus.Tasks;
        var expectedPairs = corpus.Sum(t => t.Modes.Count);

        CorpusBenchmarkRun run;
        using (var scope = _fixture.BeginScope())
            run = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(corpus, teamId, CancellationToken.None);

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
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"corpus-{userId:N}@test.local", Name = $"corpus-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"corpus-{teamId:N}", Name = "Corpus Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>A no-op fake codex CLI: emits a minimal codex-shaped event stream and exits 0 WITHOUT touching the workspace, so the grade is driven purely by the fixture's start-state (failing) — the deterministic corpus plumbing proof. Restores the env var + deletes the dir on dispose.</summary>
    private sealed class NoopBenchmarkCli : IDisposable
    {
        private readonly string? _original;
        private readonly string _dir;

        public NoopBenchmarkCli()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cs-corpus-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var script = Path.Combine(_dir, "fake-codex.sh");
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                "printf '{\"type\":\"agent_message\",\"message\":\"done (no-op corpus CLI)\"}\\n'\n" +
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
}
