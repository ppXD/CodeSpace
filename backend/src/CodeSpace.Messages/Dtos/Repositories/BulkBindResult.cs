namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// Result envelope for <c>BindRepositoriesBulkCommand</c> /
/// <c>IRepositoryBindingService.BindManyAsync</c>.
///
/// <para><b>All-or-nothing contract</b>: under the current bulk-bind command, every reachable
/// response has <see cref="FailureCount"/> == 0 and every <see cref="BulkBindItemResult.Error"/>
/// == null. The <c>TransactionalBehavior</c> pipeline rolls back the whole transaction on the
/// first thrown bind, so a partial-success response can never reach the caller — the caller
/// gets either the fully-bound batch or an exception, never a half-state object.</para>
///
/// <para>The <see cref="FailureCount"/> + <see cref="BulkBindItemResult.Error"/> fields exist
/// for forward compatibility: a future partial-success endpoint (e.g.
/// <c>BindRepositoriesBestEffortCommand</c>) can reuse this shape without breaking the wire
/// contract. Until that ships, treat both as constants — operators relying on them to detect
/// failures will silently miss every error.</para>
/// </summary>
public sealed record BulkBindResult
{
    public required IReadOnlyList<BulkBindItemResult> Items { get; init; }
    public required int SuccessCount { get; init; }

    /// <summary>Always 0 under <c>BindRepositoriesBulkCommand</c>; see the record-level remark.</summary>
    public required int FailureCount { get; init; }
}

public sealed record BulkBindItemResult
{
    public required string ProjectIdentifier { get; init; }
    public Guid? RepositoryId { get; init; }

    /// <summary>Always null under <c>BindRepositoriesBulkCommand</c>; see <see cref="BulkBindResult"/>.</summary>
    public string? Error { get; init; }
}
