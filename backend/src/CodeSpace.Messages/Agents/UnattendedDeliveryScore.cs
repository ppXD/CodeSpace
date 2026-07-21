namespace CodeSpace.Messages.Agents;

/// <summary>
/// One workflow run reduced to the fields the unattended-delivery scorer needs (a data noun, Rule 18.1) — the
/// north-star measurement's input. Decoupled from the WorkflowRun / PublishManifest / SupervisorDecisionRecord /
/// ToolCallLedger entities so the scorer stays pure + DB-free + exhaustively unit-testable, mirroring
/// <see cref="SupervisorRunOutcome"/>.
///
/// <para><see cref="Solved"/> and <see cref="Delivered"/> are read off the run's <c>PublishManifest</c> rows — the
/// SAME ledger every publish-or-park consumer reads (I1/I2/I3) — so a single-agent run and a supervisor-orchestrated
/// run score identically: neither needs its own decision ledger to prove "did it actually solve + ship the task."
/// <see cref="HumanTouches"/> is the one signal PublishManifest can't supply: it counts every point where the run
/// asked (or was forced to ask) a human — an ask_human supervisor decision, or an MCP tool call parked for approval
/// — regardless of how that ask was ultimately resolved (answered, denied, or left to expire); the ask ITSELF broke
/// "unattended," independent of its outcome.</para>
/// </summary>
public sealed record UnattendedDeliveryRunOutcome
{
    public required Guid WorkflowRunId { get; init; }

    /// <summary>At least one PublishManifest row for the run graded its diff <see cref="PublishAcceptanceState.Passed"/>, and none graded <see cref="PublishAcceptanceState.Failed"/> — the objective oracle verdict, never the model's self-report.</summary>
    public required bool Solved { get; init; }

    /// <summary>At least one PublishManifest row for the run reached <see cref="Agents.PublishState.Pushed"/> or carries a <c>PullRequestNumber</c> — the diff actually left the sandbox, not merely graded.</summary>
    public required bool Delivered { get; init; }

    /// <summary>Ask-human supervisor decisions + approval-parked MCP tool calls this run recorded, summed. Zero means the run never stopped to ask a human anything.</summary>
    public required int HumanTouches { get; init; }

    /// <summary>The run's priced USD spend (via <c>ITeamCostService</c>); null when nothing in the run was priceable — the fail-open qualifier, never a silent $0.</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>The measurable shape of ONE run against the north-star: solved, delivered, human-touch count, cost, and the headline bit — solved AND delivered AND zero human touches. The unattended-delivery analogue of <see cref="SupervisorRunScore"/>.</summary>
public sealed record UnattendedDeliveryRunScore
{
    public required Guid WorkflowRunId { get; init; }
    public required bool Solved { get; init; }
    public required bool Delivered { get; init; }
    public required int HumanTouches { get; init; }
    public decimal? CostUsd { get; init; }

    /// <summary>The north-star per-run bit: <see cref="Solved"/> AND <see cref="Delivered"/> AND <see cref="HumanTouches"/> == 0 — task in, merged/published artifact out, zero human touches.</summary>
    public required bool UnattendedSolvedWithDelivery { get; init; }
}

/// <summary>
/// The cross-run roll-up over a team's scored runs — the north-star rate plus its two components (solve rate,
/// delivery rate) and cost as a guardrail. Every run in the window counts (terminal only — see the service); there
/// is no in-flight exclusion category here because an in-flight run simply is not yet in the population.
/// </summary>
public sealed record UnattendedDeliveryRollup
{
    public required int TotalRuns { get; init; }
    public required int SolvedRuns { get; init; }
    public required int DeliveredRuns { get; init; }
    public required int UnattendedSolvedWithDeliveryRuns { get; init; }

    /// <summary>THE north-star: <see cref="UnattendedSolvedWithDeliveryRuns"/> / <see cref="TotalRuns"/>, in 0..1; 0 when there are no runs.</summary>
    public required double UnattendedSolveWithDeliveryRate { get; init; }

    /// <summary><see cref="SolvedRuns"/> / <see cref="TotalRuns"/>, in 0..1; 0 when there are no runs.</summary>
    public required double SolveRate { get; init; }

    /// <summary><see cref="DeliveredRuns"/> / <see cref="TotalRuns"/>, in 0..1; 0 when there are no runs.</summary>
    public required double DeliveryRate { get; init; }

    /// <summary>Mean human touches per run (over ALL runs, not just the touched ones) — a run with zero touches pulls this toward 0.</summary>
    public required double AvgHumanTouches { get; init; }

    /// <summary>Summed priced USD across runs that had a priceable cost; null when NONE were priceable (distinct from a real $0) — mirrors <c>TeamCostRollup.EstimatedCostUsd</c>.</summary>
    public decimal? TotalCostUsd { get; init; }

    /// <summary>Runs whose cost could not be priced — the fail-open honesty qualifier on <see cref="TotalCostUsd"/>.</summary>
    public required int UnknownCostRuns { get; init; }

    /// <summary>P2b-prep (era-aware denominator, option c): PRE-PROTOCOL terminal runs in the window — visible, never scored: a rate names exactly what it was measured over, and old tape is never re-derived into a verdict. Every rate above is over contract-era runs ONLY.</summary>
    public int LegacyRuns { get; init; }

    /// <summary>Currently-SUSPENDED runs created in the window — the parked population the terminal denominator cannot see. Surfaced so a park-heavy period can never silently flatter the rates.</summary>
    public int SuspendedRuns { get; init; }

    /// <summary>P4-U4 (dual-read parity dashboard): contract-era runs in the window that HAVE a durable shadow assessment row — the population the two columns below are over.</summary>
    public int AssessedRuns { get; init; }

    /// <summary>Assessed runs whose LATEST assessment reads Outcome=Solved — the assessment-based solve count BESIDE the legacy ladder's <see cref="SolvedRuns"/>. The standing consumer-switch delta is the difference between the two over the same window; the primary rates above still read the LEGACY ladder until the P2b switch is argued on this very evidence.</summary>
    public int AssessmentSolvedRuns { get; init; }

    /// <summary>Assessed runs whose recorded would-be terminal decision is CleanSuccess — the ONLY VDS-eligible state (Lock Clause 5). The Enforced-era north-star numerator, visible while nothing is enforced yet.</summary>
    public int WouldBeCleanSuccessRuns { get; init; }
}

/// <summary>The team's unattended-delivery scorecard — the cross-run north-star roll-up plus recent per-run scores. The north-star-metric analogue of <see cref="SupervisorScorecard"/> / <see cref="AgentRunScorecard"/>.</summary>
public sealed record UnattendedDeliveryScorecard
{
    public required UnattendedDeliveryRollup Rollup { get; init; }

    /// <summary>The recent per-run scores (most-recent first), capped by the service so the payload stays bounded.</summary>
    public required IReadOnlyList<UnattendedDeliveryRunScore> Runs { get; init; }
}
