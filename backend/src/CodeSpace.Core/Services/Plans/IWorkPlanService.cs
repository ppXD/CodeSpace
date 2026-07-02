using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Plans;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// The durable plan-artifact store (triad slice S1) — the ONE place every plan producer persists a run's
/// plan versions and every reader loads them (Rule 18.3: the concern's identity abstraction at its root).
/// Producers stay thin: they hand over a <see cref="WorkPlanDraft"/>; the store owns version assignment,
/// exactly-once (the draft's origin key), and team scoping. Narrow by design (Rule 7) — confirmation-state
/// transitions (S3) arrive as sibling members when their consumer exists, not speculatively.
/// </summary>
public interface IWorkPlanService
{
    /// <summary>
    /// Persist the run's NEXT plan version. When the draft carries an <c>OriginKey</c> and that (run, key)
    /// already has a row, the EXISTING row is returned unchanged (exactly-once for crash-replayed producers);
    /// otherwise a new row with version = max(run)+1 is inserted. Safe under concurrent writers — the unique
    /// indexes resolve races and the loser re-reads/re-numbers.
    /// </summary>
    Task<WorkPlan> SaveVersionAsync(WorkPlanDraft draft, CancellationToken cancellationToken);

    /// <summary>The run's CURRENT plan — its highest version — or null when the run has no plan. Team-scoped (a foreign team reads null). Pass <paramref name="originKind"/> to scope to ONE producer's versions (the S3 gate reads only supervisor-authored plans, so a co-resident plan.author node's version can never be flipped or named by the supervisor's card).</summary>
    Task<WorkPlan?> GetCurrentAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken, string? originKind = null);

    /// <summary>Every plan version of the run, oldest first. Team-scoped.</summary>
    Task<IReadOnlyList<WorkPlan>> ListVersionsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Compare-and-swap ONE plan row's confirmation status (S3 gate: Authored → AwaitingConfirmation on park,
    /// AwaitingConfirmation → Confirmed / Rejected on the human's answer). Team-scoped; returns false when the
    /// row is absent, foreign, or not at <paramref name="fromStatus"/> — so a crash-replayed flip is a no-op,
    /// never a double transition.
    /// </summary>
    Task<bool> SetStatusAsync(Guid planId, Guid teamId, string fromStatus, string toStatus, CancellationToken cancellationToken);
}
