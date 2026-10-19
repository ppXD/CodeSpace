using System.Linq.Expressions;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Providers.Identity;

public sealed class ActorIdentityResolver : IActorIdentityResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ActorIdentityResolver(CodeSpaceDbContext db)
    {
        _db = db;
    }

    /// <summary>A live identity on a live Active credential, in the live team that owns the provider instance.</summary>
    private static readonly Expression<Func<UserProviderIdentity, bool>> Usable = i =>
        i.DeletedDate == null &&
        i.Credential.DeletedDate == null &&
        i.Credential.Status == CredentialStatus.Active &&
        i.ProviderInstance.Team.DeletedDate == null;

    public async Task<UserProviderIdentity?> ResolveAsync(Guid actorUserId, Guid providerInstanceId, CancellationToken cancellationToken) =>
        await MayAct(_db.UserProviderIdentity.Include(i => i.Credential))
            .Where(i => i.UserId == actorUserId && i.ProviderInstanceId == providerInstanceId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActAsCandidateSummary>> ListCandidatesAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        // The repo's provider instance, under the team's tenancy (belt-and-suspenders with IRequireRepositoryAccess).
        var providerInstanceId = await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null)
            .Select(r => (Guid?)r.ProviderInstanceId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (providerInstanceId == null) return Array.Empty<ActAsCandidateSummary>();

        return await ListCandidatesOnInstanceAsync(providerInstanceId.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActAsCandidateSummary>> ListCandidatesOnInstanceAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        // Every teammate who may act on this provider instance — the EXACT predicate ResolveAsync uses, so every
        // candidate is guaranteed to resolve (no throw) at write time.
        var identities = await MayAct(_db.UserProviderIdentity.AsNoTracking())
            .Where(i => i.ProviderInstanceId == providerInstanceId)
            .Select(i => new { i.UserId, i.ProviderUsername, i.ProviderUserId, i.AvatarUrl })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (identities.Count == 0) return Array.Empty<ActAsCandidateSummary>();

        var candidateIds = identities.Select(i => i.UserId).ToList();
        var users = await _db.User.AsNoTracking()
            .Where(u => candidateIds.Contains(u.Id) && u.DeletedDate == null && !u.IsBot)
            .Select(u => new { u.Id, u.Name, u.Email })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var userById = users.ToDictionary(u => u.Id);

        return identities
            .Where(i => userById.ContainsKey(i.UserId))
            .Select(i => new ActAsCandidateSummary
            {
                UserId = i.UserId,
                Name = userById[i.UserId].Name,
                Email = userById[i.UserId].Email,
                ProviderUsername = i.ProviderUsername,
                ProviderUserId = i.ProviderUserId,
                AvatarUrl = i.AvatarUrl,
            })
            .OrderBy(c => c.Name)
            .ToList();
    }

    /// <summary>
    /// Whether an identity may act, and the ONLY place that says so: the picker and the write both filter through it, so
    /// every candidate the picker offers resolves and nobody the picker hides can be written through. A usable identity,
    /// and a user who still has standing (<see cref="TeamStanding"/>) in the team that owns the provider instance — a
    /// membership row there, or the instance Admin role, which stands in every team.
    ///
    /// <para>The standing half, and the live team, are what the write used to lack; only the picker asked them. Leaving or
    /// being removed deletes the membership row and nothing else could see it here, so a workflow any Member authored could
    /// name a departed person in <c>actAsUserId</c> and write through their personal token. The instance is team-scoped,
    /// so its team is the one whose standing counts — no caller has to pass it.</para>
    /// </summary>
    private IQueryable<UserProviderIdentity> MayAct(IQueryable<UserProviderIdentity> identities) =>
        identities.Where(Usable).Where(TeamStanding.Of<UserProviderIdentity>(_db, i => i.ProviderInstance.TeamId, i => i.UserId));
}
