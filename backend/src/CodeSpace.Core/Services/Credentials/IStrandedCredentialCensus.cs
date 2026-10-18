namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// How many disconnected credentials the binding-repair sweep would move right now. A sibling of
/// <see cref="ICredentialSuccessionService"/> rather than another member of it, so reading the backlog never widens what
/// something that only repairs it has to implement.
/// </summary>
public interface IStrandedCredentialCensus
{
    /// <summary>
    /// The dead (revoked, or refused by their provider) credentials that something live is still bound to AND whose owner has since connected another — exactly
    /// the rows <see cref="ICredentialSuccessionService.RepairStrandedAsync"/> takes, counted without its batch bound. A
    /// backlog that stays above zero across passes is a sweep that is not running, or one candidate that fails every time.
    /// </summary>
    Task<int> CountAsync(CancellationToken cancellationToken);
}
