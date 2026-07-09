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
    /// <summary>The escalated model's wire id this retry actually dispatched on.</summary>
    public required string To { get; init; }

    /// <summary>The prior attempt's model, when known (null if the prior attempt never resolved one, or none existed).</summary>
    public string? From { get; init; }

    /// <summary>Why the floor was raised, in one legible sentence (e.g. "the prior attempt's self-report contradicted its acceptance grade (over_claim)").</summary>
    public required string Reason { get; init; }
}
