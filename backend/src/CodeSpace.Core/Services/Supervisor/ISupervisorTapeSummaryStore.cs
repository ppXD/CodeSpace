using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The rolling decision-tape digest per supervisor run (P1.2 auto-compact): the decider WRITES it after folding the
/// oldest decisions on a context overflow; the rehydrate READS it so every later turn's prompt renders
/// [digest + recent tail] instead of the whole tape. One row per run, re-compacted forward (upsert).
/// </summary>
public interface ISupervisorTapeSummaryStore
{
    /// <summary>The run's rolling digest, or null when nothing was compacted yet.</summary>
    Task<SupervisorTapeSummary?> GetAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Write / advance the run's rolling digest. Idempotent per (run, upToSequence); a re-compaction moves <paramref name="upToSequence"/> FORWARD only (a stale writer's lower sequence is ignored — the newer digest already covers it).</summary>
    Task UpsertAsync(Guid supervisorRunId, Guid teamId, long upToSequence, string summary, CancellationToken cancellationToken);
}
