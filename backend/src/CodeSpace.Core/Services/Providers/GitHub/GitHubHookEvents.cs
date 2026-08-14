namespace CodeSpace.Core.Services.Providers.GitHub;

/// <summary>
/// What a GitHub hook subscribes to.
///
/// <para>GitHub takes an <c>events</c> array and documents a wildcard for it: "you can use the
/// wildcard <c>*</c> to specify all events… You'll also automatically get any new events we might
/// add in the future."</para>
///
/// <para>That last clause is the reason it is used here rather than a list. There is no way to
/// re-sync an already-registered hook, so a hook enumerating today's events would need a hand visit
/// on every installation the day a capability arrives that reads a new one — and GitHub adds event
/// types regularly. The wildcard is the only form of the subscription that stays correct without
/// anyone returning to it.</para>
///
/// <para>The cost is deliveries nothing acts on. Those are collapsed to one audit row per event type
/// per day rather than accumulating like anomalies — see <c>WebhookIngestionService.AuditEventNotMappedAsync</c>.
/// GitHub's own note that narrow subscriptions "limit the number of HTTP requests to your server" is
/// the trade being made knowingly.</para>
/// </summary>
public static class GitHubHookEvents
{
    /// <summary>GitHub's documented wildcard: every event it supports now, and every one it adds later.</summary>
    public const string Wildcard = "*";

    /// <summary>The <c>events</c> array to register with, for both repository and organization hooks.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Wildcard };
}
