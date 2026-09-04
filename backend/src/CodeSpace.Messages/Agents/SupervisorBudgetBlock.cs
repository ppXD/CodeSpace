namespace CodeSpace.Messages.Agents;

/// <summary>
/// A spawn wave the BUDGET admission refused (Rule 18.1 noun) — read off a spawn outcome's <c>budgetBlocked</c>
/// shape by <c>SupervisorOutcome.ReadBudgetBlock</c>. Written by
/// <c>RealSupervisorActionExecutor.StageAgentsAndParkAsync</c> when the all-or-nothing wave reservation is denied:
/// every fresh reservation is released and ZERO agents stage.
///
/// <para>Distinct from <c>SupervisorBlockedSubtask</c> (a dependency-staging withhold, per unit) and from a
/// REJECTED decision (a malformed action): this wave was well-formed and every unit was affordable individually —
/// the run simply has no money left for the wave. Without a legible rendering the decider saw only raw jsonb and
/// could re-spawn straight back into the same refusal.</para>
/// </summary>
public sealed record SupervisorBudgetBlock
{
    /// <summary>The subtask ids the refused wave would have staged — every one of them was withheld.</summary>
    public required IReadOnlyList<string> SubtaskIds { get; init; }

    /// <summary>The admission ledger's own reason for the refusal; null when the outcome recorded none.</summary>
    public string? Reason { get; init; }

    /// <summary>The USD already committed against the run's cap at refusal time; null when the outcome recorded none.</summary>
    public decimal? CommittedUsd { get; init; }

    /// <summary>The run's USD cap the admission measured against; null when the outcome recorded none.</summary>
    public decimal? CapUsd { get; init; }
}
