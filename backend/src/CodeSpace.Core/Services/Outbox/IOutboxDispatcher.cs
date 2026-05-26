namespace CodeSpace.Core.Services.Outbox;

/// <summary>
/// Drains pending outbox messages. The background service calls DrainOnceAsync in a loop;
/// tests call it directly to flush after enqueueing in a bind/unbind flow.
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>Processes up to batchSize due messages. Returns the count actually processed (0 when nothing is due). Each message runs in its own DI scope — handler failures do not leak DbContext state to neighbours.</summary>
    Task<int> DrainOnceAsync(int batchSize, CancellationToken cancellationToken);
}
