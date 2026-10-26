using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;
using System.Text.Json;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// An anonymous external trigger delivery: the per-node key is the only credential, not a user or team
/// session, so this declares NO authorization marker — proof is the key digest checked against the
/// trigger row. Marking it would make the pipeline reject a caller that is exactly who the endpoint
/// means to admit.
/// </summary>
public sealed record ReceiveExternalTriggerCommand : ICommand<ExternalTriggerDeliveryOutcome>
{
    public required Guid TriggerId { get; init; }

    /// <summary>The key as presented, already normalized from Bearer / <c>X-Api-Key</c>. Null when none was sent.</summary>
    public required string? PresentedKey { get; init; }

    /// <summary>Null when the caller sent no <c>Idempotency-Key</c>, or sent only whitespace — deduplication is then the caller's business.</summary>
    public required string? IdempotencyKey { get; init; }

    /// <summary>True when a key WAS presented but is malformed; the ingress answers 400 without staging anything.</summary>
    public required bool IdempotencyRejected { get; init; }

    /// <summary>The delivery body, already bounded by the endpoint's size limit.</summary>
    public required JsonElement Body { get; init; }
}
