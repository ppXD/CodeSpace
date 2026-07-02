using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Plans;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Answer a run's pending plan-confirmation card (triad S3): <c>Approve = true</c> releases execution (the
/// supervisor proceeds to spawn the confirmed plan); <c>Approve = false</c> carries the operator's revision
/// <see cref="Feedback"/> (required in that case), which the supervisor folds into a REVISED plan version
/// that re-gates. Rides the same durable Action wait as the conversation card's Answer button — first answer
/// wins. Team-scoped (<see cref="IRequireTeamMembership"/>); a foreign / absent run or a run with no pending
/// confirmation resolves to <c>null</c> → the controller 404-conflates (no existence leak).
/// </summary>
public sealed record ConfirmRunPlanCommand : ICommand<WorkPlanConfirmationOutcome?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    public bool Approve { get; init; }

    /// <summary>The operator's revision feedback — required when <see cref="Approve"/> is false; an optional trailing note when true.</summary>
    public string? Feedback { get; init; }
}
