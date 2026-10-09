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

    /// <summary>
    /// One activation matched the delivery but its workflow's completion opt-in refused to launch — <c>enforced</c>
    /// on a graph whose operating mode is not Enforceable, or an unreadable value — so that activation started
    /// nothing while every other one matching the same delivery still did. Fixed by editing the workflow, never by
    /// redelivering.
    /// </summary>
    public const string CompletionAdmissionRefused = "completion_admission_refused";

    /// <summary>
    /// A connection-scoped (group / organization) hook delivered an event for a repository that IS bound, but under an
    /// owner path the hook does not cover. Either the repository or its owner moved at the provider and the stored paths
    /// no longer agree — opening the repository re-syncs its placement — or the body names a repository this hook's
    /// secret may not speak for. Distinct from <see cref="RepositoryNotBound"/>, whose remedy is "bind it", and recorded
    /// against the repository so its own Webhook tab says why its deliveries are refused.
    /// </summary>
    public const string RepositoryOutsideHookOwner = "repository_outside_hook_owner";

    /// <summary>
    /// A correctly signed delivery from a provider that stamps every delivery with an id (GitHub's <c>X-GitHub-Delivery</c>)
    /// arrived without one. A real provider never sends that; a tool replaying a captured body does, because without an
    /// id every post of the same body would read as a new delivery.
    /// </summary>
    public const string DeliveryIdMissing = "delivery_id_missing";

    /// <summary>
    /// This hook already accepted the identical signed body under a different delivery id. The signature covers the body
    /// only, so a captured body stays valid for as long as the secret does; a fresh id on an old body is a replay, not a
    /// new event. A provider redelivery keeps its id and is not refused.
    /// </summary>
    public const string DeliveryReplayed = "delivery_replayed";

    /// <summary>
    /// A pull-request trigger that admits only members — explicitly, or by default on a public or internal repository —
    /// saw a PR whose author holds no role there, or new commits pushed to a fork head by someone other than the author
    /// who holds none. Started nothing for that activation; every other matching activation still ran.
    /// </summary>
    public const string AuthorNotMember = "author_not_member";

    /// <summary>
    /// The same activation already started a run for this pull request at the same head commit moments ago. Events for
    /// one head within the debounce window start one run, not one per event, so closing and reopening a PR cannot be used
    /// to start runs on demand; a push moves the head, so new commits are never held back.
    /// </summary>
    public const string PullRequestDebounced = "pull_request_debounced";
}
