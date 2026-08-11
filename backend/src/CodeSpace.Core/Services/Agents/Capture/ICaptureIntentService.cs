using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The capture-intent saga (P2 durable capture, slice 1) — the ToolCallLedger discipline applied to the capture
/// window: a durable promise INSERTed when the harness exits (idempotent per attempt — a crash replay re-opens
/// onto the same row), a status-guarded CAS commit once the capture sequence persisted its facts, and honest
/// INDETERMINATE marking for attempts that died inside the window (recovery paths + a terminal-run reaper).
/// Every capture step today is individually best-effort-swallowed and the spool recovery terminalizes with no
/// capture at all — this seam is what makes a lost capture VISIBLE instead of a silent Succeeded.
/// </summary>
public interface ICaptureIntentService
{
    /// <summary>Open THIS attempt's promise (idempotent per <c>(agentRunId, fenceEpoch)</c> — a crash replay lands on the existing row, never a duplicate).</summary>
    Task OpenAsync(Guid agentRunId, Guid teamId, Guid? workflowRunId, long fenceEpoch, string? expectationsJson, CancellationToken cancellationToken);

    /// <summary>Commit THIS attempt's promise: CAS Intended → Committed for exactly the epoch that opened it, recording the observed facts (including a CONFIRMED empty). False = no open promise for this attempt (a reclaimed epoch, an already-settled row) — the caller logs, never throws.</summary>
    Task<bool> CommitAsync(Guid agentRunId, long fenceEpoch, string factsJson, CancellationToken cancellationToken);

    /// <summary>Recovery: every still-Intended promise of this run becomes Indeterminate — the attempt died inside the capture window, so its side effects may or may not have run (outcome unknown). Returns the number marked.</summary>
    Task<int> MarkIndeterminateForRunAsync(Guid agentRunId, CancellationToken cancellationToken);

    /// <summary>The safety-net reaper: Intended promises whose run is already TERMINAL (any ordering recovery missed) become Indeterminate, batched. Returns the number marked.</summary>
    Task<int> SweepDanglingForTerminalRunsAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed class CaptureIntentService : ICaptureIntentService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<CaptureIntentService> _logger;

    public CaptureIntentService(CodeSpaceDbContext db, ILogger<CaptureIntentService> logger) { _db = db; _logger = logger; }

    public async Task OpenAsync(Guid agentRunId, Guid teamId, Guid? workflowRunId, long fenceEpoch, string? expectationsJson, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        _db.CaptureIntent.Add(new CaptureIntent
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AgentRunId = agentRunId,
            WorkflowRunId = workflowRunId,
            FenceEpoch = fenceEpoch,
            Status = CaptureIntentStatus.Intended,
            ExpectationsJson = expectationsJson,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A crash replay of the same attempt — the promise already exists; the settled/committed state stands.
            _db.ChangeTracker.Clear();
        }
    }

    public async Task<bool> CommitAsync(Guid agentRunId, long fenceEpoch, string factsJson, CancellationToken cancellationToken)
    {
        var committed = await _db.CaptureIntent
            .Where(i => i.AgentRunId == agentRunId && i.FenceEpoch == fenceEpoch && i.Status == CaptureIntentStatus.Intended)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, CaptureIntentStatus.Committed)
                .SetProperty(i => i.FactsJson, factsJson)
                .SetProperty(i => i.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (committed == 0)
            _logger.LogWarning("Capture intent commit found no open promise for run {RunId} at epoch {Epoch} — a reclaim or a prior settle won; the capture facts stay on the result row", agentRunId, fenceEpoch);

        return committed > 0;
    }

    public async Task<int> MarkIndeterminateForRunAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var marked = await _db.CaptureIntent
            .Where(i => i.AgentRunId == agentRunId && i.Status == CaptureIntentStatus.Intended)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, CaptureIntentStatus.Indeterminate)
                .SetProperty(i => i.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (marked > 0)
            _logger.LogWarning("Marked {Count} capture promise(s) INDETERMINATE for run {RunId} — the attempt died inside the capture window; its side effects may or may not have run (outcome unknown)", marked, agentRunId);

        return marked;
    }

    public async Task<int> SweepDanglingForTerminalRunsAsync(int batchSize, CancellationToken cancellationToken)
    {
        // Candidate set (bounded, mirrors ExpireStaleToolCallsAsync): Intended promises whose run already landed
        // terminal — an ordering recovery missed (e.g. a completion that crashed between the run CAS and nothing,
        // or an abandon path predating this seam).
        var candidates = await _db.CaptureIntent.AsNoTracking()
            .Where(i => i.Status == CaptureIntentStatus.Intended
                        && _db.AgentRun.Any(r => r.Id == i.AgentRunId
                                                 && r.Status != AgentRunStatus.Queued && r.Status != AgentRunStatus.Running))
            .OrderBy(i => i.CreatedDate)
            .Take(batchSize)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0) return 0;

        var marked = await _db.CaptureIntent
            .Where(i => candidates.Contains(i.Id) && i.Status == CaptureIntentStatus.Intended)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, CaptureIntentStatus.Indeterminate)
                .SetProperty(i => i.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (marked > 0)
            _logger.LogWarning("Capture-intent reaper marked {Count} dangling promise(s) INDETERMINATE (run already terminal — the capture window never resolved)", marked);

        return marked;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
