namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A4: ONE row per terminal contract-era workflow run — the durable projection of the north-star measurement that
/// was previously computed live over the most recent 100 runs and then thrown away. It is what makes
/// "the rate went from X to Y" answerable, and what makes the lesson A/B arm sliceable instead of merely recorded
/// per supervisor decision.
///
/// <para>Observation-only and UPSERTED BY RUN (unique on <see cref="WorkflowRunId"/>): a projection of one run's
/// settled facts, not a history of what each sweep pass thought — that history already lives in
/// <see cref="CompletionAssessmentRecord"/>. Nothing in the engine reads this row, and the live-computed scorecard
/// endpoints keep computing their own numbers, so a missing or stale row can never change a verdict.</para>
/// </summary>
public class RunScorecard : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The run this row scores. UNIQUE — the writer upserts.</summary>
    public Guid WorkflowRunId { get; set; }

    /// <summary>When the run reached its terminal, falling back to its last-modified stamp when the engine recorded no <c>CompletedAt</c> (a bypass terminal — an operator cancel, a reconciler sweep). The trend's bucketing key.</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>The run's <c>WorkflowRun.ProjectionKind</c> (a <c>TaskProjectionKinds</c> value) — null for an authored / non-task run.</summary>
    public string? ProjectionKind { get; set; }

    /// <summary>
    /// The router's effort tier the run was launched at. ALWAYS NULL today, deliberately: unlike
    /// <see cref="ProjectionKind"/> the effort mode is not denormalised onto <c>WorkflowRun</c> and no per-run
    /// durable fact carries it, so inferring one would be a guess dressed as a measurement. Declared so the shape
    /// does not change when a launch seam starts recording it.
    /// </summary>
    public string? EffortMode { get; set; }

    /// <summary>The run's metric@1 solve bit, read off its LATEST <see cref="CompletionAssessmentRecord"/> — the same verdict the live scorecard reads, never a status fallback.</summary>
    public bool Solved { get; set; }

    /// <summary>The work actually left the sandbox — a pushed manifest / opened PR, or (the repo-less lane) a current typed artifact manifest.</summary>
    public bool Delivered { get; set; }

    /// <summary>Every point the run stopped to ask a human anything (see <c>IHumanTouchReader</c>). Zero means unattended.</summary>
    public int HumanTouches { get; set; }

    /// <summary>THE north-star bit: <see cref="Solved"/> AND <see cref="Delivered"/> AND zero <see cref="HumanTouches"/>. Computed by the one existing <c>UnattendedDeliveryScorer</c>, never re-derived here; the schema's own CHECK pins the definition.</summary>
    public bool UnattendedSolvedWithDelivery { get; set; }

    /// <summary>The run's priced agent-execution spend; null when nothing in it was priceable — the fail-open qualifier, never a silent $0.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>The run's priced BRAIN-plane spend (its own decision / critic / planner / grader model calls, folded from the <c>interaction.completed</c> ledger); null when the run recorded no such call.</summary>
    public decimal? BrainPlaneUsd { get; set; }

    /// <summary>The lesson A/B arm the run ran under (<c>LessonArms</c>: injected / withheld / none), read off its supervisor decision rows. Null when the run has no decision ledger at all (a single-agent or plan-map run).</summary>
    public string? LessonArm { get; set; }

    /// <summary>The supervisor brain model this run's decisions were actually authored by, when it had one. Null for a run with no decision ledger, and for one whose rows name no model.</summary>
    public string? BrainModel { get; set; }

    /// <summary>The scorer contract this row was produced under (<c>UnattendedDeliveryScorer.ScorerVersion</c>) — so a rate is never silently compared across two different definitions of "solved with delivery".</summary>
    public string ScorerVersion { get; set; } = string.Empty;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
