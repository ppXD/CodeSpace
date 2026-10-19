using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// What ending a credential does to everything that leans on it. A Disconnect, an identity unlink, and the owner leaving
/// the team each end one, and each used to clean up a different subset: an unlink left the repositories bound through the
/// token Active on a blanked payload, and a departure ended nothing at all. One cascade now serves all three.
/// </summary>
public interface ICredentialRevocationService
{
    /// <summary>
    /// Repositories bound through the credential turn Error with a "needs a new credential" message, and the act-as
    /// identities linked to it are soft-deleted. Idempotent. Tracked changes only — the caller's unit of work saves them.
    /// Returns how many working repositories it broke.
    /// </summary>
    Task<int> CascadeAsync(Guid credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// End the credential on this side — blank its token, mark it Revoked and soft-deleted — then cascade. The provider
    /// is not asked. Tracked changes only. Returns how many working repositories it broke.
    /// </summary>
    Task<int> RetireAsync(Credential credential, CancellationToken cancellationToken);

    /// <summary>
    /// Retire every Personal credential <paramref name="userId"/> lent <paramref name="teamId"/> — the ones they own, and
    /// the owner-less ones they added before a token's owner defaulted to whoever added it — with the act-as identities
    /// linked to them, unless they still have standing there: what a person lent a team stops being the team's when they
    /// leave it, and an instance Admin, who stands in every team, has not left it. Their credentials in any other team are
    /// untouched. Reads standing from the database, so the departure's own membership delete must be saved first. Tracked
    /// changes only.
    /// </summary>
    Task RetireDepartedInTeamAsync(Guid teamId, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Retire up to <paramref name="batchSize"/> personal tokens whose person no longer has standing in the team they were
    /// lent to — no membership there and not an instance Admin — left behind by departures from before departures retired
    /// tokens, by an older pod, or by an instance Admin losing the role; each exactly as a departure retires it and saved
    /// on its own. A token that cannot be attributed to an account is never one. Returns how many were retired. A token that
    /// fails is skipped and logged and the pass goes on past it — however many fail, they cannot crowd a healthy one out — and
    /// one that has failed several passes in a row is set aside for a while (<see cref="SweepPass{TCandidate}"/>). Each token is
    /// retired under its refresh lock, judged departed again once it is held: a token a refresh or a re-authorization is
    /// writing is skipped and met on the next pass, so a retirement never blanks what such a write lands.
    /// </summary>
    Task<int> RetireDepartedAsync(int batchSize, CancellationToken cancellationToken);
}
