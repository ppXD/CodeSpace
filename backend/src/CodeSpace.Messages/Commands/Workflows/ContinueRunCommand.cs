using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Continue a STRANDED Suspended run on demand — the user-triggered twin of the reconciler's stranded-Suspended
/// re-dispatch, so a run parked with no pending wait keeps going NOW instead of waiting for the ≤2-min sweep.
///
/// <para>Tenancy: the run's workflow must belong to the caller's current team (404 conflated with not-yours).</para>
///
/// <para>Returns <c>true</c> if this call drove the continuation; <c>false</c> when the run is not a stranded
/// Suspended one — it is terminal / Running, or still parked on a pending wait (approval / timer / callback), which
/// resumes via <c>/resume</c> or its wait signal, not here.</para>
/// </summary>
public sealed record ContinueRunCommand : ICommand<bool>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
}
