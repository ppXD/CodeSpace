using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// One contributor to a run's phase tree — it reads ITS OWN durable substrate slice (the workflow run-node table,
/// the supervisor decision ledger, …) for a given (run, team) and emits the <see cref="RunPhase"/> rows it knows
/// how to derive. The projector fans out across EVERY registered source and merges the rows by Order — there is no
/// registry / resolve-by-key dispatch (unlike harness / projection / recipe): a source either has rows for the run
/// or contributes none (an empty list). A NEW run shape plugs in as a dropped <see cref="IScopedDependency"/> impl
/// the projector's injected <c>IEnumerable</c> picks up with ZERO projector edit (Rule 7 — narrow + additive).
///
/// <para>Scoped because every source reads scoped DB (via <c>IWorkflowService</c> / <c>ISupervisorDecisionLog</c> /
/// the scoped <c>CodeSpaceDbContext</c>). READ-ONLY — a source never writes or mutates the engine. A source that
/// throws is caught per-source by the projector (a broken source degrades to fewer phases, never a 500).</para>
/// </summary>
public interface IRunPhaseSource : IScopedDependency
{
    /// <summary>This source's stable provenance key, stamped on every <see cref="RunPhase.SourceKey"/> it emits (e.g. "node-summary", "supervisor-ledger"). Also the cross-source sort tie-break.</summary>
    string SourceKey { get; }

    /// <summary>Contribute this source's phase rows for the run. Returns an empty list when the source has nothing for this run (e.g. a non-supervisor run for the ledger source). The (run, team) is already tenancy-checked by the projector.</summary>
    Task<IReadOnlyList<RunPhase>> ContributeAsync(RunPhaseContext context, CancellationToken cancellationToken);
}
