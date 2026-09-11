namespace CodeSpace.Messages.Agents;

/// <summary>
/// The COMPACT, decider-visible record of a <c>retry</c> decision's tier ESCALATION (A2/P4-2): the run's own
/// evidence (a contradiction between an agent's self-report and its acceptance grade, or the run running out of
/// no-progress budget) raised the model floor for this one retried unit. A pure data noun (Rule 18.1) — built by
/// <c>RealSupervisorActionExecutor.ExecuteRetryAsync</c>, read back by <c>SupervisorOutcome.ReadEscalation</c> so
/// the NEXT turn's recitation/decider prompt names what changed and why, instead of the model silently getting a
/// different dispatch with no explanation.
/// </summary>
public sealed record SupervisorRetryEscalationOutcome
{
    /// <summary>
    /// The escalated model's wire id this retry actually dispatched on — or NULL when the trigger fired and the
    /// team's credentialed pool held nothing above <see cref="From"/>'s effective tier (D3). A null <c>To</c> is a
    /// RECORD, not an absence: the retry ran on the same model on purpose, and the next turn's decider is told so
    /// rather than left to wonder why a retry changed nothing. A retry with no trigger at all records no outcome
    /// object whatsoever, so the ordinary decision tape stays byte-identical.
    /// </summary>
    public string? To { get; init; }

    /// <summary>The prior attempt's model, when known (null if the prior attempt never resolved one, or none existed).</summary>
    public string? From { get; init; }

    /// <summary>Why the floor was raised, in one legible sentence (e.g. "the prior attempt's self-report contradicted its acceptance grade (over_claim)").</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// P22-9b — what the pure quality policy recommended for THIS unit on the same turn, or null when it had no
    /// reading (a unit outside the newest plan, or one with no attempt yet). Recorded, never obeyed:
    /// <see cref="Reason"/> above is still the whole of why this retry escalated.
    /// </summary>
    public Quality.QualityMechanism? PolicyMechanism { get; init; }

    /// <summary>
    /// Whether the quality policy AGREED with this escalation — i.e. whether <see cref="PolicyMechanism"/> is
    /// <c>EscalateModel</c> too. Null when the policy had no reading to agree or disagree with.
    ///
    /// <para>This exists because the two mechanisms are KNOWN to diverge and 9b deliberately did not replace either:
    /// the legacy trigger escalates on the FIRST over-claim, while the policy's repeat floor is two consecutive
    /// failed verdicts, so a first over-claim escalates here and reads as an ordinary attempt there. Recording the
    /// disagreement is what lets P22-9c's ablation settle which floor is right, instead of one of them being
    /// switched off on an argument. A <c>false</c> is therefore expected and is not a fault.</para>
    /// </summary>
    public bool? PolicyAgrees { get; init; }
}
