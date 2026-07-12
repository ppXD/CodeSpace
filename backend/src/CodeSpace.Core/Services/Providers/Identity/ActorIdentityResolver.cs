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

    public async Task<UserProviderIdentity?> ResolveAsync(Guid actorUserId, Guid providerInstanceId, CancellationToken cancellationToken) =>
        await _db.UserProviderIdentity
            .Include(i => i.Credential)
            .SingleOrDefaultAsync(i =>
                i.UserId == actorUserId &&
                i.ProviderInstanceId == providerInstanceId &&
                i.DeletedDate == null &&
                i.Credential.DeletedDate == null &&
                i.Credential.Status == CredentialStatus.Active, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActAsCandidateSummary>> ListCandidatesAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        // The repo's provider instance, under the team's tenancy (belt-and-suspenders with IRequireRepositoryAccess).
        var providerInstanceId = await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null)
            .Select(r => (Guid?)r.ProviderInstanceId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (providerInstanceId == null) return Array.Empty<ActAsCandidateSummary>();

        // Team users (members + owner) — only they may be offered as an author.
        var teamUserIds = await _db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ownerId = await _db.Team.AsNoTracking()
            .Where(t => t.Id == teamId && t.DeletedDate == null)
            .Select(t => (Guid?)t.OwnerUserId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (ownerId is { } oid) teamUserIds.Add(oid);
        if (teamUserIds.Count == 0) return Array.Empty<ActAsCandidateSummary>();

        // Every teammate with a LIVE, usable identity on this provider instance — the EXACT predicate
        // ResolveAsync uses, so every candidate is guaranteed to resolve (no throw) at write time.
        var identities = await _db.UserProviderIdentity.AsNoTracking()
            .Where(i => i.ProviderInstanceId == providerInstanceId &&
                        i.DeletedDate == null &&
                        i.Credential.DeletedDate == null &&
                        i.Credential.Status == CredentialStatus.Active &&
                        teamUserIds.Contains(i.UserId))
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
}
