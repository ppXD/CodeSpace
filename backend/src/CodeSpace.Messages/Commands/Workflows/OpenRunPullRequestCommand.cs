using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// The Room's "Open PR" action (PR-6): opens (or, on a repeat call, reuses) a pull/merge request for a terminal
/// run's published branch(es). Tenancy: the run must belong to the caller's current team (404 conflated with
/// not-yours). Throws when the run has no published branch — a 400 via the global exception filter.
/// </summary>
public sealed record OpenRunPullRequestCommand : ICommand<RoomPullRequestResult>, IRequireTeamMembership
{
    public Guid WorkflowRunId { get; init; }
}
