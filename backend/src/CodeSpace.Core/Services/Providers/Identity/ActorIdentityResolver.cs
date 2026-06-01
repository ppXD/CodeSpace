using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
}
