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

    /// <summary>No <c>workflow_activation</c> row matched the normalised event. Workflow exists but doesn't subscribe to this event shape OR the filter (repository_id, etc.) excludes it.</summary>
    public const string NoMatchingActivation = "no_matching_activation";
}
