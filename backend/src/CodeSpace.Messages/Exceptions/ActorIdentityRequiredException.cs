using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an operation must act AS the caller's own provider identity (Model B) but the caller
/// has not linked one for the relevant provider instance. The generic enforcement seam
/// (<c>IActorCredentialProvider.RequireAsync</c>) throws this; the API layer maps it to a typed
/// <c>actor_identity_required</c> response so a single frontend interceptor can open the binding
/// modal for any feature, naming the provider to connect.
/// </summary>
public sealed class ActorIdentityRequiredException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.ActorIdentityRequired;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["provider"] = ProviderKind.ToString(), ["providerInstanceId"] = ProviderInstanceId };

    public ProviderKind ProviderKind { get; }
    public Guid ProviderInstanceId { get; }

    /// <summary>
    /// The CodeSpace user whose identity is missing. On the synchronous 428 path this is always the caller, so the
    /// response never needs it — but a workflow node acts as a person who is NOT the one watching, and a run parked
    /// waiting for that link has to be able to NAME whom it is waiting on. Carried here so the one throw site the
    /// enforcement seam owns stays the single source of that fact.
    /// </summary>
    public Guid ActorUserId { get; }

    public ActorIdentityRequiredException(ProviderKind providerKind, Guid providerInstanceId, Guid actorUserId)
        : base($"This action must be performed as your own {providerKind} identity, but you haven't linked one for this provider instance. Connect your {providerKind} account, then retry.")
    {
        ProviderKind = providerKind;
        ProviderInstanceId = providerInstanceId;
        ActorUserId = actorUserId;
    }
}
