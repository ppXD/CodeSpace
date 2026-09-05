using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Answer a run's NEWEST pending supervisor ask — any ask alike (a content question, a review-gate escalation where
/// 'approve' is the one-shot absolution and anything else is guidance). Rides the same durable Action wait as the
/// conversation card's Answer button — first answer wins. Team-scoped; a foreign / absent run or a run with no
/// pending ask resolves to <c>null</c> → the controller 404-conflates (no existence leak).
/// </summary>
public sealed record AnswerRunAskCommand : ICommand<SupervisorAskAnswerOutcome?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.RunsDecide;

    public Guid RunId { get; init; }

    /// <summary>The operator's answer text — required non-blank.</summary>
    public string Answer { get; init; } = "";

    /// <summary>
    /// The STRUCTURED verdict for a GATE card (C4) — one of <see cref="SupervisorAnswerDecision.All"/>. Null for a
    /// content question, which has no verdict to give. When present it is what the gate rules on; when absent the
    /// gate falls back to matching the answer text's leading approve word (the legacy contract, kept for old clients).
    /// An unrecognized value is refused at the endpoint rather than silently read as a non-approval.
    /// </summary>
    public string? Decision { get; init; }
}
