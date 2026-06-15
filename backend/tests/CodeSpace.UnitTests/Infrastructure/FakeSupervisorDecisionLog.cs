using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.UnitTests.Infrastructure;

/// <summary>
/// The SHARED in-memory <see cref="ISupervisorDecisionLog"/> the supervisor unit tests drive the real
/// <see cref="SupervisorTurnService"/> / bounds / governance pipeline over (replaces the two divergent
/// per-file FakeLedgers — Rule 12.5: the duplication is ELIMINATED, not drift-detected). It faithfully models
/// the E1 invariants the turn loop relies on: a unique <c>(run, key)</c> index (a duplicate claim returns the
/// prior terminal / in-flight, never a 2nd row), a single-winner Pending → Running execution claim, and a
/// status-guarded terminal record. <c>Sequence</c> is insertion order.
///
/// <para>CRUCIALLY every lifecycle transition DELEGATES to <see cref="SupervisorDecisionStateMachine"/> — the
/// SAME authority the real <c>SupervisorDecisionLog.TryBeginExecutionAsync</c> + <c>RecordTerminalAsync</c>
/// consult — rather than re-implementing "only Pending→Running" / "no legality check" inline. So the fake can
/// NEVER drift from production's legality rules: an illegal hop (e.g. Pending→Succeeded without the claim, or a
/// double-terminal) throws <see cref="SupervisorDecisionTransitionException"/> exactly as the real ledger does.
/// </para>
/// </summary>
public sealed class FakeSupervisorDecisionLog : ISupervisorDecisionLog
{
    public List<SupervisorDecisionRecord> Rows { get; } = new();
    private long _seq;

    public void SeedTerminal(Guid runId, Guid teamId, string kind, string payloadJson, string outcomeJson) =>
        Rows.Add(new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = ++_seq, DecisionKind = kind, IdempotencyKey = $"{kind}:{Rows.Count}", InputHash = "h", PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Succeeded, OutcomeJson = outcomeJson });

    public void SeedPending(Guid runId, Guid teamId, string kind, string payloadJson) =>
        Rows.Add(new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = ++_seq, DecisionKind = kind, IdempotencyKey = $"{kind}:{Rows.Count}", InputHash = "h", PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Pending });

    public Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken)
    {
        var existing = Rows.FirstOrDefault(r => r.SupervisorRunId == supervisorRunId && r.IdempotencyKey == idempotencyKey);

        if (existing != null)
            return Task.FromResult(SupervisorDecisionStateMachine.IsTerminal(existing.Status)
                ? SupervisorDecisionClaim.Duplicate(existing.Id, existing.Status, existing.OutcomeJson, existing.Error)
                : SupervisorDecisionClaim.InFlight(existing.Id));

        var row = new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = supervisorRunId, Sequence = ++_seq, DecisionKind = decisionKind, IdempotencyKey = idempotencyKey, InputHash = inputHash, PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Pending, FenceEpoch = fenceEpoch };
        Rows.Add(row);
        return Task.FromResult(SupervisorDecisionClaim.Proceed(row.Id));
    }

    // Single-winner Pending → Running CAS, gated by the REAL state machine's legality rule (vs the old fake's
    // hard-coded "only Pending→Running"). The loser of the race (already Running/terminal) affects 0 rows → false.
    public Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken)
    {
        var row = Rows.Single(r => r.Id == decisionId);

        if (!SupervisorDecisionStateMachine.IsLegalTransition(row.Status, SupervisorDecisionStatus.Running)) return Task.FromResult(false);

        row.Status = SupervisorDecisionStatus.Running;
        return Task.FromResult(true);
    }

    // Status-guarded Running → terminal, enforcing BOTH IsTerminal + IsLegalTransition exactly as the real
    // SupervisorDecisionLog.RecordTerminalAsync does (vs the old fake's NO legality check) — so an illegal /
    // out-of-order terminal throws here just like production, and the fake can't mask a drift.
    public Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken)
    {
        if (!SupervisorDecisionStateMachine.IsTerminal(status))
            throw new SupervisorDecisionTransitionException($"SupervisorDecision terminal status must be terminal — got {status}.");

        var row = Rows.SingleOrDefault(r => r.Id == decisionId && r.TeamId == teamId)
            ?? throw new SupervisorDecisionTransitionException($"SupervisorDecision {decisionId} not found.");

        if (!SupervisorDecisionStateMachine.IsLegalTransition(row.Status, status))
            throw new SupervisorDecisionTransitionException($"Illegal SupervisorDecision transition {row.Status} → {status} (decision {decisionId}).");

        row.Status = status;
        row.OutcomeJson = outcomeJson;
        row.Error = error;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupervisorDecisionRecord>>(Rows.Where(r => r.SupervisorRunId == supervisorRunId && r.TeamId == teamId).OrderBy(r => r.Sequence).ToList());

    public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken)
    {
        var row = Rows.SingleOrDefault(r => r.Id == decisionId && r.TeamId == teamId);
        if (row != null) row.OutcomeJson = foldedOutcomeJson;
        return Task.CompletedTask;
    }

    public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => Task.FromResult(0);
}
