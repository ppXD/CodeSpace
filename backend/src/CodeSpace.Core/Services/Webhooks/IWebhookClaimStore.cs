namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// A key held for a window by one holder — how webhook ingress says "this already happened moments ago" without a read
/// two concurrent deliveries could both pass. The primary key settles the race; the loser waits for the winner's
/// transaction and then sees its claim.
/// </summary>
public interface IWebhookClaimStore
{
    /// <summary>
    /// True when <paramref name="key"/> was free, had expired, or is already held by <paramref name="holder"/> — the last
    /// so that a provider redelivering the same delivery is not mistaken for a new one. False when someone else holds it.
    /// Joins the caller's transaction, so a delivery that rolls back releases its claim.
    /// </summary>
    Task<bool> TryClaimAsync(string key, string holder, TimeSpan holdFor, CancellationToken cancellationToken);

    /// <summary>Deletes every claim whose window has ended, without waiting on one a delivery is holding. Returns how many it deleted. Called by the recurring sweep, never from a delivery.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}
