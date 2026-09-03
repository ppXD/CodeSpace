namespace CodeSpace.Messages.Agents;

/// <summary>
/// D3 — a model-tier escalation on the QUICK (single-agent) lane: the round's own evidence said the model was the
/// limit, so the NEXT attempt reaches for a stronger credentialed model. The quick-lane twin of
/// <see cref="SupervisorRetryEscalationOutcome"/> (same three facts, a different carrier: that one rides a
/// supervisor decision's outcome JSON, this one rides the agent task/result envelope). A pure data noun (Rule 18.1).
///
/// <para>It is BOTH the request and the outcome, distinguished by its carrier, not by a flag:</para>
/// <list type="bullet">
/// <item><b>On an <c>AgentTask</c></b> (<c>AgentTask.Escalation</c>) it is a REQUEST the dispatcher already decided
/// this attempt owes — <see cref="Reason"/> plus <see cref="From"/>, the prior attempt's model that sets the tier
/// FLOOR. <see cref="To"/> is always null there: the executor owns the pool read, so it resolves the pick.</item>
/// <item><b>On an <c>AgentRunResult</c></b> (<c>AgentRunResult.ModelEscalation</c>) it is the OUTCOME:
/// <see cref="To"/> names the model the escalated attempt actually ran, or is null when the team's credentialed pool
/// held nothing above the floor — the one-model case, recorded rather than silently dropped.</item>
/// </list>
/// </summary>
public sealed record AgentModelEscalation
{
    /// <summary>Why the floor was raised, in one legible sentence (e.g. "the prior round claimed success but its acceptance check failed (tests-failed-exit-1)").</summary>
    public required string Reason { get; init; }

    /// <summary>The model the escalation is measured FROM — the prior attempt's own, when known. Null when the prior attempt never resolved one (the floor is then <c>Unknown</c> and any tiered candidate qualifies).</summary>
    public string? From { get; init; }

    /// <summary>The escalated model actually dispatched. NULL on a request (not yet resolved), and null on an outcome when NOTHING in the team's credentialed pool beats <see cref="From"/>'s tier — the attempt ran on the same model, deliberately and visibly.</summary>
    public string? To { get; init; }
}
