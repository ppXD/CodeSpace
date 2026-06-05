using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Replay a prior workflow run. The new run inherits the original's trigger payload,
/// release hash, workflow_version, and variable snapshot rows. The engine's replay path
/// reads plain values from snapshot (frozen) and re-resolves secrets from the current
/// variable table (rotation safety).
///
/// <para>Tenancy: the original run's workflow must belong to the caller's current team
/// (404 conflated with not-yours).</para>
///
/// <para>Returns the new <c>WorkflowRun.Id</c> for the caller to navigate to.</para>
/// </summary>
public sealed record ReplayRunCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid OriginalRunId { get; init; }
}
