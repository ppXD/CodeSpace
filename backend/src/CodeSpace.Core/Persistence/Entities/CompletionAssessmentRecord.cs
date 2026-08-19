namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P2a-4: one APPEND-ONLY row per compose of a terminal contract-era run — the durable "what the protocol would
/// have said" record the Shadow sweep writes and P2b's terminal CAS will bind to. <see cref="LegacyIsSolved"/>
/// snapshots the legacy scorecard ladder AT COMPOSE TIME so the degraded-inflation delta is a standing query.
/// </summary>
public class CompletionAssessmentRecord : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string EnforcementMode { get; set; } = string.Empty;
    public string Basis { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Verification { get; set; } = string.Empty;
    public string AssessmentJson { get; set; } = string.Empty;
    public bool LegacyIsSolved { get; set; }

    /// <summary>
    /// P3b-4 (INACTIVE adapter): the sealed six-state <c>TerminalDecision</c> name the shadow derived for this run —
    /// parity evidence for P2b; never mutates the run's terminal (Lock Clause 1). Null on pre-P3b-4 rows.
    ///
    /// <para>An UPPER BOUND on the authority's verdict, not a copy of it: the shadow mirrors only the two
    /// EVIDENCE-dependent refusals (integrity violations, missing Required stages). The three STRUCTURAL refusals —
    /// capability registered, mode registered, mode holding <c>ProtocolReadiness.Enforceable</c> — are not applied
    /// here; a reader that needs the narrower number re-derives them from <see cref="RunMode"/> and
    /// <see cref="CapabilityKey"/> on this same row. A consumer that does not is counting runs the authority would
    /// in fact park.</para>
    /// </summary>
    public string? WouldBeTerminalDecision { get; set; }

    /// <summary>
    /// The ledger state this assessment left behind — captured AFTER composing, because composing is not read-only:
    /// it write-throughs the receipts it derives from the tape. A pre-compose snapshot would be stale the moment it
    /// was stored, and every later sweep would see a difference that was its own doing.
    ///
    /// <para>It lets a re-sweep tell "nothing new has arrived" apart from "nobody looked again" without paying for a
    /// recompose. Null on rows written before the column existed; those re-assess once and then carry one.</para>
    /// </summary>
    public string? LedgerWatermarkJson { get; set; }

    /// <summary>P2 (v4.3): the run's monotonic ledger version this assessment's compose actually read — captured AFTER the compose so its own write-through receipts are inside it. The revisit pass compares the head against the run's LATEST recorded value in SQL; null (a pre-slice row) compares stale once and converges.</summary>
    public long? LedgerVersion { get; set; }

    /// <summary>P0-A (dual projection): the metric@1 outcome name — the solve-rate's ONLY verdict column. Null on rows written before the projection existed; those runs read unassessed, never solved.</summary>
    public string? MetricOutcome { get; set; }

    /// <summary>The full <c>MetricAt1Projection</c> this compose produced — outcome plus its bindings (@1 attempt refs, obligation set, unit, versions), self-describing per row.</summary>
    public string? MetricJson { get; set; }

    /// <summary>The operating mode <c>RunModeClassifier</c> derived for this run — resolving it in the mode registry IS the mode-registration gate the shadow does not apply (<c>"generic"</c> is deliberately unregistered). Null on pre-slice rows.</summary>
    public string? RunMode { get; set; }

    /// <summary>WHAT this run was asked for, derived from its staked obligation set — resolving it in the capability registry IS the capability-registration gate the shadow does not apply. Null on pre-slice rows.</summary>
    public string? CapabilityKey { get; set; }

    /// <summary>The <c>ProtocolReadiness</c> name <see cref="RunMode"/>'s profile HELD when this row was written; null when the mode had no registered profile, and on pre-slice rows. Historical on purpose — a later registry edit shows up as drift against the registry's current standing instead of silently rewriting what a past run would have got.</summary>
    public string? ReadinessAtCompose { get; set; }

    /// <summary>Whether the reduce this run's answer was synthesized over read ALL of its branches — the run row's own <c>resultsCoverage.complete</c> fact, copied here so a partial-input answer is distinguishable per assessment. Null when the run recorded no such fact (every run but a budget-declaring plan-map). Recorded evidence only: nothing gates on it.</summary>
    public bool? ResultsCoverageComplete { get; set; }

    public int RejectionCount { get; set; }
    public int ContractErrorCount { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
