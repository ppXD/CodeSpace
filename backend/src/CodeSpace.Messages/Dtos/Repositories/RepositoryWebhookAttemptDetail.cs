namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// One FAILED registration attempt as the operator reads it — a row of
/// <c>repository_webhook_attempt</c>, field for field.
///
/// <para>Nothing is withheld here because there is nothing to withhold: every column on that
/// table is masked at capture time, so the request we sent carries neither the webhook secret
/// nor the provider credential.</para>
/// </summary>
public sealed record RepositoryWebhookAttemptDetail
{
    /// <summary>Value of <c>attempts</c> after this attempt was counted, so the timeline lines up with the state machine.</summary>
    public required int AttemptNumber { get; init; }

    public required DateTimeOffset AttemptedAt { get; init; }

    public required string Error { get; init; }

    /// <summary>Null means the call never got an answer — a timeout, a DNS failure, a refused connection. That absence is itself the diagnosis.</summary>
    public int? StatusCode { get; init; }

    public string? ResponseBody { get; init; }

    public string? RequestMethod { get; init; }

    public string? RequestUrl { get; init; }

    public string? RequestBody { get; init; }

    /// <summary>Headers we sent, as a JSON object, already masked. Passed through as the stored string so the client renders the record rather than a re-serialization of it.</summary>
    public string? RequestHeadersJson { get; init; }
}
