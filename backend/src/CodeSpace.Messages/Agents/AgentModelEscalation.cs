namespace CodeSpace.Messages.Agents;

/// <summary>
/// D3 — a model-tier escalation on the QUICK (single-agent) lane: the round's own evidence said the model was the
/// limit, so the NEXT attempt reaches for a stronger credentialed model. The quick-lane twin of
/// <see cref="SupervisorRetryEscalationOutcome"/> (same three facts, a different carrier: that one rides a
/// supervisor decision's outcome JSON, this one rides the agent task/result envelope). A pure data noun (Rule 18.1).
///
/// <para>Three carriers, each with an unambiguous meaning — the CARRIER says which, never a flag:</para>
/// <list type="bullet">
/// <item><b><c>AgentTask.Escalation</c></b> — a REQUEST the dispatcher already decided this attempt owes:
/// <see cref="Reason"/> plus <see cref="From"/>, the prior attempt's model that sets the tier FLOOR.
/// <see cref="To"/> is always null there — the executor owns the pool read, so it resolves the pick against the
/// pool as it is NOW, never a stale one.</item>
/// <item><b><c>AgentRunResult.ModelEscalation</c></b> — what this run APPLIED: <see cref="To"/> is the model an
/// escalated round actually ran, or null when the pool held nothing above the floor (the one-model case, recorded
/// rather than silently dropped).</item>
/// <item><b><c>AgentRunResult.ProposedEscalation</c></b> — what the NEXT attempt should do, stamped when the run
/// ended with its own evidence still saying the model was the limit: <see cref="From"/> is the model this run
/// finished on, <see cref="To"/> the pick for the next attempt (null = nothing stronger exists, so a respawn would
/// only re-burn the same model). Deliberately SEPARATE from the applied record: an `agent.run` node reads this one
/// to decide whether a deterministic failure is worth respawning, and an applied record must never flip that
/// verdict for a run whose acceptance actually passed.</item>
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
