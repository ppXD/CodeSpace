using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Keeps a bound repository covered by a connection hook when its owner path moves at the provider.
///
/// <para>A group (GitLab) or organization (GitHub) hook is keyed at the provider by the owner's id, so it keeps
/// delivering after a rename or a transfer — but its row holds the owner PATH it was registered under, and ingestion
/// routes a delivery only to a repository whose stored path that hook covers. The moment CodeSpace learns a repository
/// moved is the metadata re-sync that rewrites its <c>NamespacePath</c>; this is the provisioner's "is this owner
/// covered" question asked at that moment, as a bind asks it. Split in two because the caller owns the unit of work
/// between staging and dispatch.</para>
/// </summary>
public interface IConnectionWebhookCoverageService
{
    /// <summary>
    /// When <paramref name="repository"/> (with its <c>ProviderInstance</c> loaded) sits on a connection-scoped instance
    /// and its <c>NamespacePath</c> moved away from <paramref name="previousNamespacePath"/>, make sure a hook covers the
    /// new path — staged in the caller's unit of work, so the move and its coverage commit together. Returns the id to
    /// dispatch once that unit of work has committed, or null when there is nothing to register.
    /// </summary>
    Task<Guid?> StageForMoveAsync(Repository repository, string previousNamespacePath, CancellationToken cancellationToken);

    /// <summary>Dispatch a hook <see cref="StageForMoveAsync"/> staged, once the caller's unit of work has committed. Null is a no-op.</summary>
    Task DispatchAfterCommitAsync(Guid? connectionWebhookId, CancellationToken cancellationToken);
}
