namespace CodeSpace.Messages.Agents;

/// <summary>
/// The terminal verdict a parked tool-call approval (durable mid-turn HITL, item D) resolves to — the value a blocked
/// handler call awaits via the in-memory waiter registry. <see cref="Approved"/> stamps the decision and lets the
/// handler run the side effect; <see cref="Rejected"/> fails the call without running it; <see cref="Expired"/> is the
/// deadline reaper's verdict (item D3). The durable ledger row is the authority — this is only the wake-up signal.
/// </summary>
public enum ToolApprovalOutcome
{
    Approved,
    Rejected,
    Expired,
}
