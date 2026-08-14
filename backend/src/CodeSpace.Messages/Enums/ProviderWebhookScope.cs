namespace CodeSpace.Messages.Enums;

/// <summary>
/// Where a connection registers its webhooks. A connection is in exactly one mode at a time —
/// the transition retires the outgoing mode's hooks before registering the incoming one, because
/// two modes delivering the same event is worse than either mode alone.
/// </summary>
public enum ProviderWebhookScope
{
    /// <summary>
    /// One hook per bound repository, registered at bind. The default, and what every connection
    /// that predates the setting keeps.
    /// </summary>
    Repository,

    /// <summary>
    /// One GitLab group / GitHub organization hook per owner path, covering every repository
    /// underneath it. Binding a repository registers no per-repository hook; the repository a
    /// delivery belongs to is read out of the payload instead of the callback URL.
    /// </summary>
    Connection
}
