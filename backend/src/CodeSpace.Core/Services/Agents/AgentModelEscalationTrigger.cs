namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// D3 — whether a finished attempt is EVIDENCE that its model was the limit, and therefore that the next attempt
/// should reach for a stronger one. The quick lane's counterpart to
/// <see cref="Supervisor.SupervisorRetryEscalation.EscalationReason"/> (which reads a supervisor turn's cadence and
/// per-unit contradiction off the decision tape); this one reads a single agent run's own verdict.
///
/// <para>Representation-agnostic, exactly like <see cref="AgentContradiction"/>: the executor's revise loop projects
/// an <c>AgentRunResult</c> into these primitives and the agent.run node projects its flat resume payload into the
/// same ones, so "the model was the limit" can never mean two different things in the two lanes.</para>
///
/// <para>Escalation costs real money, so the bar is EVIDENCE, not merely failure. Three exclusions, each because a
/// stronger model provably cannot change the outcome: an INFRA-classed acceptance detail (the check itself never
/// ran — <see cref="AgentAcceptanceContract.IsInfraFailure"/>, the one shared classification); a gateway wire fault
/// (<see cref="Supervisor.AgentRetryCauses"/> already owns that repair — a fresh start with thinking disabled — and
/// a pricier model would meet the same broken gateway); and a failure with neither produced work nor a self-report
/// to contradict, which says nothing about the model at all.</para>
/// </summary>
public static class AgentModelEscalationTrigger
{
    /// <summary>
    /// Why the next attempt should escalate, or null when it shouldn't. <paramref name="acceptanceFailed"/> is the
    /// OBJECTIVE verdict being false (never merely a failed run — a run can die for a hundred reasons that prove
    /// nothing about its model); <paramref name="workPresent"/> is git ground truth (changed files or a produced
    /// branch); <paramref name="error"/> is the terminal error the cause classifier reads.
    /// </summary>
    public static string? Reason(string? contradiction, bool acceptanceFailed, string? acceptanceDetail, bool workPresent, string? error)
    {
        if (Supervisor.AgentRetryCauses.Classify(error) is not null) return null;

        if (!acceptanceFailed) return null;

        if (AgentAcceptanceContract.IsInfraFailure(acceptanceDetail, workPresent)) return null;

        var detail = string.IsNullOrWhiteSpace(acceptanceDetail) ? "no detail" : acceptanceDetail;

        if (contradiction == AgentContradiction.OverClaim)
            return $"the prior round claimed success but its acceptance check failed ({detail})";

        if (workPresent)
            return $"the prior round produced work but its acceptance check failed ({detail})";

        return null;
    }
}
