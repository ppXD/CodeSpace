using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an operation must act AS the caller's own provider identity (Model B) but the caller
/// has not linked one for the relevant provider instance. The generic enforcement seam
/// (<c>IActorCredentialProvider.RequireAsync</c>) throws this; the API layer maps it to a typed
/// <c>actor_identity_required</c> response so a single frontend interceptor can open the binding
/// modal for any feature, naming the provider to connect.
/// </summary>
public sealed class ActorIdentityRequiredException : Exception
{
    public ProviderKind ProviderKind { get; }
    public Guid ProviderInstanceId { get; }

    public ActorIdentityRequiredException(ProviderKind providerKind, Guid providerInstanceId)
        : base($"This action must be performed as your own {providerKind} identity, but you haven't linked one for this provider instance. Connect your {providerKind} account, then retry.")
    {
        ProviderKind = providerKind;
        ProviderInstanceId = providerInstanceId;
    }
}
