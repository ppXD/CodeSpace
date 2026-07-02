using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A COUNTING + optionally FAULT-INJECTING <see cref="IAgentRunService"/> decorator for the D1 batched-event-write
/// tests. It delegates EVERY member to the real inner service, while:
///   • COUNTING (always, thread-safe): how many times <see cref="AppendEventsAsync"/> (the batched path) and
///     <see cref="AppendEventAsync"/> (the single-row path) were called, and the total events appended. This pins
///     the D1 perf thesis — "one batched INSERT per checkpoint, not one per line" — which no behavioural test
///     covers; a regression that flushes per-line or bypasses the buffer passes every ORDER/scale test while
///     silently destroying the round-trip reduction.
///   • THROWING (when <see cref="ThrowOnAppendEventsCall"/> &gt; 0): on the Nth <see cref="AppendEventsAsync"/>
///     call, simulating a DB/flush failure at a spool checkpoint. Because the executor's CheckpointHandleOffset
///     awaits FlushAsync (→ AppendEventsAsync) BEFORE persisting the advanced StdoutOffset, a throw here proves
///     the flush-before-offset invariant: the durable offset must NOT advance past events that never landed.
/// No production change — this is a test-only decorator, registered over the real service via the hand-built
/// AgentRunExecutor ctor (passed as its FIRST arg) so the BufferedEventWriter writes through it. Mirrors the
/// established <see cref="ThrowingAgentRunService"/> delegation pattern.
/// </summary>
public sealed class InstrumentedAgentRunService : IAgentRunService
{
    private readonly IAgentRunService _inner;
    private int _batchedCalls;
    private int _perEventCalls;
    private long _totalEvents;

    public InstrumentedAgentRunService(IAgentRunService inner) { _inner = inner; }

    /// <summary>The message the injected fault carries — asserted so the throw can't be confused with a real DB failure.</summary>
    public const string AppendEventsFaultMessage = "injected fault: simulated DB failure flushing a batched agent-event write";

    /// <summary>When &gt; 0, the Nth (1-based) AppendEventsAsync call throws <see cref="AppendEventsFaultMessage"/>. 0 = never throw.</summary>
    public int ThrowOnAppendEventsCall { get; set; }

    /// <summary>How many times the BATCHED append path was invoked (one per flush — checkpoint, cap, or final).</summary>
    public int BatchedCalls => Volatile.Read(ref _batchedCalls);

    /// <summary>How many times the SINGLE-ROW append path was invoked — must stay 0 on the durable streaming hot path.</summary>
    public int PerEventCalls => Volatile.Read(ref _perEventCalls);

    /// <summary>Total events appended via the batched path.</summary>
    public long TotalEvents => Volatile.Read(ref _totalEvents);

    public Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _batchedCalls);

        if (ThrowOnAppendEventsCall > 0 && call == ThrowOnAppendEventsCall)
            throw new InvalidOperationException(AppendEventsFaultMessage);

        Interlocked.Add(ref _totalEvents, events.Count);

        return _inner.AppendEventsAsync(runId, events, cancellationToken);
    }

    public Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _perEventCalls);

        return _inner.AppendEventAsync(runId, @event, cancellationToken);
    }

    // ── pure delegation ───────────────────────────────────────────────────────
    public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default) => _inner.CreateAsync(task, teamId, workflowRunId, nodeId, iterationKey, cancellationToken);

    public Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) => _inner.MarkRunningAsync(runId, cancellationToken);

    public Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken) => _inner.HeartbeatAsync(runId, cancellationToken);

    public Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken) => _inner.ReclaimForReattachAsync(runId, cancellationToken);

    public Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken) => _inner.SetRunnerHandleAsync(runId, handleJson, cancellationToken);

    public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => _inner.CompleteAsync(runId, result, cancellationToken);

    public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) => _inner.CompleteAsync(runId, result, expectedEpoch, cancellationToken);

    public Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken) => _inner.CancelQueuedAsync(runId, reason, cancellationToken);

    public Task<bool> CancelRunningAsync(Guid runId, string reason, CancellationToken cancellationToken) => _inner.CancelRunningAsync(runId, reason, cancellationToken);

    public Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) => _inner.GetAsync(runId, cancellationToken);

    public Task<ResumableSession?> FindResumableSessionAsync(Guid teamId, Guid? parentRunId, string nodeId, string iterationKey, CancellationToken cancellationToken) => _inner.FindResumableSessionAsync(teamId, parentRunId, nodeId, iterationKey, cancellationToken);

    public Task<ResumableSession?> FindResumableSubtaskAttemptAsync(Guid teamId, Guid supervisorRunId, string subtaskId, CancellationToken cancellationToken) => _inner.FindResumableSubtaskAttemptAsync(teamId, supervisorRunId, subtaskId, cancellationToken);

    public Task<AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => _inner.GetSummaryForTeamAsync(runId, teamId, cancellationToken);

    public Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken) => _inner.GetEventsAsync(runId, teamId, afterSequence, cancellationToken);
}
