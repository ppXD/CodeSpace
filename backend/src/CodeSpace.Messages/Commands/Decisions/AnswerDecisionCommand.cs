using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Decisions;

/// <summary>
/// Answer a pending decision FROM THE QUEUE — the generic write that completes the cross-grain "Needs decision" queue
/// (Decision substrate D3b). One command answers EITHER grain: the service routes by the decision's id to the right
/// durable resume mechanism (an <c>agent.code</c> <c>decision.request</c> unblocks its mid-run call; a
/// <c>flow.decision</c> node resumes its run from the exact node), so the caller never needs to know which grain it is.
///
/// <para>Tenancy (<see cref="IRequireTeamMembership"/>): the team is resolved from <c>ICurrentTeam</c> and the answerer
/// from <c>ICurrentUser</c> in the handler — NEVER this body (fail-closed). A decision outside the team is a clean
/// not-found, never a cross-team leak. <see cref="DecisionId"/> is bound from the route (Rule 17), not the body.</para>
/// </summary>
public sealed record AnswerDecisionCommand : ICommand<AnswerDecisionResult>, IRequireTeamMembership
{
    /// <summary>The decision to answer — the queue item id (the ledger row id for an agent decision, the workflow-wait id for a node decision). Bound from the route.</summary>
    public Guid DecisionId { get; init; }

    /// <summary>The chosen option id(s) — one for confirm / choose_one, many for choose_many. Empty for a pure free-text answer.</summary>
    public IReadOnlyList<string> SelectedOptions { get; init; } = Array.Empty<string>();

    /// <summary>A free-text answer (for free_text) or an optional note alongside a choice.</summary>
    public string? FreeText { get; init; }
}
