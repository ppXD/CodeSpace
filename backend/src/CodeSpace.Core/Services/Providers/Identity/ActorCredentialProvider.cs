using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Providers.Identity;

public sealed class ActorCredentialProvider : IActorCredentialProvider, IScopedDependency
{
    private readonly IActorIdentityResolver _resolver;

    public ActorCredentialProvider(IActorIdentityResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<Credential> RequireAsync(Guid actorUserId, ProviderInstance instance, CancellationToken cancellationToken)
    {
        var identity = await _resolver.ResolveAsync(actorUserId, instance.Id, cancellationToken).ConfigureAwait(false);

        if (identity == null) throw new ActorIdentityRequiredException(instance.Provider, instance.Id);

        return identity.Credential;
    }
}
