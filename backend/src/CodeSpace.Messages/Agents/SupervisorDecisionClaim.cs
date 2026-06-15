namespace CodeSpace.Messages.Agents;

/// <summary>
/// The outcome of <c>ISupervisorDecisionLog.TryClaimAsync</c> — whether the caller won the right to emit + execute this
/// supervisor decision, or a prior/concurrent row already owns it. The arbiter is the unique
/// <c>(supervisor_run_id, idempotency_key)</c> index, so exactly one caller for a given key ever gets
/// <see cref="SupervisorDecisionClaimOutcome.Proceed"/>. Mirrors <see cref="ToolCallClaimOutcome"/>.
/// </summary>
public enum SupervisorDecisionClaimOutcome
{
    /// <summary>We INSERTed a fresh Pending row — the caller emits + executes the decision, then records the terminal.</summary>
    Proceed,

    /// <summary>A TERMINAL row for (run, key) already exists — return its stored outcome, do NOT re-execute the decision.</summary>
    Duplicate,

    /// <summary>A non-terminal row (Pending / AwaitingApproval / Running) exists — a concurrent or prior-suspended decision owns the key; the caller must NOT double-execute.</summary>
    InFlight,
}

/// <summary>
/// The result of trying to claim the right to emit + execute a supervisor decision (a data noun, Rule 18.1 — carries
/// only primitives, never the Core entity). On <see cref="SupervisorDecisionClaimOutcome.Proceed"/> the caller executes
/// the decision under <see cref="DecisionId"/>; on <see cref="SupervisorDecisionClaimOutcome.Duplicate"/> it returns the
/// prior outcome (<see cref="PriorOutcomeJson"/> / <see cref="PriorError"/>) WITHOUT re-executing. Mirrors
/// <see cref="ToolCallClaim"/>.
/// </summary>
public sealed record SupervisorDecisionClaim
{
    public required SupervisorDecisionClaimOutcome Outcome { get; init; }

    /// <summary>The decision row this claim refers to (the freshly-inserted Pending row on Proceed, the existing row otherwise).</summary>
    public Guid DecisionId { get; init; }

    /// <summary>On Duplicate: the prior row's terminal status.</summary>
    public SupervisorDecisionStatus PriorStatus { get; init; }

    /// <summary>On a Duplicate success: the prior row's stored execution outcome (null on a duplicate failure).</summary>
    public string? PriorOutcomeJson { get; init; }

    /// <summary>On a Duplicate failure: the prior row's error (null on a duplicate success).</summary>
    public string? PriorError { get; init; }

    public static SupervisorDecisionClaim Proceed(Guid decisionId) => new() { Outcome = SupervisorDecisionClaimOutcome.Proceed, DecisionId = decisionId };
    public static SupervisorDecisionClaim InFlight(Guid decisionId) => new() { Outcome = SupervisorDecisionClaimOutcome.InFlight, DecisionId = decisionId };
    public static SupervisorDecisionClaim Duplicate(Guid decisionId, SupervisorDecisionStatus priorStatus, string? priorOutcomeJson, string? priorError) =>
        new() { Outcome = SupervisorDecisionClaimOutcome.Duplicate, DecisionId = decisionId, PriorStatus = priorStatus, PriorOutcomeJson = priorOutcomeJson, PriorError = priorError };
}
