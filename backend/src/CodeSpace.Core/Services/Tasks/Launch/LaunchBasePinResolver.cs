using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Default <see cref="ILaunchBasePinResolver"/>: per eligible repo, resolve the clone request through the SAME
/// <see cref="IAgentWorkspaceResolver"/> the run's clone uses (same URL, same credential, same ref defaulting), then
/// read the tip over the SAME git transport (<see cref="IRemoteTipResolver"/>) — so the pin can never skew from what
/// the clone would have fetched. Hard refs come from the launch's own authoring (the operator's BaseBranch pin for
/// the primary; each related spec's authored ref); a repo riding a SESSION-soft ref is left unpinned by design.
/// </summary>
public sealed class LaunchBasePinResolver : ILaunchBasePinResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IAgentWorkspaceResolver _workspaces;
    private readonly IRemoteTipResolver _tips;

    public LaunchBasePinResolver(CodeSpaceDbContext db, IAgentWorkspaceResolver workspaces, IRemoteTipResolver tips)
    {
        _db = db;
        _workspaces = workspaces;
        _tips = tips;
    }

    public async Task<IReadOnlyDictionary<Guid, string>?> ResolveVectorAsync(Guid teamId, TaskLaunchSeed seed, ResolvedAgentProfile profile, IReadOnlyDictionary<Guid, string> sessionBaseRefs, CancellationToken cancellationToken)
    {
        var scope = CollectScope(seed, profile, sessionBaseRefs);

        if (scope.Count == 0) return null;

        var cloneable = await LoadCloneableRepoIdsAsync(teamId, scope.Keys, cancellationToken).ConfigureAwait(false);

        var vector = new Dictionary<Guid, string>();

        foreach (var (repositoryId, hardRef) in scope)
        {
            if (!cloneable.Contains(repositoryId)) continue;   // no clone URL ⇒ nothing will ever clone it ⇒ unpinned

            var request = await _workspaces.ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken, hardRef).ConfigureAwait(false);

            if (request is null) continue;

            // refRequired only for an AUTHORED ref (the operator's BaseBranch / a related spec's Ref) — its absence
            // is an authoring error worth failing the launch for. An implicit recorded default branch that the
            // remote doesn't have (an empty just-created repo, a stale record) launches unpinned instead.
            if (await _tips.ResolveTipShaAsync(request, refRequired: hardRef is not null, cancellationToken).ConfigureAwait(false) is { } sha) vector[repositoryId] = sha;
        }

        return vector.Count > 0 ? vector : null;
    }

    /// <summary>The (repositoryId → hard authored ref, null = default branch) pairs to pin: the primary (the operator's BaseBranch pin) + each related repo (its authored ref). A repo riding a SESSION-soft ref is excluded — its branch-or-default disjunction cannot be one commit. Internal static so the eligibility policy is unit-pinned directly.</summary>
    internal static IReadOnlyDictionary<Guid, string?> CollectScope(TaskLaunchSeed seed, ResolvedAgentProfile profile, IReadOnlyDictionary<Guid, string> sessionBaseRefs)
    {
        var scope = new Dictionary<Guid, string?>();

        if ((profile.RepositoryId ?? seed.RepositoryId) is { } primaryId && !sessionBaseRefs.ContainsKey(primaryId))
            scope[primaryId] = NullIfBlank(seed.BaseBranch);

        foreach (var related in profile.RelatedRepositories ?? Array.Empty<Messages.Agents.WorkspaceRepositorySpec>())
        {
            if (sessionBaseRefs.ContainsKey(related.RepositoryId) || scope.ContainsKey(related.RepositoryId)) continue;

            scope[related.RepositoryId] = NullIfBlank(related.Ref);
        }

        return scope;
    }

    /// <summary>The subset of <paramref name="repositoryIds"/> with a non-blank HTTPS clone URL, team-scoped — the others are unpinnable by construction (the workspace resolver fails loud on them, but a launch that never clones them must not).</summary>
    private async Task<HashSet<Guid>> LoadCloneableRepoIdsAsync(Guid teamId, IEnumerable<Guid> repositoryIds, CancellationToken cancellationToken)
    {
        var ids = repositoryIds.ToList();

        var cloneable = await _db.Repository.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == teamId && r.DeletedDate == null && r.CloneUrlHttps != null && r.CloneUrlHttps != "")
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return cloneable.ToHashSet();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
