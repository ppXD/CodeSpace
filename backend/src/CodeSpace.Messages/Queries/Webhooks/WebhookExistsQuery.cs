using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Webhooks;

/// <summary>
/// Whether a webhook id names a hook at all. ANONYMOUS, like the delivery it precedes: it answers only what that
/// delivery's own 404 already tells the caller, and it is asked first so that a body posted to an id naming nothing is
/// never read.
/// </summary>
public sealed record WebhookExistsQuery : IQuery<bool>
{
    public required Guid WebhookId { get; init; }

    /// <summary>Which table the id lives in: a per-repository hook, or a group / organization (connection) hook.</summary>
    public required ProviderWebhookScope Scope { get; init; }
}
