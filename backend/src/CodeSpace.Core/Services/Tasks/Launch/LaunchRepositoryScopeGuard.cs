using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Default <see cref="ILaunchRepositoryScopeGuard"/> — one query over the whole repo set the request touches.
/// A foreign / missing / deleted repo is an INDISTINGUISHABLE not-found (so a foreign repo never leaks its
/// existence), and rejecting ANY one fails the whole call fail-closed — on the launch path that happens BEFORE
/// the session opens (no orphan), and on the preview path before any routing runs.
/// </summary>
public sealed class LaunchRepositoryScopeGuard : ILaunchRepositoryScopeGuard, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public LaunchRepositoryScopeGuard(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task EnsureInTeamAsync(TaskLaunchSeed seed, TaskLaunchRequest request, CancellationToken cancellationToken)
    {
        var ids = ScopeIds(seed, request);

        if (ids.Count == 0) return;

        var inTeam = await _db.Repository.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == request.TeamId && r.DeletedDate == null)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (inTeam.Count != ids.Count)
            throw new KeyNotFoundException($"Repository {string.Join(", ", ids.Except(inTeam))} not found or not accessible.");
    }

    /// <summary>Every repo the request touches, distinct: the primary (the seed's, else the request's) plus each related (multi-repo) repo. Empty when none was named.</summary>
    private static HashSet<Guid> ScopeIds(TaskLaunchSeed seed, TaskLaunchRequest request)
    {
        var ids = new HashSet<Guid>();

        if ((seed.RepositoryId ?? request.RepositoryId) is { } primary) ids.Add(primary);
        if (request.RelatedRepositories is { } related) foreach (var r in related) ids.Add(r.RepositoryId);

        return ids;
    }
}
