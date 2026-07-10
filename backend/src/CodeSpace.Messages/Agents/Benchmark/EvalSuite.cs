namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// M1a — the immutable identity of ONE benchmark suite: a content-derived <see cref="Version"/> plus the FIXED
/// cell universe (every task × mode pair) all percentage claims are measured over. The version pins the suite's
/// task-level identity (ids, fixture REFERENCES, harnesses, oracles, timeouts, goals, modes) so a solve-rate is
/// never compared across silently different corpora, and the cell list is the FIXED DENOMINATOR: a cell that
/// could not run is still a cell. DELIBERATE EXCLUSION: fixture CONTENT is not hashed (only the ref string is) —
/// a fixture edit under an unchanged ref keeps the version; folding a content hash into the ref is a known
/// follow-up. A data noun (Rule 18.1); <c>EvalSuite</c> in Core computes it.
/// </summary>
public sealed record EvalSuiteManifest
{
    /// <summary>Content-derived version, algorithm-prefixed (e.g. <c>sha256/corpus-v2:ab12…</c>) — changing ANY task's id, fixture ref, harness, oracle, timeout, goal, or modes changes it; reordering tasks does NOT.</summary>
    public required string Version { get; init; }

    /// <summary>The complete (task × mode) cell universe, canonically ordered — the fixed denominator.</summary>
    public required IReadOnlyList<CorpusCellRef> Cells { get; init; }
}

/// <summary>One cell of the suite manifest: a (task, mode) pair.</summary>
public sealed record CorpusCellRef
{
    public required string TaskId { get; init; }

    public required BenchmarkMode Mode { get; init; }
}

/// <summary>
/// The four-state outcome of one suite cell — the M1a truth vocabulary. <see cref="CorpusCellState.Solved"/> and
/// <see cref="CorpusCellState.Unsolved"/> are CAPABILITY verdicts (the oracle ran); <see cref="CorpusCellState.InfraUnknown"/>
/// is an EVALUATOR verdict (the cell's plumbing failed — it proves nothing about the model but still occupies its
/// cell in the fixed denominator); <see cref="CorpusCellState.Abstained"/> is reserved for the impossible/contradictory
/// task tier (M3) — an honest decline scored as its own state, never laundered into Solved or Unsolved.
/// </summary>
public sealed record CorpusCellOutcome
{
    public required string TaskId { get; init; }

    public required BenchmarkMode Mode { get; init; }

    public required CorpusCellState State { get; init; }

    /// <summary>The grade detail / infra error backing the state (best-effort, for the operator reading a table).</summary>
    public string? Detail { get; init; }
}

public enum CorpusCellState
{
    Solved = 0,
    Unsolved = 1,
    Abstained = 2,
    InfraUnknown = 3,
}

/// <summary>
/// The fixed-denominator reduction of a suite's cells. <see cref="SolveRateOverSuite"/> divides by EVERY cell —
/// the conservative headline (an infra-dead cell hurts it, so a broken evaluator can never inflate capability) —
/// while <see cref="EvaluatorHealth"/> isolates the instrument's own health so a red run is attributable: low
/// health = fix the evaluator; full health + low solve rate = an honest capability result.
/// </summary>
public sealed record CorpusCellScore
{
    public required int Solved { get; init; }

    public required int Unsolved { get; init; }

    public required int Abstained { get; init; }

    public required int InfraUnknown { get; init; }

    public int Total => Solved + Unsolved + Abstained + InfraUnknown;

    /// <summary>Solved over ALL cells — the fixed denominator. 0 on an empty suite.</summary>
    public double SolveRateOverSuite => Total == 0 ? 0 : (double)Solved / Total;

    /// <summary>The share of cells whose verdict is a REAL capability verdict (not infra) — 1.0 on a healthy run.</summary>
    public double EvaluatorHealth => Total == 0 ? 0 : (double)(Total - InfraUnknown) / Total;
}
