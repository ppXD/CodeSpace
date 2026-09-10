using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Quality;

/// <summary>
/// WHICH quality mechanism the run should spend its next increment of budget on (P22, Rule 18.1 — a data noun).
/// Deliberately a CLOSED set of MECHANISMS, not a set of task types: the policy picks among these by the run's
/// recorded evidence, never by a switch on what the task is called. Every member is something the platform can
/// actually spend budget on (or the two zero-cost exits, <see cref="AskHuman"/> and <see cref="Stop"/>).
///
/// <para>Each mechanism must EARN its place: P22-9c's same-budget ablation harness measures whether spending the
/// same budget through a mechanism beats spending it on more <see cref="SingleAgent"/> attempts, and a member that
/// never wins its ablation is removed rather than left as an unmeasured option. A new mechanism is a new member
/// plus a new ordered row in <c>QualityPolicy</c> — never a widening of an existing member's meaning (Rule 7).</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityMechanism
{
    /// <summary>Spend the increment on one more ordinary attempt by one agent — the cheapest mechanism, and the ablation BASELINE every other member is measured against.</summary>
    SingleAgent,

    /// <summary>Decompose the unit and spend the increment across smaller sub-units — the mechanism for work whose recorded scale suggests one pass cannot hold it.</summary>
    SplitIntoSubtasks,

    /// <summary>Spend the increment on an INDEPENDENT reviewer instead of more production — the mechanism that BUYS EVIDENCE where none exists or where recorded reviews disagree.</summary>
    IndependentCritic,

    /// <summary>Spend the increment on a stronger model for the same unit — reserved for work-classed failure the current capability keeps reproducing, never for an infra-classed one.</summary>
    EscalateModel,

    /// <summary>Spend a BOUNDED increment on repairing the machinery around the work (a check that never ran, a dependency that was unavailable) rather than on the work itself.</summary>
    BoundedRepair,

    /// <summary>Spend nothing and hand the decision to a human — only ever on RECORDED evidence that a human verdict is required, never as a generic fallback.</summary>
    AskHuman,

    /// <summary>Spend nothing more. Every stop carries its evidence in <see cref="QualityDecision.Reason"/> — the goal is met, the only available evidence approves, a human waived verification, the budget cannot buy another attempt, or the recorded no-progress cap is reached. A waived stop is never a recorded pass (the amend-acceptance FATAL-1 invariant); the reason says which stop this is.</summary>
    Stop,
}
