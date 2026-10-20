namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// What an anonymous external trigger delivery produced. Mirrors the ingress's own outcome so the
/// controller can map it to a status without the outcome type having to cross from <c>CodeSpace.Core</c>:
/// <see cref="ExternalTriggerDeliveryKind.Accepted"/> is 202 even when admission refused to stage a run,
/// and only <see cref="ExternalTriggerDeliveryKind.Deduplicated"/> carries a run id the caller already has.
/// </summary>
public sealed record ExternalTriggerDeliveryOutcome
{
    public required ExternalTriggerDeliveryKind Kind { get; init; }

    /// <summary>The run this call staged or deduplicated onto; <see cref="Guid.Empty"/> when none was.</summary>
    public required Guid RunId { get; init; }

    /// <summary>Non-empty only on <see cref="ExternalTriggerDeliveryKind.InvalidPayload"/> — the ingress's own messages.</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

public enum ExternalTriggerDeliveryKind
{
    Accepted,
    Deduplicated,
    NotFound,
    Unauthorized,
    InvalidPayload,
}
