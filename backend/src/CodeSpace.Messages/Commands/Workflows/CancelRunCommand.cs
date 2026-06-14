using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Operator-triggered cancel of a workflow run: aborts the run and tears down its branch agents +
/// staged children. CAS the run from any non-terminal state (Pending/Enqueued/Running/Suspended) →
/// Cancelled; the primary target is a suspended <c>flow.map</c> fan-out parked on K branch agent runs.
///
/// <para>Tenancy: the run must belong to the caller's current team (404 conflated with not-yours) —
/// fail-closed, never a silent success.</para>
///
/// <para>Returns a <c>CancelRunOutcome</c>: <c>Cancelled=true</c> + the count of branch agent runs the
/// kill-wave aborted on a successful flip, or <c>Cancelled=false</c> + the existing terminal status on
/// an already-terminal idempotent no-op.</para>
/// </summary>
public sealed record CancelRunCommand : ICommand<CancelRunOutcome?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
}
