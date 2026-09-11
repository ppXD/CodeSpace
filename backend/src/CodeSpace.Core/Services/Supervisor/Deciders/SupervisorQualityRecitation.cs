using System.Text;
using CodeSpace.Messages.Quality;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The QUALITY POLICY block (P22-9b): the pure <c>QualityPolicy</c>'s per-unit recommendation, recited into the
/// decider prompt at the same prompt-tail cluster the plan / budget / bounds recitations occupy. Pure over the
/// already-folded <see cref="SupervisorUnitQualityDecision"/>s — it derives nothing of its own, so the block and the
/// durable <c>quality_decisions</c> column can never disagree about what the policy said.
///
/// <para><b>A recommendation, not a mask.</b> The block is placed AFTER the run bounds it is bounded by and BEFORE
/// the verb roster, and it never touches the roster or <c>SupervisorActionMask</c>: a mechanism can neither offer a
/// verb the run may not emit nor withdraw one it may. The header says outright that the model may reject the
/// recommendation by naming the evidence it missed — which is why <see cref="SupervisorUnitQualityDecision.Reason"/>
/// is recited verbatim, evidence and all, rather than reduced to the mechanism's name.</para>
///
/// <para>Null until a unit has actually been ATTEMPTED (the fold emits no reading for an un-attempted unit), so
/// every pre-spawn turn's prompt stays byte-identical.</para>
/// </summary>
public static class SupervisorQualityRecitation
{
    /// <summary>The block's pinned header — a stable prompt landmark (tests + the model key on it), and the one place the block's non-binding status is stated.</summary>
    public const string Header = "QUALITY POLICY (per unit, derived from recorded evidence — a recommendation you may reject by naming the evidence it missed):";

    /// <summary>The block, or null when nothing has been attempted yet.</summary>
    public static string? Render(IReadOnlyList<SupervisorUnitQualityDecision> decisions)
    {
        if (decisions.Count == 0) return null;

        var builder = new StringBuilder(Header);

        foreach (var decision in decisions)
            builder.AppendLine().Append($"- [{decision.SubtaskId}] {decision.Mechanism} → {SteerFor(decision.Mechanism)} — {decision.Reason}");

        return builder.ToString();
    }

    /// <summary>
    /// What each mechanism means in the verbs this lane actually has — the mechanism enum is the policy's
    /// vocabulary, not the supervisor's, and a name like <c>BoundedRepair</c> is not a verb the model can emit.
    /// Each arm names the closest existing verb and, where the distinction matters, WHY (a same-model retry for
    /// machinery that failed, a stronger-model retry for capability that did).
    ///
    /// <para><see cref="QualityMechanism.IndependentCritic"/> is deliberately NOT given a verb: no per-unit work
    /// review exists in this lane (<c>SupervisorQualityFacts.NoWorkReviewIsRecorded</c>), so the honest steer is the
    /// statement of absence — inventing "spawn a reviewer" here would point the model at a mechanism the run cannot
    /// actually spend on.</para>
    /// </summary>
    private static string SteerFor(QualityMechanism mechanism) => mechanism switch
    {
        QualityMechanism.EscalateModel => "retry (stronger model)",
        QualityMechanism.SplitIntoSubtasks => "plan/replan",
        QualityMechanism.BoundedRepair => "retry (same model; the machinery failed, not the work)",
        QualityMechanism.AskHuman => "ask_human",
        QualityMechanism.Stop => "close",
        QualityMechanism.IndependentCritic => "ungraded; nothing recorded can grade this",
        QualityMechanism.SingleAgent => "retry",
        // A new mechanism (Rule 7: a new member plus a new ordered row) reaches here before anyone maps it to a
        // verb. Say so, rather than fall back to a verb the roster may not even offer for this turn.
        _ => "no verb mapped for this mechanism yet",
    };
}
