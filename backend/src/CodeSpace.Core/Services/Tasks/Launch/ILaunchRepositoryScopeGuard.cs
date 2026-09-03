using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// The ONE fail-closed tenancy gate over the repositories a launch touches. Extracted so the launch path and the
/// read-only route PREVIEW enforce byte-identically the same check from one place — two copies of a tenancy guard
/// is the kind of drift that silently opens a cross-team read on whichever copy is forgotten.
/// </summary>
public interface ILaunchRepositoryScopeGuard
{
    /// <summary>Throws <see cref="KeyNotFoundException"/> unless EVERY repository the request touches (the primary — the seed's, else the request's — plus each related repo) is a live repo of <c>request.TeamId</c>. No repo named ⇒ a no-op (an analysis-only task is valid).</summary>
    Task EnsureInTeamAsync(TaskLaunchSeed seed, TaskLaunchRequest request, CancellationToken cancellationToken);
}
