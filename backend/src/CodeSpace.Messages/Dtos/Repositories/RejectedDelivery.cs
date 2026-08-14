namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// One delivery that arrived and was refused — a <c>workflow_run_request</c> row with
/// <c>status = Rejected</c>, as the operator reads it.
///
/// <para>This is the other half of "why isn't my webhook working". The registration timeline
/// (<see cref="RepositoryWebhookDetail.AttemptTimeline"/>) answers the case where the hook was never
/// created. This answers the case where it was: the provider is sending, and CodeSpace is throwing
/// each one away. Nothing on the page said so before, so a working registration and a hook whose
/// every delivery fails its signature check read identically.</para>
///
/// <para>The reasons are not one severity. A signature mismatch is broken; an unsubscribed event
/// type is noise; "no workflow was listening" is the system working exactly as configured. The row
/// carries <see cref="Reason"/> as its own field rather than one prose sentence so the reader can
/// tell those apart at a glance instead of being handed five refusals in one alarmed tone.</para>
/// </summary>
public sealed record RejectedDelivery
{
    public required Guid Id { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Null when nothing had resolved a repository by the time the delivery was refused. Kept in the
    /// answer rather than filtered out: a delivery that arrived and was discarded is the thing being
    /// looked for, and hiding the ones we cannot place would hide them at the worst moment.
    /// </summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>
    /// One of <see cref="Constants.WorkflowRunRequestRejectionReasons"/> — the discriminator the
    /// reader's sentence is chosen from. Empty when the stored error carries no recognisable reason
    /// prefix, in which case <see cref="Detail"/> is the whole of it.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>What the failing site had to say, with the reason prefix removed. Free text — never the sole basis for the copy shown.</summary>
    public required string Detail { get; init; }

    /// <summary>The provider's own delivery id, so a refusal here can be matched against the delivery in the provider's UI. Null when it was never read — signature failures reject before the body is trusted.</summary>
    public string? ExternalEventId { get; init; }

    /// <summary>Headers as a JSON object with secret-bearing values already stripped at capture. Passed through as stored so the client renders the record rather than a re-serialization of it.</summary>
    public string? RawHeadersRedactedJson { get; init; }

    /// <summary>The verifier's diagnostic — which algorithm was tried, which key id. Populated for signature failures, null otherwise.</summary>
    public string? VerificationResultJson { get; init; }
}
