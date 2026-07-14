using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P2a-2 (R): the completion protocol's durable requirement/receipt ledger. Requirements UPSERT (one row per
/// (run, kind, ref) — re-persisting the same obligation is a no-op, an amended obligation overwrites its envelope);
/// receipts APPEND exactly-once per (run, kind, ref, attempt, target-key) — a crash-replayed producer lands on the
/// same row silently. Readers get the FULL envelopes back (the jsonb is the truth); ReceiptAdmission + the
/// selectors sit between these reads and the reducer.
/// </summary>
public interface ICompletionContractStore
{
    Task UpsertRequirementsAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<RequirementEnvelope> requirements, CancellationToken cancellationToken);

    Task AppendReceiptAsync(Guid workflowRunId, Guid teamId, ReceiptEnvelope receipt, CancellationToken cancellationToken);

    Task<IReadOnlyList<RequirementEnvelope>> ListRequirementsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReceiptEnvelope>> ListReceiptsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}
