using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Outbox;

namespace CodeSpace.IntegrationTests.Outbox;

/// <summary>
/// Test-only outbox handler that always succeeds without side effects.
/// Lets the lease/claim integration tests exercise the dispatcher's concurrency semantics
/// without dragging in webhook registration or workflow engine machinery.
///
/// MessageType <c>"NoOp"</c> is intentionally not a production-recognised constant — the
/// production message-types live in <see cref="OutboxMessageTypes"/> and stay there. This
/// handler is registered only via the test assembly's IOutboxMessageHandler scan.
/// </summary>
public sealed class NoOpOutboxHandler : IOutboxMessageHandler
{
    public string MessageType => "NoOp";

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
