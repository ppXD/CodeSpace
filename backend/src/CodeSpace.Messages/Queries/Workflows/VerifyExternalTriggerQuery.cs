using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>Anonymous digest check before body reading, outside the command transaction pipeline.</summary>
public sealed record VerifyExternalTriggerQuery : IQuery<ExternalTriggerVerificationOutcome>
{
    public required Guid TriggerId { get; init; }
    public required string? PresentedKey { get; init; }
}

public sealed record ExternalTriggerVerificationOutcome(ExternalTriggerDeliveryKind Kind);
