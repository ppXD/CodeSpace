namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One FAILED provider-side registration attempt for a <see cref="ConnectionWebhook"/> — the
/// connection-scoped twin of <see cref="RepositoryWebhookAttempt"/>, and identical to it column
/// for column so both modes' diagnostics read the same.
///
/// <para>This is where a GitLab Free instance's refusal of the group-hooks endpoint is recorded:
/// the status it answered with and the body it answered with, in the same shape as any other
/// refusal, so the operator reads GitLab's own words rather than our paraphrase of them.</para>
///
/// <para>Every field here is already masked. Nothing in this row is a working secret.</para>
/// </summary>
public class ConnectionWebhookAttempt : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid ConnectionWebhookId { get; set; }

    /// <summary>Value of <c>connection_webhook.attempts</c> after this attempt was counted, so the timeline lines up with the state machine.</summary>
    public int AttemptNumber { get; set; }

    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The same string this attempt wrote to <c>last_error</c>.</summary>
    public string Error { get; set; } = default!;

    /// <summary>HTTP status the provider answered with. NULL means the call never got an answer — a timeout, a DNS failure, a refused connection.</summary>
    public int? StatusCode { get; set; }

    /// <summary>The provider's response body, truncated at capture time.</summary>
    public string? ResponseBody { get; set; }

    public string? RequestMethod { get; set; }
    public string? RequestUrl { get; set; }

    /// <summary>The body we sent, with secret-bearing fields masked BEFORE the row was written.</summary>
    public string? RequestBody { get; set; }

    /// <summary>Headers we sent as JSON, with credential-carrying values masked. Shows WHICH auth scheme was used, never the token.</summary>
    public string? RequestHeadersJson { get; set; }

    public ConnectionWebhook Webhook { get; set; } = default!;
}
