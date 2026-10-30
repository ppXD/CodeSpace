namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// What an anonymous external trigger delivery produced. Mirrors the ingress's own outcome so the
/// controller can map it to a status without the outcome type having to cross from <c>CodeSpace.Core</c>.
/// A staged run is <see cref="ExternalTriggerDeliveryKind.Accepted"/>; a deterministic admission
/// refusal is <see cref="ExternalTriggerDeliveryKind.Forbidden"/> or <see cref="ExternalTriggerDeliveryKind.Unprocessable"/>.
/// </summary>
public sealed record ExternalTriggerDeliveryOutcome
{
    public required ExternalTriggerDeliveryKind Kind { get; init; }

    /// <summary>The run this call staged or deduplicated onto; <see cref="Guid.Empty"/> when none was.</summary>
    public required Guid RunId { get; init; }

    /// <summary>Non-empty only on <see cref="ExternalTriggerDeliveryKind.InvalidPayload"/> — the ingress's own messages.</summary>
    public required IReadOnlyList<string> Errors { get; init; }
    public string? Code { get; init; }
}

public enum ExternalTriggerDeliveryKind
{
    Accepted,
    Deduplicated,
    NotFound,
    Unauthorized,
    Forbidden,
    Unprocessable,
    InvalidPayload,
    RateLimited,
}
