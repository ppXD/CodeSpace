using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Redaction;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.ProviderInstances;

/// <summary>
/// Which provider connections reach the same host — the one definition, so a Re-link dialog, a relink command, a connect's
/// carry-forward and the repair sweep cannot disagree about it.
///
/// <para>A team may hold several connections to one GitLab: the unique index is per OAuth application (team, provider, base URL,
/// client id), so a second application on the same host is a second connection. Their credentials are not interchangeable at the
/// wire — each is refreshed through its own application — but the repositories behind them are the same repositories, so a
/// repository bound under one may be taken over by a credential of another. Two connections are interchangeable when they are the
/// same team's, the same provider's, and their API roots (<see cref="ApiRoot"/>) are the same service
/// (<see cref="CredentialUrl.SameRoot"/>: scheme, host, port and path). Nothing else makes them so: a different host, port or path,
/// another provider, another team.</para>
/// </summary>
public static class ProviderInstanceHost
{
    /// <summary>Where provider calls go: the API URL when one is set, the base URL otherwise — the fallback every provider reads.</summary>
    public static string ApiRoot(ProviderInstance instance) => string.IsNullOrWhiteSpace(instance.ApiUrl) ? instance.BaseUrl : instance.ApiUrl;

    /// <summary>Whether the two are one connection or two connections to the same host (see the class summary). Symmetric, and true for a connection against itself.</summary>
    public static bool AreInterchangeable(ProviderInstance a, ProviderInstance b) =>
        a.Id == b.Id || (a.TeamId == b.TeamId && a.Provider == b.Provider && CredentialUrl.SameRoot(ApiRoot(a), ApiRoot(b)));

    /// <summary>Whether two connections of a team, known by id, are the same or interchangeable — false when either is not a live connection of the team.</summary>
    public static async Task<bool> AreInterchangeableAsync(CodeSpaceDbContext db, Guid teamId, Guid a, Guid b, CancellationToken cancellationToken)
    {
        if (a == b) return true;

        var instances = await LiveInstancesAsync(db.ProviderInstance.Where(p => p.TeamId == teamId && (p.Id == a || p.Id == b)), cancellationToken).ConfigureAwait(false);

        return instances.Count == 2 && AreInterchangeable(instances[0], instances[1]);
    }

    /// <summary>The ids of <paramref name="instance"/> and every live connection of its team interchangeable with it, itself first.</summary>
    public static async Task<IReadOnlyList<Guid>> InterchangeableIdsAsync(CodeSpaceDbContext db, ProviderInstance instance, CancellationToken cancellationToken)
    {
        var teamInstances = await LiveInstancesAsync(db.ProviderInstance.Where(p => p.TeamId == instance.TeamId && p.Provider == instance.Provider), cancellationToken).ConfigureAwait(false);

        return teamInstances.Where(other => other.Id != instance.Id && AreInterchangeable(instance, other)).Select(other => other.Id).Prepend(instance.Id).ToList();
    }

    /// <summary>The same as <see cref="InterchangeableIdsAsync(CodeSpaceDbContext, ProviderInstance, CancellationToken)"/> for a connection known by id; just the id when it is not a live connection.</summary>
    public static async Task<IReadOnlyList<Guid>> InterchangeableIdsAsync(CodeSpaceDbContext db, Guid providerInstanceId, CancellationToken cancellationToken)
    {
        var instance = (await LiveInstancesAsync(db.ProviderInstance.Where(p => p.Id == providerInstanceId), cancellationToken).ConfigureAwait(false)).SingleOrDefault();

        return instance is null ? new[] { providerInstanceId } : await InterchangeableIdsAsync(db, instance, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every set of two or more live connections, across teams, that are interchangeable with one another — the connections the repair sweep must look across. A connection with no sibling is in none.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<Guid>>> SiblingGroupsAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        GroupsOf(await LiveInstancesAsync(db.ProviderInstance, cancellationToken).ConfigureAwait(false));

    /// <summary>The groups of two or more among <paramref name="instances"/>; pure, so the rule is pinned without a database.</summary>
    public static IReadOnlyList<IReadOnlyList<Guid>> GroupsOf(IEnumerable<ProviderInstance> instances)
    {
        var groups = new List<List<ProviderInstance>>();

        foreach (var instance in instances.OrderBy(i => i.Id))
        {
            var home = groups.FirstOrDefault(group => AreInterchangeable(group[0], instance));

            if (home is null) groups.Add(new List<ProviderInstance> { instance });
            else home.Add(instance);
        }

        return groups.Where(group => group.Count > 1).Select(group => (IReadOnlyList<Guid>)group.Select(i => i.Id).ToList()).ToList();
    }

    /// <summary>Only what the rule reads — never the OAuth secret a full row carries.</summary>
    private static async Task<List<ProviderInstance>> LiveInstancesAsync(IQueryable<ProviderInstance> scope, CancellationToken cancellationToken) =>
        await scope.AsNoTracking().Where(p => p.DeletedDate == null)
            .Select(p => new ProviderInstance { Id = p.Id, TeamId = p.TeamId, Provider = p.Provider, BaseUrl = p.BaseUrl, ApiUrl = p.ApiUrl })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
