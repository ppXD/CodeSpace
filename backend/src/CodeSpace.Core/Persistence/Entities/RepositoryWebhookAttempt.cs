namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One FAILED provider-side registration attempt for a <see cref="RepositoryWebhook"/> — the
/// timeline behind the state machine. <c>repository_webhook.attempts</c> says how many times we
/// tried and <c>last_error</c> says what the newest try said; neither can distinguish "403 ten
/// times" from "nine timeouts then a 403", which are different problems with different remedies.
/// One row per attempt does.
///
/// <para>Audit columns are deliberately absent, matching <see cref="AgentRunEvent"/>: this IS the
/// record, <see cref="AttemptedAt"/> is the only timestamp that means anything, and "by whom" is
/// always the registrar.</para>
///
/// <para>Every field here is already masked. Nothing in this row is a working secret.</para>
/// </summary>
public class RepositoryWebhookAttempt : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid RepositoryWebhookId { get; set; }

    /// <summary>Value of <c>repository_webhook.attempts</c> after this attempt was counted, so the timeline lines up with the state machine.</summary>
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

    public RepositoryWebhook Webhook { get; set; } = default!;
}
