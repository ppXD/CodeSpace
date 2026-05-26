using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Outbox;

/// <summary>
/// Drain the outbox by N messages. Dispatched by the recurring job; the handler delegates
/// to <c>IOutboxDispatcher.DrainOnceAsync</c>.
///
/// <para>NOT tenant-scoped — this is a system-wide operation. Tenancy is enforced inside
/// each outbox-message handler (RegisterWebhookOutboxHandler etc.) by reading aggregate_id
/// and looking up the resource it points to.</para>
/// </summary>
public sealed record DrainOutboxCommand : ICommand<DrainOutboxResponse>
{
    /// <summary>Max messages to claim per drain sweep. Default 50.</summary>
    public int BatchSize { get; init; } = 50;
}

public sealed record DrainOutboxResponse
{
    /// <summary>How many messages were actually claimed + processed this sweep (0 when the queue is empty).</summary>
    public required int Processed { get; init; }
}
