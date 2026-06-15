namespace CodeSpace.Messages.Agents;

/// <summary>
/// The decision vocabulary a supervisor turn can emit. <see cref="SupervisorDecisionRecord"/>'s
/// <c>DecisionKind</c> is an OPEN string (a new verb adds zero schema churn), so these are CONVENTION
/// constants — the values a decider may pick from — not a closed enum. E2 ships the minimal two-verb
/// vocabulary (<see cref="Plan"/> / <see cref="Stop"/>); E3 widens it to the full six
/// (plan · spawn · retry · ask_human · merge · stop) without touching the ledger or the wait machinery.
/// </summary>
public static class SupervisorDecisionKinds
{
    /// <summary>Decompose the goal into subtasks (E2 stub records a fixed planned list; E3 plans for real).</summary>
    public const string Plan = "plan";

    /// <summary>Terminate the supervisor turn loop — the run completes via the normal walk. The fail-closed budget-exhaustion verb too.</summary>
    public const string Stop = "stop";
}
