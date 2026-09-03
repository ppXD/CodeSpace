using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// The corpus orchestrator — pins the cross-corpus LOOP + aggregation + the honest infra-error split, with a stub
/// instrument + stub stager (no real process / git / Postgres; the real plumbing is proven in
/// CorpusBenchmarkFlowTests). It must run EVERY (task × mode) pair, stage a fresh workspace per pair, reduce the
/// grades into a per-mode solve-rate, and record a pair whose plumbing throws as an EXCLUDED infra error rather than
/// aborting the corpus or deflating the score with an unsolved task.
/// </summary>
[Trait("Category", "Unit")]
public class CorpusBenchmarkRunnerTests
{
    private static readonly IReadOnlyList<BenchmarkMode> TwoModes = new[] { BenchmarkMode.HarnessCli, BenchmarkMode.HarnessCliWithMcp };

    [Fact]
    public async Task Runs_every_task_mode_pair_and_aggregates_a_per_mode_solve_rate()
    {
        // task A solves; task B does not. Across 2 modes → each mode has 1 solved + 1 unsolved = a 0.5 solve rate.
        var solved = new HashSet<string> { "task-a" };
        var runner = new StubRunner(passWhen: (taskId, _) => solved.Contains(taskId));
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        var corpus = new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) };

        var run = await sut.RunAsync(corpus, Guid.NewGuid(), selection: null, CancellationToken.None);

        run.Results.Count.ShouldBe(4, "every (task × mode) pair ran — 2 tasks × 2 modes");
        run.Errored.ShouldBeEmpty();
        runner.Calls.Count.ShouldBe(4);
        runner.Calls.ShouldContain(c => c.TaskId == "task-a" && c.Mode == BenchmarkMode.HarnessCli);
        runner.Calls.ShouldContain(c => c.TaskId == "task-b" && c.Mode == BenchmarkMode.HarnessCliWithMcp);

        // The scorecard reduces to per-mode rows whose success IS the objective grade (the solve rate), not run completion.
        foreach (var row in run.Scorecard.Harnesses)
        {
            row.Total.ShouldBe(2, "each mode ran both tasks");
            row.SuccessRate.ShouldBe(0.5, "one of the two tasks solved per mode");
        }
    }

    [Fact]
    public async Task A_pair_whose_run_timed_out_still_counts_in_the_corpus_denominator_not_silently_dropped()
    {
        // P4.2 — the exact scenario RealModelBenchmarkCorpusE2ETests' fixed denominator now depends on: a pair that
        // ran long enough to TIME OUT (never reached RunStatus.Succeeded) must still land in run.Scorecard.Overall.Total
        // as an attempted-but-unsolved pair — never vanish from the rate the way a stricter "Succeeded-only" filter would.
        var runner = new StubRunner(passWhen: (_, _) => true, timedOutWhen: (taskId, _) => taskId == "task-b");
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        var run = await sut.RunAsync(new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None);

        run.Results.Count.ShouldBe(4, "all 4 pairs RAN (task-b's just timed out instead of erroring at the plumbing layer)");
        run.Errored.ShouldBeEmpty("a timeout is a terminal run outcome, never an infra plumbing error");

        run.Scorecard.Overall.Total.ShouldBe(4, "every terminal pair — including the 2 timed-out ones — counts in the denominator");
        run.Scorecard.Overall.Succeeded.ShouldBe(2, "only task-a's 2 pairs solved");
        run.Scorecard.Overall.SuccessRate.ShouldBe(0.5, "2 of 4 — the timed-out pairs correctly drag the rate down instead of being excluded");
    }

    [Fact]
    public async Task Stages_a_fresh_isolated_workspace_per_pair_and_hands_it_to_the_runner()
    {
        var stager = new RecordingStager();
        var runner = new StubRunner(passWhen: (_, _) => true);
        var sut = new CorpusBenchmarkRunner(runner, stager, new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        await sut.RunAsync(new[] { MakeTask("task-a", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None);

        stager.Staged.Count.ShouldBe(2, "one fresh fixture staged per (task × mode) pair");
        stager.Staged.Select(s => s.Directory).Distinct().Count().ShouldBe(2, "each pair gets its OWN isolated workspace — never shared");
        stager.Staged.ShouldAllBe(s => s.FixtureRef == "fixture-task-a");
        // the runner received exactly the directory the stager prepared for that pair (no path drift)
        runner.Calls.Select(c => c.Workspace).ShouldBe(stager.Staged.Select(s => s.Directory), ignoreOrder: true);
    }

    [Fact]
    public async Task An_infra_throw_on_one_pair_is_recorded_as_errored_excluded_from_the_score_and_the_corpus_continues()
    {
        // The runner throws for task-b only (a runner-side infra fault). The corpus must NOT abort, and task-b must not
        // be scored as an unsolved task — it is excluded, surfaced in Errored.
        var runner = new StubRunner(passWhen: (_, _) => true, throwWhen: (taskId, _) => taskId == "task-b");
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        var run = await sut.RunAsync(new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None);

        run.Results.Count.ShouldBe(2, "only task-a's two pairs ran cleanly");
        run.Results.ShouldAllBe(r => r.TaskId == "task-a");
        run.Errored.Count.ShouldBe(2, "task-b's two pairs are recorded as infra errors, not silently dropped");
        run.Errored.ShouldAllBe(e => e.TaskId == "task-b" && e.Error.Length > 0);

        // The solve rate is over the ATTEMPTED (ran) pairs — task-a's, all solved → 1.0 — never deflated by task-b's infra flake.
        run.Scorecard.Harnesses.ShouldAllBe(row => row.SuccessRate == 1.0);
    }

    [Fact]
    public async Task A_staging_failure_is_an_infra_error_not_an_unsolved_task()
    {
        var runner = new StubRunner(passWhen: (_, _) => true);
        var sut = new CorpusBenchmarkRunner(runner, new ThrowingStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        var run = await sut.RunAsync(new[] { MakeTask("task-a", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None);

        run.Results.ShouldBeEmpty("nothing could run — staging failed for every pair");
        run.Errored.Count.ShouldBe(2);
        runner.Calls.ShouldBeEmpty("the runner is never invoked when its workspace could not be staged");
    }

    [Fact]
    public async Task A_caller_cancellation_propagates_and_is_not_swallowed_as_an_infra_error()
    {
        var runner = new StubRunner(passWhen: (_, _) => true, cancel: true);
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.RunAsync(new[] { MakeTask("task-a", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None));
    }

    [Fact]
    public async Task The_agent_selection_is_threaded_verbatim_to_the_instrument_for_every_pair()
    {
        // The corpus runner chooses the LOOP; the SELECTION (which agent attempts it — real harness/model/credential)
        // belongs to the run and must reach EVERY (task × mode) the instrument runs, unchanged — else a real-model gate
        // would silently run the fake CLI on some pairs.
        var runner = new StubRunner(passWhen: (_, _) => true);
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        var credId = Guid.NewGuid();
        var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = "gw-model", ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };

        await sut.RunAsync(new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) }, Guid.NewGuid(), selection, CancellationToken.None);

        runner.Calls.Count.ShouldBe(4, "the loop still ran every pair");
        runner.Calls.ShouldAllBe(c => ReferenceEquals(c.Selection, selection), "every pair got the SAME selection the caller passed — never null, never a copy");
    }

    [Fact]
    public async Task A_null_selection_reaches_the_instrument_as_null_the_deterministic_default()
    {
        var runner = new StubRunner(passWhen: (_, _) => true);
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance);

        await sut.RunAsync(new[] { MakeTask("task-a", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None);

        runner.Calls.ShouldAllBe(c => c.Selection == null, "no selection ⇒ the instrument runs the env's fake CLI with no credential (the CI plumbing default)");
    }

    // ─── A4: durable per-cell persistence ───

    [Fact]
    public async Task Every_graded_cell_is_persisted_with_its_suite_version_and_the_selection_that_attempted_it()
    {
        // Before this, a corpus run's per-cell results reached the CI step summary and nowhere else — the solve rate
        // was re-derived from scratch each run and never comparable across runs, commits, or model bundles.
        var store = new RecordingResultStore();
        var runner = new StubRunner(passWhen: (taskId, _) => taskId == "task-a");
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), store, NullLogger<CorpusBenchmarkRunner>.Instance);
        var teamId = Guid.NewGuid();
        var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = "claude-sonnet-4-5" };

        var run = await sut.RunAsync(new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) }, teamId, selection, CancellationToken.None);

        store.Recorded.Count.ShouldBe(4, "one row per graded (task × mode) cell — the same population run.Results carries");
        store.Recorded.ShouldAllBe(r => r.TeamId == teamId);
        store.Recorded.Select(r => r.SuiteVersion).Distinct().ShouldHaveSingleItem().ShouldBe(run.SuiteVersion, "the row is joined on the suite's content-derived identity, so two silently different corpora never share a rate");
        store.Recorded.ShouldAllBe(r => r.Selection == selection, "which agent attempted the cell is half of any comparison across model bundles");
        store.Recorded.Count(r => r.Result.Grade.Passed).ShouldBe(2, "the persisted grade is the OBJECTIVE one, not run completion");
    }

    [Fact]
    public async Task A_pair_that_never_ran_is_never_persisted()
    {
        var store = new RecordingResultStore();
        var runner = new StubRunner(passWhen: (_, _) => true, throwWhen: (taskId, _) => taskId == "task-b");
        var sut = new CorpusBenchmarkRunner(runner, new NoopStager(), store, NullLogger<CorpusBenchmarkRunner>.Instance);

        await sut.RunAsync(new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) }, Guid.NewGuid(), selection: null, CancellationToken.None);

        store.Recorded.Count.ShouldBe(2, "an infra-errored cell has no grade to record — persisting it would put a capability claim behind an evaluator fault");
        store.Recorded.ShouldAllBe(r => r.Result.TaskId == "task-a");
    }

    [Fact]
    public async Task A_persistence_fault_leaves_the_corpus_verdict_exactly_as_it_was()
    {
        // The load-bearing guarantee of an ADDITIVE observation table: it can neither red a passing corpus nor turn a
        // failing cell into an infra error. The gate reads the in-memory results; the write is best-effort.
        var runner = new StubRunner(passWhen: (taskId, _) => taskId == "task-a");
        var corpus = new[] { MakeTask("task-a", TwoModes), MakeTask("task-b", TwoModes) };
        var teamId = Guid.NewGuid();

        var healthy = await new CorpusBenchmarkRunner(runner, new NoopStager(), new RecordingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance)
            .RunAsync(corpus, teamId, selection: null, CancellationToken.None);

        var broken = await new CorpusBenchmarkRunner(new StubRunner(passWhen: (taskId, _) => taskId == "task-a"), new NoopStager(), new ThrowingResultStore(), NullLogger<CorpusBenchmarkRunner>.Instance)
            .RunAsync(corpus, teamId, selection: null, CancellationToken.None);

        broken.Results.Count.ShouldBe(healthy.Results.Count);
        broken.Errored.Count.ShouldBe(0, "a write fault is NOT an evaluator fault — it must not occupy the cell as InfraUnknown");
        broken.Scorecard.Overall.SuccessRate.ShouldBe(healthy.Scorecard.Overall.SuccessRate, "the verdict is byte-identical with a dead store");
        broken.SuiteVersion.ShouldBe(healthy.SuiteVersion);
    }

    // ─── stubs ───

    private static BenchmarkTask MakeTask(string id, IReadOnlyList<BenchmarkMode> modes) => new()
    {
        Id = id,
        Description = id,
        FixtureRef = $"fixture-{id}",
        Goal = "make the check pass",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = "codex-cli",
        Modes = modes,
    };

    private sealed record StagedCall(string FixtureRef, string Directory);

    private sealed class StubRunner : IBenchmarkRunner
    {
        private readonly Func<string, BenchmarkMode, bool> _passWhen;
        private readonly Func<string, BenchmarkMode, bool>? _throwWhen;
        private readonly Func<string, BenchmarkMode, bool>? _timedOutWhen;
        private readonly bool _cancel;
        public List<(string TaskId, BenchmarkMode Mode, string Workspace, BenchmarkAgentSelection? Selection)> Calls { get; } = new();

        public StubRunner(Func<string, BenchmarkMode, bool> passWhen, Func<string, BenchmarkMode, bool>? throwWhen = null, Func<string, BenchmarkMode, bool>? timedOutWhen = null, bool cancel = false)
        {
            _passWhen = passWhen; _throwWhen = throwWhen; _timedOutWhen = timedOutWhen; _cancel = cancel;
        }

        public Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory, Guid teamId, BenchmarkAgentSelection? selection, CancellationToken cancellationToken)
        {
            if (_cancel) throw new OperationCanceledException();
            if (_throwWhen?.Invoke(task.Id, mode) == true) throw new InvalidOperationException($"runner blew up on {task.Id}/{mode}");

            Calls.Add((task.Id, mode, workspaceDirectory, selection));

            // A timed-out pair never reached Succeeded and never passed the grade — a terminal RUN outcome, distinct
            // from an infra plumbing throw (which the corpus runner records as Errored, not a Result at all).
            if (_timedOutWhen?.Invoke(task.Id, mode) == true)
                return Task.FromResult(new BenchmarkResult
                {
                    TaskId = task.Id,
                    Mode = mode,
                    RunStatus = AgentRunStatus.TimedOut,
                    Grade = new BenchmarkGrade { Passed = false, Detail = "grade-error: tests-timed-out" },
                    McpFullCatalog = mode == BenchmarkMode.HarnessCliWithMcp,
                    DurationSeconds = 120.0,
                });

            var passed = _passWhen(task.Id, mode);
            return Task.FromResult(new BenchmarkResult
            {
                TaskId = task.Id,
                Mode = mode,
                RunStatus = AgentRunStatus.Succeeded,
                Grade = new BenchmarkGrade { Passed = passed, Detail = passed ? "tests-passed" : "tests-failed-exit-1" },
                McpFullCatalog = mode == BenchmarkMode.HarnessCliWithMcp,
                DurationSeconds = 1.0,
            });
        }
    }

    private sealed class NoopStager : IBenchmarkFixtureStager
    {
        public void Stage(string fixtureRef, string directory) { }
    }

    private sealed record RecordedCell(Guid TeamId, string SuiteVersion, BenchmarkResult Result, BenchmarkAgentSelection? Selection);

    /// <summary>Records every persisted cell so the loop's write side is asserted without Postgres.</summary>
    private sealed class RecordingResultStore : IBenchmarkResultStore
    {
        public List<RecordedCell> Recorded { get; } = new();

        public Task RecordAsync(Guid teamId, string suiteVersion, BenchmarkResult result, BenchmarkAgentSelection? selection, CancellationToken cancellationToken)
        {
            Recorded.Add(new RecordedCell(teamId, suiteVersion, result, selection));
            return Task.CompletedTask;
        }
    }

    /// <summary>A dead store — proves the observation write can never move the corpus verdict.</summary>
    private sealed class ThrowingResultStore : IBenchmarkResultStore
    {
        public Task RecordAsync(Guid teamId, string suiteVersion, BenchmarkResult result, BenchmarkAgentSelection? selection, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the benchmark_result write failed");
    }

    private sealed class RecordingStager : IBenchmarkFixtureStager
    {
        public List<StagedCall> Staged { get; } = new();
        public void Stage(string fixtureRef, string directory) => Staged.Add(new StagedCall(fixtureRef, directory));
    }

    private sealed class ThrowingStager : IBenchmarkFixtureStager
    {
        public void Stage(string fixtureRef, string directory) => throw new InvalidOperationException($"unknown fixture {fixtureRef}");
    }
}
