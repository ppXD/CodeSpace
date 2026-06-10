using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Reclaim agent workspaces orphaned by a crashed worker — the prepare→dispose lifecycle only cleans up
/// on the happy path / prepare-failure, so a dead worker leaks its clone (which may retain credentials if
/// token-strip failed). Fired by the recurring janitor job; can also be sent ad-hoc from an admin
/// endpoint / tests.
///
/// <para>NOT tenant-scoped — system-wide disk reclamation that runs without an actor context. Returns the
/// count reclaimed for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record SweepStaleAgentWorkspacesCommand : ICommand<SweepStaleAgentWorkspacesResponse>;

/// <summary>Count of orphaned workspaces the sweep reclaimed across all janitors.</summary>
public sealed record SweepStaleAgentWorkspacesResponse
{
    public required int Reclaimed { get; init; }
}
