using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Owns the durable lifecycle of an agent run: create it (Queued), flip it Running, append normalized
/// live-log events, heartbeat for liveness, and land a terminal result. The one place that reads/writes
/// <see cref="AgentRun"/> + <see cref="AgentRunEvent"/>, so a node, a worker, a reconciler, and the API
/// all drive runs through the same guarded transitions — no status flip bypasses
/// <see cref="AgentRunStateMachine"/>, and the run's xmin token stops two workers double-flipping it.
/// </summary>
public interface IAgentRunService
{
    /// <summary>Persist a new run in <see cref="AgentRunStatus.Queued"/> with <paramref name="task"/> as its envelope. workflowRunId/nodeId soft-link the spawning agent.code node (null for a standalone run).</summary>
    Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, CancellationToken cancellationToken);

    /// <summary>Queued → Running; stamps StartedAt + an initial heartbeat. Throws <see cref="AgentRunTransitionException"/> when the run isn't Queued.</summary>
    Task MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Refresh the liveness heartbeat a stuck-run reconciler reads. Idempotent; does not change status.</summary>
    Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Append one normalized event to the run's append-only log. Sequence + timestamp are DB-assigned.</summary>
    Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken);

    /// <summary>Land a terminal result (the target state is <paramref name="result"/>'s Status); stores the result + CompletedAt. Throws when the transition is illegal or the result's status isn't terminal.</summary>
    Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken);

    /// <summary>Read a run (untracked). Throws <see cref="KeyNotFoundException"/> when absent.</summary>
    Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Read the run's events with <c>Sequence &gt; afterSequence</c> in order — the live-stream / replay cursor (pass 0 for the whole log).</summary>
    Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken);
}

public sealed class AgentRunService : IAgentRunService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<AgentRunService> _logger;

    public AgentRunService(CodeSpaceDbContext db, ILogger<AgentRunService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, CancellationToken cancellationToken)
    {
        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            NodeId = nodeId,
            Harness = task.Harness,
            Status = AgentRunStatus.Queued,
            TaskJson = JsonSerializer.Serialize(task, AgentJson.Options),
        };

        _db.AgentRun.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent run created. RunId={RunId} Harness={Harness} TeamId={TeamId}", run.Id, run.Harness, teamId);

        return run;
    }

    public async Task MarkRunningAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        EnsureTransition(run, AgentRunStatus.Running);

        run.Status = AgentRunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.HeartbeatAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        run.HeartbeatAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken)
    {
        var record = new AgentRunEvent
        {
            Id = Guid.NewGuid(),
            AgentRunId = runId,
            Kind = @event.Kind,
            Text = @event.Text,
            DataJson = @event.Data?.GetRawText(),
        };

        _db.AgentRunEvent.Add(record);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return record;
    }

    public async Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken)
    {
        if (!AgentRunStateMachine.IsTerminal(result.Status))
            throw new AgentRunTransitionException($"AgentRunResult.Status must be terminal — got {result.Status}.");

        var run = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        EnsureTransition(run, result.Status);

        run.Status = result.Status;
        run.ResultJson = JsonSerializer.Serialize(result, AgentJson.Options);
        run.Error = result.Error;
        run.CompletedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent run completed. RunId={RunId} Status={Status}", runId, result.Status);
    }

    public async Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

    public async Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken) =>
        await _db.AgentRunEvent.AsNoTracking()
            .Where(e => e.AgentRunId == runId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<AgentRun> LoadAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.AgentRun.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

    private static void EnsureTransition(AgentRun run, AgentRunStatus to)
    {
        if (!AgentRunStateMachine.IsLegalTransition(run.Status, to))
            throw new AgentRunTransitionException($"Illegal AgentRun transition {run.Status} → {to} (run {run.Id}).");
    }
}

/// <summary>An agent run was asked to make a transition its lifecycle doesn't allow (completing a Queued run, re-running a terminal one, a non-terminal completion status, …). A node/handler surfaces this as a clean failure.</summary>
public sealed class AgentRunTransitionException : Exception
{
    public AgentRunTransitionException(string message) : base(message) { }
}
