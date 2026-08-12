namespace CodeSpace.Messages.Contracts;

/// <summary>The @1 attempt one unit's metric verdict was read from — which attempt answered, pinned so a metric row is auditable back to the exact dispatch it scored.</summary>
public sealed record MetricAttemptRef
{
    public required string UnitId { get; init; }
    public required Guid AttemptId { get; init; }
    public required int AttemptOrdinal { get; init; }
}

/// <summary>
/// The METRIC@1 projection (P0-A dual projection): the second, isolated output of the one completion compose —
/// same composer, same facts, same admission rules as the operational assessment, but receipts are admitted
/// against the FIRST server-authorized attempt per unit (<c>AttemptSelectors.SelectFirstAuthorized</c>) instead of
/// the operational active one. This is the solve-rate's verdict: no retry credit, never best-of-N, never a
/// human-corrected re-run — and structurally NO status fallback: a clean engine Success with nothing staked reads
/// <see cref="OutcomeDisposition.Unknown"/> here, so "it exited zero" can never move a metric again.
///
/// <para>The @1 statistical unit is FROZEN at run grain (<see cref="Unit"/> = <c>"run@1"</c>): one run, one
/// outcome, folded worst-first over its units' @1 attempts. A per-unit rate (<c>UnitVDS@1</c>) is a FUTURE unit
/// with its own string — never a silent reinterpretation of rows stamped <c>run@1</c>. Bindings make every row
/// self-describing: the @1 attempts read, the obligations staked, and the policy/suite versions the verdict was
/// computed under (suite/corpus are null outside a benchmark context).</para>
/// </summary>
public sealed record MetricAt1Projection
{
    /// <summary>The projection semantics version — bump when @1 selection/fold semantics change; rows computed under an older version are never silently reinterpreted.</summary>
    public const int CurrentProjectionVersion = 1;

    /// <summary>The frozen @1 statistical unit for this row shape: run grain.</summary>
    public const string RunAt1Unit = "run@1";

    public required int ProjectionVersion { get; init; }

    /// <summary>The @1 statistical unit this row was computed at — <see cref="RunAt1Unit"/> today.</summary>
    public required string StatisticalUnit { get; init; }

    /// <summary>The run-grain @1 outcome — the metric plane's ONLY solve signal.</summary>
    public required OutcomeDisposition Outcome { get; init; }

    /// <summary>The verification fold behind <see cref="Outcome"/>, kept for legibility (why did @1 not solve).</summary>
    public required VerificationDisposition Verification { get; init; }

    /// <summary>The first-authorized attempt per unit the verdict was read from (MetricRunAttemptRef).</summary>
    public required IReadOnlyList<MetricAttemptRef> AttemptRefs { get; init; }

    /// <summary>The staked obligations the verdict answered, as <c>kind:requirementRef</c> (MetricObligationSet).</summary>
    public required IReadOnlyList<string> ObligationRefs { get; init; }

    /// <summary>The run's stamped completion policy version the verdict was computed under; null on a legacy run.</summary>
    public int? CompletionPolicyVersion { get; init; }

    /// <summary>The benchmark corpus cell this run answers, when launched by a suite; null in production.</summary>
    public string? CorpusCellRef { get; init; }

    /// <summary>The eval-suite version that launched this run, when any; null in production.</summary>
    public string? EvalSuiteVersion { get; init; }
}
