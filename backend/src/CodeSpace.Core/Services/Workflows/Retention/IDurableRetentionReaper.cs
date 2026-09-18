using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>
/// Runs one BOUNDED retention sweep over every registered cursor. Bounded means three separate things, all of them
/// load-bearing: each cursor claims at most <c>BatchSize</c> records per sweep; a record is settled by its own
/// conditional write rather than a transaction spanning the batch; and a row that fails is logged and skipped, so one
/// unreachable destination cannot stop the rest of the sweep.
/// </summary>
public interface IDurableRetentionReaper : IScopedDependency
{
    Task<DurableRetentionSweepSummary> SweepAsync(CancellationToken cancellationToken);
}
