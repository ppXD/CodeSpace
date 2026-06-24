namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The outcome of running a WHOLE corpus (every <see cref="BenchmarkTask"/> × every one of its
/// <see cref="BenchmarkTask.Modes"/>) through the instrument and reducing it to one comparison (a pure data noun,
/// Rule 18.1). This is what turns the per-(task,mode) <see cref="BenchmarkResult"/> instrument into a benchmark
/// with a SCORE: <see cref="Scorecard"/> is the solve-rate over the pairs that actually RAN, per-mode.
///
/// <para><b>Honest infra split:</b> a (task,mode) pair whose plumbing could not even run (fixture staging or the
/// runner threw — an infra fault, not the agent failing to solve) is recorded in <see cref="Errored"/> and EXCLUDED
/// from the scorecard's denominator, never counted as an unsolved task. So the solve-rate is "of the tasks the agent
/// actually attempted, how many it solved" — the same infra-vs-capability honesty the real-model gates enforce —
/// rather than silently deflating the number with runner flakes.</para>
///
/// <para><b>Coverage contract (read this before quoting the rate):</b> the rate is over <see cref="Results"/> ONLY,
/// so a NON-EMPTY <see cref="Errored"/> means the headline number is PARTIAL coverage — "the solve-rate over the
/// {Results.Count} pairs that ran", NOT over the whole corpus. The offline seed corpus stages deterministically so
/// <see cref="Errored"/> is empty (the rate is full-coverage); the moment a heavier/remote fixture stager lands —
/// one that can reproducibly throw on the HARD tasks — a consumer/gate MUST reconcile <see cref="Errored"/> against
/// <see cref="Results"/> (else a 70%-over-3-survivors reads as 70%-of-corpus). That slice should make the gap
/// un-missable (a coverage field, or refusing a headline rate when <see cref="Errored"/> is non-empty).</para>
/// </summary>
public sealed record CorpusBenchmarkRun
{
    /// <summary>Every (task,mode) pair that RAN and was graded — the objective per-pair outcomes.</summary>
    public required IReadOnlyList<BenchmarkResult> Results { get; init; }

    /// <summary>Pairs whose plumbing could not run (an infra fault) — surfaced, not buried, and excluded from the solve-rate. Empty on a clean run.</summary>
    public required IReadOnlyList<CorpusBenchmarkError> Errored { get; init; }

    /// <summary>The solve-rate comparison over <see cref="Results"/>, per-mode, on the existing scorecard shape (success = the objective grade PASSED, not the run merely finishing).</summary>
    public required AgentRunScorecard Scorecard { get; init; }
}

/// <summary>One (task,mode) pair the corpus runner could not execute (an infra fault during fixture staging or the run) — kept so a flaky pair is visible without aborting the whole corpus or polluting the solve-rate.</summary>
public sealed record CorpusBenchmarkError
{
    public required string TaskId { get; init; }

    public required BenchmarkMode Mode { get; init; }

    /// <summary>The infra failure reason (the thrown message), for the operator to diagnose — NOT a solve verdict.</summary>
    public required string Error { get; init; }
}
