namespace CodeSpace.Messages.Contracts;

/// <summary>
/// What one reconciliation pass of the completeness plane saw. Counts rather than rows, because the per-facet answer
/// already lives on the durable statement that names its own run — a pass reports how far it moved and nothing a
/// reader would otherwise have to trust it to have remembered.
/// </summary>
public sealed record RunDataManifestReconciliation
{
    /// <summary>Statements the pass picked up as unattributed shortfalls.</summary>
    public required int Examined { get; init; }

    /// <summary>Statements whose expectation is now indeterminate, and permanently so — a later delta is absorbed rather than folded.</summary>
    public required int Unstated { get; init; }

    /// <summary>Picked up and left exactly as found: the statement stopped qualifying before the write, or the write was contained and lost.</summary>
    public required int Unchanged { get; init; }
}
