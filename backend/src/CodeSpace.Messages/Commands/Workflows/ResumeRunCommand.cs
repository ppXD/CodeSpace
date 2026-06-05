using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Resolve a pending Approval wait on a Suspended run with a human decision and resume it.
///
/// <para>Tenancy: the run's workflow must belong to the caller's current team (404 conflated
/// with not-yours).</para>
///
/// <para>Returns <c>true</c> if the run resumed; <c>false</c> when it had no pending approval
/// wait (already resolved, not suspended, or parked on a timer / callback instead).</para>
/// </summary>
public sealed record ResumeRunCommand : ICommand<bool>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    /// <summary>The decision — approve (true) or reject (false). Surfaced as the node's <c>approved</c> output.</summary>
    public bool Approved { get; init; }

    /// <summary>Optional reviewer note, surfaced as the node's <c>comment</c> output.</summary>
    public string? Comment { get; init; }
}
