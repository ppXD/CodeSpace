using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Outbox;

/// <summary>
/// Handles one MessageType. The dispatcher resolves the handler via the MessageType discriminator
/// and invokes HandleAsync inside a fresh DI scope (its own DbContext, transaction boundary).
/// </summary>
public interface IOutboxMessageHandler
{
    /// <summary>Stable string discriminator matching OutboxMessage.MessageType. Renaming breaks every in-flight message in production — bump with a migration.</summary>
    string MessageType { get; }

    /// <summary>Performs the external side effect and any local DB writes that must succeed together. Throwing here triggers the dispatcher's retry / dead-letter policy.</summary>
    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}
