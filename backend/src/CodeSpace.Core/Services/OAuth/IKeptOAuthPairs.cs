namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// The pairs a process kept because it could not store them (<see cref="IOAuthTokenRecovery.StoreKeptPairAsync"/>), seen from
/// the one place that makes them obsolete: a re-authorization, which replaces a credential's token material wholesale. A pair
/// the provider rotated for the grant that was replaced must never be stored over the new authorization — a later store
/// settles a row that changed meanwhile by which pair expires later, and a new authorization is not always the later one.
/// A sibling of <see cref="IOAuthTokenRecovery"/> rather than a member: nothing that recovers a refused token has any use for it.
/// </summary>
public interface IKeptOAuthPairs
{
    /// <summary>
    /// Forgets the pair this process kept for the credential, if it kept one. Called by the re-authorization while it holds
    /// the credential's refresh locks, which is also what every use of a kept pair holds, so none is half-way through
    /// storing it. Another process's kept pair is not reachable from here; its store is decided by the row it finds.
    /// </summary>
    void Discard(Guid credentialId);
}
