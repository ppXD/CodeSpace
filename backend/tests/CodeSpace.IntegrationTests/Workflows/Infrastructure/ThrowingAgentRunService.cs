using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A FAULT-INJECTING <see cref="IAgentRunService"/> decorator for the supervisor crash-recovery test: it
/// delegates EVERY member to the real inner service, EXCEPT it THROWS on the N-th <see cref="CreateAsync"/> call
/// (1-based). Registered over the real service in a child scope (last-wins) so the supervisor executor's spawn
/// fan-out stages the leading agents FOR REAL (each <c>CreateAsync</c> commits its own row) and then crashes
/// MID-LOOP — exactly the durable residue a process crash leaves: one or more committed orphan agents, NO waits
/// flushed (the single SaveChanges at the end of the staging loop never ran), and the spawn decision stuck
/// Running (the Pending→Running claim hop already flipped it before the side effect). No production change — the
/// fault is injected purely through this test-only decorator + DI override.
/// </summary>
public sealed class ThrowingAgentRunService : IAgentRunService
{
    private readonly IAgentRunService _inner;
    private readonly int _throwOnCall;
    private int _calls;

    public ThrowingAgentRunService(IAgentRunService inner, int throwOnCall) { _inner = inner; _throwOnCall = throwOnCall; }

    /// <summary>The message the injected fault carries — asserted by the test so the throw can't be confused with a real failure.</summary>
    public const string FaultMessage = "injected fault: simulated crash mid spawn fan-out";

    public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default)
    {
        if (++_calls == _throwOnCall) throw new InvalidOperationException(FaultMessage);

        return _inner.CreateAsync(task, teamId, workflowRunId, nodeId, iterationKey, cancellationToken);
    }

    public Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) => _inner.MarkRunningAsync(runId, cancellationToken);

    public Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken) => _inner.HeartbeatAsync(runId, cancellationToken);

    public Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken) => _inner.ReclaimForReattachAsync(runId, cancellationToken);

    public Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken) => _inner.SetRunnerHandleAsync(runId, handleJson, cancellationToken);

    public Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken) => _inner.AppendEventAsync(runId, @event, cancellationToken);

    public Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken) => _inner.AppendEventsAsync(runId, events, cancellationToken);

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
