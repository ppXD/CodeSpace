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
    /// capability registered, mode registered, mode holding <c>ProtocolReadiness.Enforceable</c> — are neither
    /// applied nor re-derivable here: this row records no mode and no capability key. Every consumer of this column
    /// therefore counts runs the authority would in fact park.</para>
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
    public int RejectionCount { get; set; }
    public int ContractErrorCount { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
