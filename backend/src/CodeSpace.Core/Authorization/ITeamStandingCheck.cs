namespace CodeSpace.Core.Authorization;

/// <summary>
/// Asks again, mid-request, the question request-entry authorization answered once: does the caller still hold
/// standing on this team? For a request that outlives that answer — the run-ledger tail can stay open for days on a
/// run waiting for a decision — while every other door already refuses a member who was removed, a team that was
/// deleted, a session that was ended, or a token that aged out.
/// </summary>
public interface ITeamStandingCheck
{
    /// <summary>
    /// True while the bearer handler would still accept the token the caller presented. Reads only the request's
    /// principal and the clock, so a long-lived caller can ask it on every turn of its loop. It has to: expiry is the one
    /// way a session ends with nobody acting, so no write ever follows it to prompt a <see cref="HoldsAsync"/>.
    /// </summary>
    bool WithinTokenLifetime();

    /// <summary>
    /// The whole answer: <see cref="WithinTokenLifetime"/>, the caller's session is live under the stamp their token
    /// carries, and they hold the global Admin role or a membership row on the team while it is not deleted. Answers as of
    /// the instance's scope: its collaborators memoize per scope, so a long-lived caller resolves it from a fresh scope
    /// for each ask.
    /// </summary>
    Task<bool> HoldsAsync(Guid teamId, CancellationToken cancellationToken);
}
