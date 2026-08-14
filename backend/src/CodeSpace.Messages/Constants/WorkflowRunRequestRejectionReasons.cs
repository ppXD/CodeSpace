namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical <c>error</c> values for <c>workflow_run_request</c> rows whose
/// <c>status = Rejected</c>. Operators reading the audit view filter on these strings to
/// answer "why did this webhook / source not fire". Pinned by
/// <c>WorkflowRunRequestRejectionReasonsTests</c>.
///
/// <para>The error column also carries free-text detail (exception message, verifier
/// diagnostic, etc.); these constants are the discriminator + the free text is appended.
/// Example: <c>"signature_invalid: HMAC-SHA256 mismatch"</c>.</para>
/// </summary>
public static class WorkflowRunRequestRejectionReasons
{
    /// <summary>Webhook signature verification failed. Common causes: wrong secret, replay attack, body tampered in transit.</summary>
    public const string SignatureInvalid = "signature_invalid";

    /// <summary>The webhook is configured as inactive in CodeSpace (operator disabled it). Provider still delivering.</summary>
    public const string WebhookInactive = "webhook_inactive";

    /// <summary>Provider payload couldn't be mapped to a tracked event type (e.g. a "deployment" event for a repo subscribed only to PRs).</summary>
    public const string EventNotMapped = "event_not_mapped";

    /// <summary>Signature passed but the body couldn't be parsed into the expected shape (non-JSON, or missing/mistyped fields the normalizer requires). We respond 200 + audit so the provider doesn't retry-storm / auto-disable the webhook.</summary>
    public const string MalformedPayload = "malformed_payload";

    /// <summary>No <c>workflow_activation</c> row matched the normalised event. Workflow exists but doesn't subscribe to this event shape OR the filter (repository_id, etc.) excludes it.</summary>
    public const string NoMatchingActivation = "no_matching_activation";

    /// <summary>
    /// A connection-scoped (group / organization) hook delivered an event for a repository nobody
    /// bound. The ordinary case for a group hook rather than a fault — it covers every project under
    /// the owner and we asked for some of them — so it is recorded at most once per repository per
    /// day instead of once per delivery.
    /// </summary>
    public const string RepositoryNotBound = "repository_not_bound";

    /// <summary>
    /// The hook is still switched on but the connection has moved off it — a scope switch or a
    /// teardown retired it, and the provider is delivering to a hook we asked to have deleted.
    /// Distinct from <see cref="WebhookInactive"/>, which is an operator turning a live hook off.
    /// </summary>
    public const string WebhookRetired = "webhook_retired";
}
